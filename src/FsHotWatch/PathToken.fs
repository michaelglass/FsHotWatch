/// The checkout root as a token (AUTOMATION-1602 (b)): what makes a record filed in one
/// checkout of a repository readable in another checkout of the same tree.
///
/// `tokenize root` replaces the root with `Token` wherever it stands as a whole path prefix:
/// ordinally, with a separator or an option's punctuation before it, and a separator, that
/// punctuation or the end of the string after it. `/w/ab/x` is not under `/w/a`, nor is
/// `/v/w/a/x`, nor `/W/A/x`. `detokenize` rebinds the token to another root; the token is no
/// spelling a path can hold, so no boundary rule is needed there.
///
/// A root is portable only when it is spelled as the file system spells it
/// (`portableRoot`): two spellings of one tree would need two tokens, and a replay would then
/// have to know which one MSBuild had written.
module FsHotWatch.PathToken

open System
open System.IO
open System.Text

/// The token a root collapses to: the placeholder the check cache already uses
/// (`CheckCache.RepoRootPlaceholder`). `CachePathIdentity`'s grammar excludes `$`, `{` and
/// `}`, so no real path holds it.
[<Literal>]
let Token = "${repo}"

/// BUMP BY HAND for any change to `tokenize`, to what the portable options key reads, or to
/// what the shadow compares: it is a term of the portable request key, and the shadow's
/// trust count counts only lines that carry the current one.
[<Literal>]
let Version = "fshw-root-token-v1"

/// What may stand before a root: the start, an option's separator (`-r:`, `x=`, `a;b`, `a,b`),
/// a quote, a space or tab, an opening bracket, a response-file `@` or a `|`.
let private leftBoundary = ":=;,\"' \t([@|"

/// What may stand after a root: the end, a separator, or what may stand before one, or a
/// closing bracket.
let private rightBoundary = leftBoundary + ")]/"

/// `s` with every whole-prefix occurrence of `root` replaced by `Token`.
let tokenize (root: string) (s: string) : string =
    if String.IsNullOrEmpty root || String.IsNullOrEmpty s then
        s
    else
        let out = StringBuilder(s.Length)

        let rec scan (copiedTo: int) (searchFrom: int) =
            match s.IndexOf(root, searchFrom, StringComparison.Ordinal) with
            | -1 -> out.Append(s, copiedTo, s.Length - copiedTo) |> ignore
            | at ->
                let after = at + root.Length

                if
                    (at = 0 || leftBoundary.Contains s[at - 1])
                    && (after = s.Length || rightBoundary.Contains s[after])
                then
                    out.Append(s, copiedTo, at - copiedTo).Append(Token) |> ignore
                    scan after after
                else
                    scan copiedTo (at + 1)

        scan 0 0
        out.ToString()

/// `s` with `Token` rebound to `root`.
let detokenize (root: string) (s: string) : string =
    if isNull s then s else s.Replace(Token, root)

/// Whether `root` still occurs in `s` anywhere, as a tokenized string's would when it named
/// something under the root that `tokenize` could not rebind (`/w/a2` for `/w/a`).
let survives (root: string) (s: string) : bool =
    not (isNull s) && s.Contains(root, StringComparison.Ordinal)

/// Whether `s` holds `Token` already.
let holdsToken (s: string) : bool =
    not (isNull s) && s.Contains(Token, StringComparison.Ordinal)

/// The temporary directories: a path under one is not durable, so a record naming one is
/// never filed portably.
let private temporaryPrefixes =
    lazy
        ([ Path.GetTempPath(); "/tmp/"; "/private/tmp/"; "/var/folders/" ]
         |> List.filter (String.IsNullOrEmpty >> not)
         |> List.distinct)

/// The temporary directory `s` names, if any.
let temporaryIn (s: string) : string option =
    if isNull s then
        None
    else
        temporaryPrefixes.Value
        |> List.tryFind (fun prefix -> s.Contains(prefix, StringComparison.Ordinal))

/// A function naming the checkout nested in `root` that a tokenized string reaches into, if
/// any: a directory below `Token` on the way to the path named that holds its own `.jj` or
/// `.git`, as intelligence's `.workspaces/<name>` do. A record filed in the outer checkout
/// must never name the nested one's files as its own. Answers are remembered per directory.
let nestedCheckouts (root: string) : string -> string option =
    let known = Collections.Generic.Dictionary<string, bool>(StringComparer.Ordinal)

    let isCheckout (relative: string) =
        match known.TryGetValue relative with
        | true, answer -> answer
        | false, _ ->
            let dir = Path.Combine(root, relative)

            let answer =
                Directory.Exists(Path.Combine(dir, ".jj"))
                || Directory.Exists(Path.Combine(dir, ".git"))
                || File.Exists(Path.Combine(dir, ".git"))

            known[relative] <- answer
            answer

    let prefix = Token + "/"
    let pathEnd = leftBoundary + ")]"

    fun (s: string) ->
        let rec from (searchFrom: int) =
            match
                (if isNull s then
                     -1
                 else
                     s.IndexOf(prefix, searchFrom, StringComparison.Ordinal))
            with
            | -1 -> None
            | at ->
                let start = at + prefix.Length

                let stop =
                    match s.IndexOfAny(pathEnd.ToCharArray(), start) with
                    | -1 -> s.Length
                    | i -> i

                let segments =
                    s.Substring(start, stop - start).Split('/', StringSplitOptions.RemoveEmptyEntries)

                let nested =
                    segments
                    |> Array.scan (fun (path: string) segment -> if path = "" then segment else path + "/" + segment) ""
                    |> Array.skip 1
                    |> Array.tryFind (fun relative ->
                        not (relative.Split('/') |> Array.exists (fun seg -> seg = "." || seg = ".."))
                        && isCheckout relative)

                match nested with
                | Some relative -> Some(Path.Combine(root, relative))
                | None -> from start

        from 0

/// `root` when a record filed under it may be read under another root: macOS or Linux, its
/// full path with no trailing separator, at least two segments deep, and spelled exactly as
/// `RepositoryIdentity.canonicalize` spells it (no symlink on the way, the case on disk);
/// otherwise why not.
let portableRoot (root: string) : Result<string, string> =
    if OperatingSystem.IsWindows() then
        Error "portable records are not built for Windows paths"
    elif String.IsNullOrEmpty root then
        Error "the root is empty"
    elif Path.TrimEndingDirectorySeparator(Path.GetFullPath root) <> root then
        Error $"the root %s{root} is not spelled as its full path without a trailing separator"
    elif root.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2 then
        Error $"the root %s{root} is too near the file system's root"
    else
        match RepositoryIdentity.canonicalize root with
        | Ok canonical when canonical.Value = root -> Ok root
        | Ok canonical ->
            Error $"the root %s{root} is reached through a symlink or spelled in another case (%s{canonical.Value})"
        | Error e -> Error $"the root %s{root} cannot be canonicalized: %A{e}"
