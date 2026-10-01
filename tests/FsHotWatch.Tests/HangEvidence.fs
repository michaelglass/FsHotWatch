/// Evidence for a test whose bounded wait on real child processes expired.
///
/// A fixture that drives real `dotnet` children (restore, build, a nested test run) and
/// waits a few minutes for them loses everything when the wait expires: the test throws,
/// `withTempDir` deletes the worktree, and with it the children's own logs. What the
/// stuck child was doing is then unknowable, and the failure reads as "timed out" and
/// nothing more. `onExpiry` captures, before the timeout propagates, what is needed to
/// answer that: this process's child tree, each child's stacks (`sample` on macOS for
/// native frames, `createdump --triage` for managed ones), and the logs the fixture
/// names, into `.fshw/diagnostics/` of the repository under test, which outlives the
/// temp directory. The timeout's message then names where the evidence is.
///
/// Capture is best-effort and never masks the timeout: a step that fails is recorded in
/// `capture-errors.txt` and the rest go on.
module FsHotWatch.Tests.HangEvidence

open System
open System.IO
open System.Runtime.InteropServices
open FsHotWatch

/// Runs `command args` to completion and returns its output, or why it did not.
type Runner = string -> string -> Result<string, string>

/// At most this many children get stacks taken: a stuck tree is a handful of processes,
/// and a dump each is not free.
[<Literal>]
let MaxStacked = 6

/// `ps -A -o pid=,ppid=,etime=,stat=,command=` rows as (pid, ppid, the whole line).
let parsePs (text: string) : (int * int * string) list =
    text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.choose (fun line ->
        match line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries) with
        | [| pid; ppid; _ |] ->
            match Int32.TryParse pid, Int32.TryParse ppid with
            | (true, p), (true, pp) -> Some(p, pp, line.Trim())
            | _ -> None
        | _ -> None)
    |> Array.toList

/// Every descendant of `root` in `rows`, children before grandchildren.
let descendants (root: int) (rows: (int * int * string) list) : (int * string) list =
    let rec walk (frontier: int list) (seen: Set<int>) (acc: (int * string) list) =
        match frontier with
        | [] -> List.rev acc
        | parent :: rest ->
            let children =
                rows
                |> List.filter (fun (pid, ppid, _) -> ppid = parent && not (seen.Contains pid))

            let pids = children |> List.map (fun (pid, _, _) -> pid)

            walk
                (rest @ pids)
                (Set.union seen (Set.ofList pids))
                (List.rev (children |> List.map (fun (pid, _, line) -> pid, line)) @ acc)

    walk [ root ] (Set.singleton root) []

/// Whether a child's command line names a .NET process, which `createdump` can read.
let private isDotnet (line: string) =
    line.Contains "dotnet" || line.Contains ".dll" || line.Contains "testhost"

/// The `.log` files under `path` (or `path` itself when it is a file), each with the
/// name it is kept under.
let private keptFiles (path: string) : (string * string) list =
    if File.Exists path then
        [ path, Path.GetFileName path ]
    elif Directory.Exists path then
        Directory.EnumerateFiles(path, "*.log", SearchOption.AllDirectories)
        |> Seq.map (fun f -> f, Path.GetRelativePath(path, f).Replace('/', '_').Replace('\\', '_'))
        |> Seq.toList
    else
        []

/// Capture into `dir`: the child tree of `rootPid`, stacks for up to `MaxStacked` of
/// those children, and the `.log` files under each of `keep`. Returns the errors met,
/// also written to `capture-errors.txt`; children that exited before their stacks were
/// taken (`alive` says no) are listed in `capture-skips.txt` instead.
/// `captureTo` on a given platform: `onMac` takes native stacks with `sample`, which only
/// macOS has; every platform dumps .NET processes with `createdump`.
let captureToOn
    (onMac: bool)
    (run: Runner)
    (alive: int -> bool)
    (dir: string)
    (rootPid: int)
    (keep: string list)
    : string list =
    Directory.CreateDirectory dir |> ignore
    let errors = ResizeArray<string>()
    let skips = ResizeArray<string>()

    // A stack step that fails for a child no longer running lost a race the capture
    // expects (the tree holds other tests' short-lived children too): a skip, not an
    // error. One still running whose stacks could not be taken is a real failure.
    let stackStep (what: string) (pid: int) (result: Result<string, string>) =
        match result with
        | Ok _ -> ()
        | Error _ when not (alive pid) ->
            if not (skips.Contains $"%d{pid} exited before its stacks were taken") then
                skips.Add $"%d{pid} exited before its stacks were taken"
        | Error reason -> errors.Add $"%s{what} %d{pid}: %s{reason}"

    let attempt (what: string) (step: unit -> unit) =
        try
            step ()
        with ex ->
            errors.Add $"%s{what}: %s{ex.Message}"

    let tree =
        match run "ps" "-A -o pid=,ppid=,etime=,stat=,command=" with
        | Ok text ->
            descendants rootPid (parsePs text)
            |> List.filter (fun (_, line) -> not (line.Contains "ps -A -o pid="))
        | Error reason ->
            errors.Add $"ps: %s{reason}"
            []

    attempt "tree" (fun () -> File.WriteAllLines(Path.Combine(dir, "tree.txt"), tree |> List.map snd))

    let createdump =
        Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "createdump")

    for pid, line in List.truncate MaxStacked tree do
        let sampleFile = Path.Combine(dir, $"sample-%d{pid}.txt")
        let dumpFile = Path.Combine(dir, $"dump-%d{pid}.dmp")

        if onMac then
            stackStep "sample" pid (run "sample" $"%d{pid} 2 -file %s{sampleFile}")

        if isDotnet line then
            stackStep "createdump" pid (run createdump $"--triage -f %s{dumpFile} %d{pid}")

    for source in keep do
        for file, name in keptFiles source do
            attempt $"keep %s{file}" (fun () -> File.Copy(file, Path.Combine(dir, name), true))

    if errors.Count > 0 then
        File.WriteAllLines(Path.Combine(dir, "capture-errors.txt"), errors)

    if skips.Count > 0 then
        File.WriteAllLines(Path.Combine(dir, "capture-skips.txt"), skips)

    List.ofSeq errors

/// `captureToOn` this platform.
let captureTo (run: Runner) (alive: int -> bool) (dir: string) (rootPid: int) (keep: string list) : string list =
    captureToOn (RuntimeInformation.IsOSPlatform OSPlatform.OSX) run alive dir rootPid keep

/// The real runner: the command, bounded at 20 seconds.
let private realRunner: Runner =
    fun command args ->
        match
            ProcessHelper.runProcess
                command
                args
                (Path.GetTempPath())
                []
                (ProcessHelper.ProcessBounds.silent (TimeSpan.FromSeconds 20.0))
        with
        | ProcessHelper.Succeeded output -> Ok(ProcessHelper.ProcessOutput.text output)
        | other -> Error $"%A{other}"

/// Whether process `pid` is still running.
let private running (pid: int) =
    try
        use p = Diagnostics.Process.GetProcessById pid
        not p.HasExited
    with :? ArgumentException ->
        false

/// `.fshw/diagnostics/` of the repository the test binary was built from, or the temp
/// directory when it cannot be found.
let private diagnosticsHome () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            Path.GetTempPath()
        elif File.Exists(Path.Combine(dir.FullName, "FsHotWatch.slnx")) then
            Path.Combine(dir.FullName, ".fshw", "diagnostics")
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

/// Capture this process's child tree and `keep` under a fresh directory named for
/// `label`, and return that directory.
let capture (label: string) (keep: string list) : string =
    let dir =
        Path.Combine(diagnosticsHome (), $"""hang-%s{DateTime.UtcNow.ToString "yyyyMMddTHHmmss"}-%s{label}""")

    captureTo realRunner running dir Environment.ProcessId keep |> ignore
    dir

let rec private isTimeout (ex: exn) =
    match ex with
    | :? TimeoutException -> true
    | :? AggregateException as agg -> agg.InnerExceptions |> Seq.exists isTimeout
    | _ -> false

/// `onExpiry` with the capture supplied.
let onExpiryWith
    (captureWith: string -> string list -> string)
    (label: string)
    (keep: unit -> string list)
    (work: unit -> 'T)
    : 'T =
    try
        work ()
    with ex when isTimeout ex ->
        let dir = captureWith label (keep ())
        raise (TimeoutException($"%s{ex.Message} — evidence captured in %s{dir}", ex))

/// Run `work`; if it times out, capture evidence (`capture label (keep ())`) first and
/// rethrow as a `TimeoutException` naming where it is.
let onExpiry (label: string) (keep: unit -> string list) (work: unit -> 'T) : 'T = onExpiryWith capture label keep work
