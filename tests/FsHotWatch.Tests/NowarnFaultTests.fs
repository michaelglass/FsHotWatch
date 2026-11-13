/// AUTOMATION-1664 (A): a long-lived TransparentCompiler checker that has seen a referenced
/// assembly's stamp move reports, from then on, a warning the checked file itself
/// `#nowarn`s; `--warnaserror+` makes it an error. A fresh checker of the same inputs
/// reports nothing. What a `check` record files must not depend on which of the two
/// answered: its key names the content and the options, never the checker's history.
module FsHotWatch.Tests.NowarnFaultTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// An interface with a static abstract member (FS3535 to declare, `#nowarn`ed here).
let private declaringText =
    "module A\n#nowarn \"3535\"\ntype IFoo =\n    static abstract Make: unit -> IFoo\n"

/// Uses it as a type, which is FS3536, and says so with its own `#nowarn`.
let private usingText =
    "module B\n\n#nowarn \"3536\" // IFoo used as a type on purpose\n\nopen A\n\nlet keep (x: IFoo) = x\n"

type private Project =
    { Root: string
      Using: string
      Extra: string
      Options: FSharpProjectOptions }

/// Two files, warnings as errors, and one in-repo `-r:` assembly the code never uses:
/// moving its bytes moves the reference's stamp and nothing else.
let private projectIn (root: string) =
    let path name = Path.Combine(root, name)
    File.WriteAllText(path "A.fs", declaringText)
    File.WriteAllText(path "B.fs", usingText)
    let extra = Path.Combine(root, "lib", "Extra.dll")
    Directory.CreateDirectory(Path.GetDirectoryName extra) |> ignore
    File.Copy(typeof<FactAttribute>.Assembly.Location, extra, true)
    let baseOptions, sdk = sdkProjectOptions.Value

    { Root = root
      Using = path "B.fs"
      Extra = extra
      Options =
        { baseOptions with
            ProjectFileName = path "P.fsproj"
            SourceFiles = [| path "A.fs"; path "B.fs" |]
            OtherOptions = Array.append sdk [| "--warnaserror+"; $"-r:%s{extra}" |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None } }

let private pipelineOn (project: Project) (checker: FSharpChecker) =
    let pipeline = CheckPipeline(checker, repoRoot = project.Root)
    pipeline.RegisterProject(project.Options.ProjectFileName, project.Options)
    pipeline

/// A pipeline whose "fresh" checker is the warm one: the faulted answer is delivered as
/// the warm checker gave it, so what the record makes of it can be seen.
/// A pipeline that checks a faulted answer again on a fresh checker, as the daemon's does.
let private recheckingOn (project: Project) (checker: FSharpChecker) =
    let pipeline =
        CheckPipeline(checker, repoRoot = project.Root, freshChecker = FsHotWatch.Daemon.Daemon.freshCheckerForFault)

    pipeline.RegisterProject(project.Options.ProjectFileName, project.Options)
    pipeline

let private keepingFaultOn (project: Project) (checker: FSharpChecker) =
    let pipeline =
        CheckPipeline(checker, repoRoot = project.Root, freshChecker = (fun () -> checker))

    pipeline.RegisterProject(project.Options.ProjectFileName, project.Options)
    pipeline

let private check (project: Project) (pipeline: CheckPipeline) =
    match pipeline.CheckFile(AbsFilePath.create project.Using) |> Async.RunSynchronously with
    | Some result -> result
    | None -> failwith "no check result"

let private rawCodes (result: FileCheckResult) =
    match result.CheckResults with
    | FullCheck(_, results) ->
        results.Diagnostics
        |> Array.map (fun d -> d.Severity, d.ErrorNumber)
        |> List.ofArray
    | _ -> failwith "not a full check"

/// Move the in-repo reference's bytes, so its content stamp moves.
let private moveExtra (project: Project) =
    File.Copy(typeof<Swensen.Unquote.AssertionFailedException>.Assembly.Location, project.Extra, true)

// Three real checks: cold, after the stamp moved, and on a fresh checker.
[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a re-check in a warm checker after a reference's stamp moved files the record a fresh checker files`` () =
    withTempDir "nowarn-fault-record" (fun root ->
        let project = projectIn root
        let warmChecker = FsHotWatch.Daemon.Daemon.createChecker ()
        let warm = keepingFaultOn project warmChecker

        let cold = check project warm
        let coldOutputs = (CheckRecords.CheckOutputs.ofResult (Some root) cold).Value
        test <@ List.isEmpty (rawCodes cold) @>
        test <@ not (CheckRecords.CheckOutputs.hasErrors coldOutputs) @>

        moveExtra project
        let rechecked = check project warm

        let recheckedOutputs =
            (CheckRecords.CheckOutputs.ofResult (Some root) rechecked).Value

        let fresh =
            check project (pipelineOn project (FsHotWatch.Daemon.Daemon.createChecker ()))

        let freshOutputs = (CheckRecords.CheckOutputs.ofResult (Some root) fresh).Value

        // The control: this FCS reproduces the fault, so the comparison below is not vacuous.
        test <@ rawCodes rechecked = [ FSharpDiagnosticSeverity.Error, 3536 ] @>
        test <@ List.isEmpty (rawCodes fresh) @>

        test <@ not (CheckRecords.CheckOutputs.hasErrors recheckedOutputs) @>
        test <@ recheckedOutputs = freshOutputs @>
        test <@ recheckedOutputs = coldOutputs @>

        // A pipeline with no fresh checker keeps the warm answer, and files it the same.
        let kept = check project (pipelineOn project warmChecker)
        test <@ rawCodes kept = [ FSharpDiagnosticSeverity.Error, 3536 ] @>
        test <@ (CheckRecords.CheckOutputs.ofResult (Some root) kept).Value = freshOutputs @>)

// Three real checks: cold, the faulted answer, and its re-check on a fresh checker.
[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a live check reporting a code its own file nowarns is re-checked on a fresh checker, and says so`` () =
    withTempDir "nowarn-fault-recheck" (fun root ->
        let project = projectIn root
        let warm = recheckingOn project (FsHotWatch.Daemon.Daemon.createChecker ())
        let lines = Collections.Concurrent.ConcurrentQueue<string>()

        use _ =
            Logging.installSink
                { Write = lines.Enqueue
                  Level = Logging.LogLevel.Debug
                  File = None }

        test <@ List.isEmpty (rawCodes (check project warm)) @>
        test <@ lines |> Seq.forall (fun line -> not (line.Contains "#nowarn")) @>

        moveExtra project
        let rechecked = check project warm

        // The warm checker's answer named FS3536 (the control in the test above); the one
        // delivered is the fresh checker's.
        test <@ List.isEmpty (rawCodes rechecked) @>

        let named =
            lines
            |> Seq.filter (fun line -> line.Contains "FS3536" && line.Contains "B.fs" && line.Contains "fresh checker")
            |> List.ofSeq

        test <@ named.Length = 1 @>)

[<Fact>]
let ``a fault is a code the real compiler would have silenced, and only that`` () =
    let own (flags: string list) (source: string) =
        FcsSuppression.Suppression.forFile Set.empty flags source

    let faults flags source diagnostics =
        FcsDiagnosticFilter.nowarnFaultCodes (own flags source) diagnostics

    let nowarned = "module B\n#nowarn \"3536\" \"1182\"\n"

    // A #nowarned warning, and one --warnaserror promoted: both faults, each named once.
    test <@ faults [] nowarned [ 3536, false; 3536, false ] = [ 3536 ] @>
    test <@ faults [ "--warnaserror+" ] nowarned [ 1182, true; 3536, true ] = [ 1182; 3536 ] @>
    test <@ faults [ "--warnaserror:3536" ] nowarned [ 3536, true ] = [ 3536 ] @>

    // An error in its own right is the compiler's to report, #nowarn or not; a code the
    // file does not name, and an answer with nothing to silence, are no fault.
    test <@ List.isEmpty (faults [] nowarned [ 3536, true ]) @>
    test <@ List.isEmpty (faults [ "--warnaserror+" ] nowarned [ 1, true; 64, false ]) @>
    test <@ List.isEmpty (faults [ "--warnaserror+" ] "module B\n" [ 3536, true ]) @>

    // The host's configured codes are not the file's: they never make a fault.
    let configured =
        FcsSuppression.Suppression.forFile (Set.ofList [ 3536 ]) [ "--warnaserror+" ] "module B\n"

    test <@ List.isEmpty (FcsDiagnosticFilter.nowarnFaultCodes configured [ 3536, true ]) @>

[<Fact>]
let ``the fault's log lines name the file and every code`` () =
    let line =
        FcsDiagnosticFilter.nowarnFaultLogLine "App.fsproj" "/w/src/Program.fs" [ 1182; 3536 ]

    test <@ line.Contains "/w/src/Program.fs" && line.Contains "FS1182, FS3536" @>
    test <@ line.Contains "fresh checker" && line.Contains "App.fsproj" @>

    let survived =
        FcsDiagnosticFilter.nowarnSurvivedLogLine "App.fsproj" "/w/src/Program.fs" [ 3536 ]

    test <@ survived.Contains "/w/src/Program.fs" && survived.Contains "FS3536" @>

    let kept =
        FcsDiagnosticFilter.nowarnKeptLogLine "App.fsproj" "/w/src/Program.fs" [ 3536 ]

    test
        <@
            kept.Contains "/w/src/Program.fs"
            && kept.Contains "FS3536"
            && kept.Contains "kept"
        @>
