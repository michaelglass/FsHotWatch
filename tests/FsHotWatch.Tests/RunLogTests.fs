module FsHotWatch.Tests.RunLogTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

// ---------------------------------------------------------------------------
// AUTOMATION-279 — `RunLog`: the streamed per-project test-run log.
//
// The defect it replaces was a message that told its reader the failure was
// visible "without the saved log" while nothing in the plugin ever saved one.
// So the two properties under test here are (a) bytes reach DISK as they are
// written, not at close, and (b) the value that a message quotes a path out of
// can only be constructed by code that actually opened the file.
// ---------------------------------------------------------------------------

/// Read a file that is still open for writing elsewhere.
let private readWhileOpen (path: string) =
    use fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
    use reader = new StreamReader(fs)
    reader.ReadToEnd()

[<Fact(Timeout = 15000)>]
let ``a run log is on disk BEFORE it is closed`` () =
    // AutoFlush, pinned. Without it these bytes sit in a StreamWriter buffer that
    // only `Close` empties — and the run this artifact exists for is precisely the
    // one where nothing gets to call `Close`. Reading before the close is the only
    // way to tell the two apart.
    withTempDir "runlog-flush" (fun dir ->
        let handle = RunLog.openFor dir "Some.Project"

        match handle.Ref with
        | RunLog.Ref.Written path ->
            handle.Write "first chunk"

            test <@ readWhileOpen path = "first chunk" @>

            handle.Write ", second chunk"
            test <@ readWhileOpen path = "first chunk, second chunk" @>

            handle.Close()
        | RunLog.Ref.Unavailable reason -> Assert.Fail $"expected an open log in a writable temp dir, got: %s{reason}")

[<Fact(Timeout = 15000)>]
let ``a run log lands beside that run's CTRF report, named for the project`` () =
    // Same directory as `<Project>.ctrf.json` so a run's evidence is in one place,
    // and NOT the bare `.log` extension — that names the dead flat layout that
    // `Ctrf.tidyRunsDir` purges from the top of `test-runs/`.
    withTempDir "runlog-naming" (fun dir ->
        let runDir = Ctrf.runDir dir (Guid.NewGuid())
        let handle = RunLog.openFor runDir "Intelligence.Tests.Integration"
        handle.Close()

        match handle.Ref with
        | RunLog.Ref.Written path ->
            test <@ Path.GetDirectoryName path = runDir @>
            test <@ Path.GetFileName path = "Intelligence.Tests.Integration.output.log" @>
            test <@ RunLog.Suffix <> ".log" @>
        | RunLog.Ref.Unavailable reason -> Assert.Fail $"expected an open log, got: %s{reason}")

[<Fact(Timeout = 15000)>]
let ``openFor creates the run directory rather than failing on its absence`` () =
    withTempDir "runlog-mkdir" (fun dir ->
        let runDir = Path.Combine(dir, "not", "yet", "there")
        test <@ not (Directory.Exists runDir) @>

        let handle = RunLog.openFor runDir "Proj"
        handle.Write "hello"
        handle.Close()

        match handle.Ref with
        | RunLog.Ref.Written path -> test <@ File.ReadAllText path = "hello" @>
        | RunLog.Ref.Unavailable reason -> Assert.Fail $"expected the dir to be created, got: %s{reason}")

[<Fact(Timeout = 15000)>]
let ``a log that could NOT be opened says so, and names no path`` () =
    // THE invariant. A `Ref.Written` is only ever constructed by the code holding
    // the open handle, so a path in a diagnostic is a path that exists. When the
    // open fails, the caller gets a reason to state instead — never a plausible
    // path to a file nobody wrote, and never silence.
    withTempDir "runlog-blocked" (fun dir ->
        // A regular FILE where the run directory should be: CreateDirectory on it
        // fails, so the open cannot succeed.
        let blocked = Path.Combine(dir, "occupied")
        File.WriteAllText(blocked, "I am a file, not a directory")

        let handle = RunLog.openFor blocked "Proj"

        match handle.Ref with
        | RunLog.Ref.Unavailable reason ->
            test <@ reason.Contains "could not open" @>
            // Writing to a disabled handle is a no-op, not a crash: the caller does
            // not branch on this, it just runs the tests.
            handle.Write "swallowed"
            handle.Close()
            handle.Close()
        | RunLog.Ref.Written path -> Assert.Fail $"expected Unavailable when the run dir cannot exist, got %s{path}")

// --- isExpectedFileFailure: which throws are the box, and which are our bug ---

[<Theory(Timeout = 5000)>]
[<InlineData "io">]
[<InlineData "unauthorized">]
[<InlineData "notsupported">]
[<InlineData "argument">]
let ``the filesystem saying no is survivable`` (kind: string) =
    let ex: exn =
        match kind with
        | "io" -> IOException "No space left on device"
        | "unauthorized" -> UnauthorizedAccessException "Access to the path is denied"
        | "notsupported" -> NotSupportedException "The given path's format is not supported"
        | "argument" -> ArgumentException "Illegal characters in path"
        | other -> failwith $"unknown kind %s{other}"

    test <@ RunLog.isExpectedFileFailure ex @>

[<Fact(Timeout = 5000)>]
let ``a bug in fshw is NOT filed under 'your disk'`` () =
    // The arm that matters. Swallowing this would turn a null-deref in our own
    // code into the message "no output log was saved (…)" — a defect report
    // pointing at the operator's hardware.
    test <@ not (RunLog.isExpectedFileFailure (NullReferenceException())) @>
    test <@ not (RunLog.isExpectedFileFailure (InvalidOperationException())) @>
    test <@ not (RunLog.isExpectedFileFailure (exn "something we have never seen")) @>

[<Fact(Timeout = 15000)>]
let ``note swallows a throwing write — a note ABOUT a run cannot fail the run`` () =
    // Driven through a hand-built handle because the real failure (a disk that
    // fills between two writes) cannot be scheduled. The guard is the point: this
    // is called from inside the plugin's retry path.
    let handle: RunLog.Handle =
        { Ref = RunLog.Ref.Written "/nowhere/Proj.output.log"
          Write = fun _ -> raise (IOException "No space left on device")
          Close = ignore }

    RunLog.note handle "relaunching once"

[<Fact(Timeout = 15000)>]
let ``note marks fshw's own words so the rest of the file is verbatim child output`` () =
    withTempDir "runlog-note" (fun dir ->
        let handle = RunLog.openFor dir "Proj"
        handle.Write "child says this"
        RunLog.note handle "relaunching once"
        handle.Write "child says that"
        handle.Close()

        match handle.Ref with
        | RunLog.Ref.Written path ->
            let contents = File.ReadAllText path
            test <@ contents.Contains "[fshw] relaunching once" @>
            test <@ contents.Contains "child says this" @>
            test <@ contents.Contains "child says that" @>

            // Every line WITHOUT the marker is the child's, byte for byte.
            let ours =
                contents.Split('\n')
                |> Array.filter (fun l -> l.Contains "[fshw]")
                |> Array.length

            test <@ ours = 1 @>
        | RunLog.Ref.Unavailable reason -> Assert.Fail $"expected an open log, got: %s{reason}")

[<Fact(Timeout = 15000)>]
let ``Close is idempotent`` () =
    // It is called from a `finally` on a path that may also have closed already;
    // a second close must not throw out of an exception handler.
    withTempDir "runlog-close" (fun dir ->
        let handle = RunLog.openFor dir "Proj"
        handle.Write "x"
        handle.Close()
        handle.Close()
        handle.Close()

        match handle.Ref with
        | RunLog.Ref.Written path -> test <@ File.ReadAllText path = "x" @>
        | RunLog.Ref.Unavailable reason -> Assert.Fail $"expected an open log, got: %s{reason}")
