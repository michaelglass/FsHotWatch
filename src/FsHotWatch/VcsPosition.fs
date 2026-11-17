/// Where a checkout stands in its version control, read between change batches so a warm
/// daemon can tell an edit from a VCS operation that rewrote its tree (AUTOMATION-1705).
///
/// Read from the files the VCS writes, never by running `jj` or `git`: a `jj` command
/// run from here would itself snapshot the working copy and record an operation.
///
/// - jj: the operation that last updated THIS workspace's working copy, from
///   `.jj/working_copy/checkout`. Not the repository's op head: every workspace of a
///   repository shares that, so another agent's `jj rebase` in its own workspace would
///   move it. An operation that only snapshotted the working copy recorded edits already
///   on disk and wrote no file, so moving to one is an edit.
/// - git: `HEAD`, and the commit the branch it names points at (loose or packed).
module FsHotWatch.VcsPosition

open System
open System.IO
open FsHotWatch.RepositoryIdentity

/// What the operation that last updated a jj working copy did to it.
[<RequireQualifiedAccess>]
type JjOperationKind =
    /// `snapshot working copy`: jj recorded edits already on disk. It wrote no file.
    | Snapshot
    /// Anything else (rebase, new, edit, undo, restore, ...): it may have rewritten files.
    | Rewrite

/// What a git branch points at.
[<RequireQualifiedAccess>]
type GitRef =
    | Points of commit: string
    /// A branch with no commit yet (a fresh `git init`).
    | Unborn

/// A checkout's position in its VCS.
[<RequireQualifiedAccess>]
type Position =
    /// The jj operation that last updated this workspace's working copy.
    | JjWorkingCopy of operation: string * kind: JjOperationKind
    /// git `HEAD` naming a commit directly.
    | GitDetached of commit: string
    /// git `HEAD` naming a branch, and what that branch points at.
    | GitBranch of name: string * target: GitRef

module Position =
    let private short (id: string) = id.Substring(0, min 12 id.Length)

    /// How a log line names a position.
    let describe (position: Position) : string =
        match position with
        | Position.JjWorkingCopy(op, JjOperationKind.Rewrite) -> $"jj op {short op}"
        | Position.JjWorkingCopy(op, JjOperationKind.Snapshot) -> $"jj op {short op} (snapshot)"
        | Position.GitDetached commit -> $"git HEAD {short commit} (detached)"
        | Position.GitBranch(name, GitRef.Points commit) -> $"git {name} at {short commit}"
        | Position.GitBranch(name, GitRef.Unborn) -> $"git {name} (unborn)"

/// Why a change batch happened, read from the position before it and after it.
[<RequireQualifiedAccess>]
type BatchOrigin =
    /// Somebody edited files. `now` is the position to compare the next batch with.
    | Edit of now: Position
    /// A VCS operation moved the checkout between the two batches.
    | VcsOperation of before: Position * after: Position

/// Classify a batch by the position before it and the position now.
let classify (before: Position) (after: Position) : BatchOrigin =
    if before = after then
        BatchOrigin.Edit after
    else
        match after with
        | Position.JjWorkingCopy(_, JjOperationKind.Snapshot) -> BatchOrigin.Edit after
        | Position.JjWorkingCopy(_, JjOperationKind.Rewrite)
        | Position.GitDetached _
        | Position.GitBranch _ -> BatchOrigin.VcsOperation(before, after)

/// Whether a checkout has a position to read, decided once when the daemon starts.
[<RequireQualifiedAccess; NoComparison; NoEquality>]
type Probe =
    /// No VCS this reads (a plain directory, or a layout that does not resolve): every
    /// batch is an edit.
    | Untracked of reason: string
    /// Reads the position now. An `Error` says which file could not be read.
    | Tracked of read: (unit -> Result<Position, string>)

// ---- protobuf: just enough to read jj's records --------------------------------------

/// A protobuf field's value, for the two wire types jj's records use here.
[<RequireQualifiedAccess>]
type private Field =
    | Varint of uint64
    | Bytes of byte[]

let private varintAt (bytes: byte[]) (start: int) : Result<uint64 * int, string> =
    let rec go index shift (acc: uint64) =
        if index >= bytes.Length then
            Error "truncated varint"
        else
            let b = bytes[index]
            let acc = acc ||| (uint64 (b &&& 0x7Fuy) <<< shift)

            if b &&& 0x80uy = 0uy then
                Ok(acc, index + 1)
            else
                go (index + 1) (shift + 7) acc

    go start 0 0UL

/// Every top-level field of a message, in order.
let private fieldsOf (bytes: byte[]) : Result<(int * Field) list, string> =
    let rec go index acc =
        if index >= bytes.Length then
            Ok(List.rev acc)
        else
            varintAt bytes index
            |> Result.bind (fun (key, next) ->
                let number = int (key >>> 3)

                match int (key &&& 7UL) with
                | 0 ->
                    varintAt bytes next
                    |> Result.map (fun (value, after) -> (number, Field.Varint value), after)
                | 2 ->
                    varintAt bytes next
                    |> Result.bind (fun (length, start) ->
                        if uint64 (bytes.Length - start) < length then
                            Error "truncated field"
                        else
                            Ok((number, Field.Bytes(Array.sub bytes start (int length))), start + int length))
                | wire -> Error $"unsupported wire type {wire}")
            |> Result.bind (fun (field, next) -> go next (field :: acc))

    go 0 []

let private bytesField (number: int) (fields: (int * Field) list) : byte[] option =
    fields
    |> List.tryPick (fun (n, field) ->
        match field with
        | Field.Bytes value when n = number -> Some value
        | Field.Bytes _
        | Field.Varint _ -> None)

let private varintField (number: int) (fields: (int * Field) list) : uint64 option =
    fields
    |> List.tryPick (fun (n, field) ->
        match field with
        | Field.Varint value when n = number -> Some value
        | Field.Varint _
        | Field.Bytes _ -> None)

let private readBytes (file: string) : Result<byte[], string> =
    // Whatever stops the read (missing, a directory, no permission, a bad path), an
    // unread file is not a position.
    try
        Ok(File.ReadAllBytes file)
    with ex ->
        Error $"cannot read {file}: {ex.Message}"

let private readText (file: string) : Result<string, string> =
    readBytes file
    |> Result.map (fun bytes -> Text.Encoding.UTF8.GetString(bytes).Trim())

// ---- jj ------------------------------------------------------------------------------

/// `.jj/working_copy/checkout` (jj's `Checkout` message): field 2 is the operation id.
let private checkoutOperation (workingCopy: string) : Result<string, string> =
    let file = Path.Combine(workingCopy, "checkout")

    readBytes file
    |> Result.bind (fun bytes -> fieldsOf bytes |> Result.mapError (fun reason -> $"{file}: {reason}"))
    |> Result.bind (fun fields ->
        match bytesField 2 fields with
        | Some op -> Ok(Convert.ToHexString(op).ToLowerInvariant())
        | None -> Error $"{file} holds no operation id")

/// An operation (jj's `Operation` message): field 3 is its metadata, whose field 7 is
/// `is_snapshot`. Absent reads as false, as proto3 reads it.
let private operationKind (operations: string) (op: string) : Result<JjOperationKind, string> =
    let file = Path.Combine(operations, op)

    readBytes file
    |> Result.bind (fun bytes -> fieldsOf bytes |> Result.mapError (fun reason -> $"{file}: {reason}"))
    |> Result.bind (fun fields ->
        bytesField 3 fields
        |> Option.map fieldsOf
        |> Option.defaultValue (Ok [])
        |> Result.mapError (fun reason -> $"{file} metadata: {reason}"))
    |> Result.map (fun metadata ->
        if varintField 7 metadata |> Option.exists (fun flag -> flag <> 0UL) then
            JjOperationKind.Snapshot
        else
            JjOperationKind.Rewrite)

let private readJj (root: string) () : Result<Position, string> =
    resolveWorktree root
    |> Result.mapError IdentityError.describe
    |> Result.bind (fun worktree ->
        let workingCopy = Path.Combine(worktree.Root.Value, ".jj", "working_copy")
        let operations = Path.Combine(worktree.Store.Path.Value, "op_store", "operations")

        checkoutOperation workingCopy
        |> Result.bind (fun op ->
            operationKind operations op
            |> Result.map (fun kind -> Position.JjWorkingCopy(op, kind))))

// ---- git -----------------------------------------------------------------------------

let private isCommitId (text: string) =
    text.Length >= 40 && text |> Seq.forall Uri.IsHexDigit

/// What `name` points at in the common directory: its loose file, else its line in
/// `packed-refs`, else nothing yet.
let private resolveRef (common: string) (name: string) : Result<GitRef, string> =
    let loose = Path.Combine(common, name)
    let packed = Path.Combine(common, "packed-refs")

    if File.Exists loose then
        readText loose |> Result.map GitRef.Points
    elif File.Exists packed then
        readText packed
        |> Result.map (fun text ->
            text.Split('\n')
            |> Array.tryPick (fun line ->
                let line = line.Trim()

                if line.EndsWith(" " + name, StringComparison.Ordinal) then
                    Some(GitRef.Points(line.Substring(0, line.Length - name.Length - 1)))
                else
                    None)
            |> Option.defaultValue GitRef.Unborn)
    else
        Ok GitRef.Unborn

let private readGit (root: string) () : Result<Position, string> =
    canonicalize root
    |> Result.bind (fun canonical ->
        gitDirOf canonical
        |> Option.defaultValue (Error(IdentityError.PathNotFound(Path.Combine(canonical.Value, ".git")))))
    |> Result.bind (fun (_, gitDir) -> gitCommonDir gitDir |> Result.map (fun common -> gitDir, common))
    |> Result.mapError IdentityError.describe
    |> Result.bind (fun (gitDir, common) ->
        let headFile = Path.Combine(gitDir.Value, "HEAD")

        readText headFile
        |> Result.bind (fun head ->
            if head.StartsWith("ref: ", StringComparison.Ordinal) then
                let name = head.Substring("ref: ".Length).Trim()

                resolveRef common.Value name
                |> Result.map (fun target -> Position.GitBranch(name, target))
            elif isCommitId head then
                Ok(Position.GitDetached head)
            else
                Error $"{headFile} names neither a ref nor a commit: \"{head}\""))

/// How to read the position of the checkout rooted at `root`. The layout is resolved
/// once to choose the reader; each read resolves it again, so a read after the layout
/// changed reports what it found rather than reading stale paths.
let probeFor (root: string) : Probe =
    match resolveWorktree root with
    | Error error -> Probe.Untracked(IdentityError.describe error)
    | Ok worktree ->
        match worktree.Kind with
        | CheckoutKind.JjDefaultWorkspace
        | CheckoutKind.JjSecondaryWorkspace -> Probe.Tracked(readJj root)
        | CheckoutKind.GitMainCheckout
        | CheckoutKind.GitWorktree -> Probe.Tracked(readGit root)
        | CheckoutKind.PlainDirectory -> Probe.Untracked(CheckoutKind.describe CheckoutKind.PlainDirectory)
