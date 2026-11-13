/// A child process's reads, by syscall tracing, for the read-set audit (ADR-059 §35 Change
/// 1b, AUTOMATION-1634; `ReadSetAudit`). Linux only: `strace -f` follows every process the
/// command starts. macOS has no unprivileged equivalent (`fs_usage` and `dtruss` need root,
/// and dtrace is fenced by SIP), so there the strace half of the audit runs only in CI
/// (ADR-059 §37).
///
/// What it does not see: a read made by a process the command did not start (a build
/// server it hands work to, an already-running daemon), a read through a file descriptor
/// inherited from outside, and a relative path in a process that changed directory before
/// strace saw a `chdir` of its own (a child starts from the command's directory here).
module FsHotWatch.Strace

open System
open System.IO
open System.Text
open FsHotWatch.ReadTrace

/// Set to `required` where strace must trace (the `read-set-audit` CI job): there a
/// missing strace fails the tests instead of skipping them.
[<Literal>]
let RequiredVariable = "FSHW_READ_SET_STRACE"

/// Whether strace is on PATH.
let available () : bool =
    OperatingSystem.IsLinux()
    && (Environment.GetEnvironmentVariable "PATH"
        |> Option.ofObj
        |> Option.defaultValue "")
        .Split(Path.PathSeparator)
       |> Array.exists (fun d -> d <> "" && File.Exists(Path.Combine(d, "strace")))

/// The quoted string starting at `text[start]` (which is `"`), unescaped, and the index
/// after its closing quote.
let private quoted (text: string) (start: int) : (string * int) option =
    let sb = StringBuilder()
    let mutable i = start + 1
    let mutable closed = false

    while not closed && i < text.Length do
        match text[i] with
        | '\\' when i + 1 < text.Length ->
            let next = text[i + 1]

            match next with
            | 'n' -> sb.Append '\n' |> ignore
            | 't' -> sb.Append '\t' |> ignore
            | c -> sb.Append c |> ignore

            i <- i + 2
        | '"' ->
            closed <- true
            i <- i + 1
        | c ->
            sb.Append c |> ignore
            i <- i + 1

    if closed then Some(sb.ToString(), i) else None

let private probes =
    set
        [ "stat"
          "lstat"
          "newfstatat"
          "fstatat64"
          "stat64"
          "lstat64"
          "statx"
          "access"
          "faccessat"
          "faccessat2"
          "readlink"
          "readlinkat"
          "getxattr"
          "lgetxattr"
          "listxattr"
          "llistxattr" ]

let private opens = set [ "open"; "openat"; "openat2"; "creat" ]
let private execs = set [ "execve"; "execveat" ]

let private dirfdFirst =
    set
        [ "openat"
          "openat2"
          "newfstatat"
          "fstatat64"
          "statx"
          "faccessat"
          "faccessat2"
          "readlinkat"
          "execveat" ]

/// One complete call: pid, name, the argument text and what it returned.
type private Call =
    { Pid: string
      Name: string
      Args: string
      Failed: bool }

let private splitPid (line: string) =
    let line = line.TrimStart()
    let space = line.IndexOf ' '

    if space > 0 && line.Substring(0, space) |> Seq.forall Char.IsDigit then
        line.Substring(0, space), line.Substring(space).TrimStart()
    else
        "", line

/// `name(args) = ret` → the call; None for a signal, an exit or anything unparsed.
let private ofComplete (pid: string) (body: string) : Call option =
    let paren = body.IndexOf '('
    let eq = body.LastIndexOf ") = "

    if paren <= 0 || eq < paren then
        None
    else
        let ret = body.Substring(eq + 4).TrimStart()

        Some
            { Pid = pid
              Name = body.Substring(0, paren)
              Args = body.Substring(paren + 1, eq - paren - 1)
              Failed = ret.StartsWith "-1" || ret.StartsWith "?" }

/// The reads in strace's output `lines` (`strace -f -y -e trace=%file`), relative paths
/// resolved against `workingDirectory` unless the process changed directory.
let parse (workingDirectory: string) (lines: string seq) : Read list =
    let pending = Collections.Generic.Dictionary<string, string>()
    let cwd = Collections.Generic.Dictionary<string, string>()
    let reads = Collections.Generic.List<Read>()

    let resolve (pid: string) (dirfd: string option) (path: string) =
        if Path.IsPathRooted path then
            path
        else
            let directory =
                match dirfd with
                | Some fd when fd.Contains '<' && fd.EndsWith ">" -> fd.Substring(fd.IndexOf '<' + 1).TrimEnd('>')
                | _ ->
                    match cwd.TryGetValue pid with
                    | true, d -> d
                    | _ -> workingDirectory

            Path.Combine(directory, path)

    let handle (call: Call) =
        let firstQuote = call.Args.IndexOf '"'

        if firstQuote >= 0 then
            match quoted call.Args firstQuote with
            | None -> ()
            | Some(path, after) ->
                let dirfd =
                    if dirfdFirst.Contains call.Name then
                        Some(call.Args.Substring(0, firstQuote).TrimEnd(',', ' '))
                    else
                        None

                let full () =
                    Path.GetFullPath(resolve call.Pid dirfd path)

                if call.Name = "chdir" then
                    if not call.Failed then
                        cwd[call.Pid] <- full ()
                elif opens.Contains call.Name then
                    let flags = call.Args.Substring after

                    let writeOnly = call.Name = "creat" || flags.Contains "O_WRONLY"

                    if call.Failed then
                        reads.Add
                            { Path = full ()
                              Kind = ReadKind.Probe }
                    elif not writeOnly then
                        let kind =
                            if flags.Contains "O_DIRECTORY" then
                                ReadKind.Probe
                            else
                                ReadKind.Open

                        reads.Add { Path = full (); Kind = kind }
                elif execs.Contains call.Name then
                    reads.Add
                        { Path = full ()
                          Kind = (if call.Failed then ReadKind.Probe else ReadKind.Open) }
                elif probes.Contains call.Name then
                    reads.Add
                        { Path = full ()
                          Kind = ReadKind.Probe }

    for raw in lines do
        let pid, body = splitPid raw

        if body.EndsWith " <unfinished ...>" then
            pending[pid] <- body.Substring(0, body.Length - " <unfinished ...>".Length)
        elif body.StartsWith "<... " then
            // `<... openat resumed>) = 4` completes the call this pid left unfinished.
            let close = body.IndexOf '>'

            match pending.TryGetValue pid with
            | true, start when close > 0 ->
                pending.Remove pid |> ignore
                ofComplete pid (start + body.Substring(close + 1)) |> Option.iter handle
            | _ -> ()
        else
            ofComplete pid body |> Option.iter handle

    List.ofSeq reads

/// A traced run: the command's exit code and every read it and its descendants made.
type Traced = { ExitCode: int; Reads: Read list }

/// Run `program args` in `workingDirectory` under `strace -f`, through `ProcessHelper` (so the
/// child is tracked and reaped), bounded by `timeout`, and parse what it read. Error when
/// strace timed out or wrote no trace.
let trace
    (timeout: TimeSpan)
    (workingDirectory: string)
    (program: string)
    (args: string list)
    : Result<Traced, string> =
    let output = Path.Combine(Path.GetTempPath(), $"fshw-strace-{Guid.NewGuid():N}.txt")

    let argText =
        [ "-f"; "-qq"; "-y"; "-e"; "trace=%file"; "-o"; output; "--"; program ] @ args
        |> List.map ProcessHelper.quoteArg
        |> String.concat " "

    try
        let exitCode =
            match
                ProcessHelper.runProcess
                    "strace"
                    argText
                    workingDirectory
                    []
                    (ProcessHelper.ProcessBounds.silent timeout)
            with
            | ProcessHelper.Succeeded _ -> Ok 0
            | ProcessHelper.Failed(code, _) -> Ok code
            | ProcessHelper.TimedOut(after, _, _) -> Error $"strace timed out after %O{after}"

        exitCode
        |> Result.bind (fun code ->
            if File.Exists output then
                Ok
                    { ExitCode = code
                      Reads = parse workingDirectory (File.ReadLines output) }
            else
                Error $"strace wrote no trace (exit %d{code})")
    finally
        if File.Exists output then
            File.Delete output
