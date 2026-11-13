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
        let warm = pipelineOn project (FsHotWatch.Daemon.Daemon.createChecker ())

        let cold = check project warm
        let coldOutputs = (CheckRecords.CheckOutputs.ofResult (Some root) cold).Value
        test <@ rawCodes cold = [] @>
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
        test <@ rawCodes fresh = [] @>

        test <@ not (CheckRecords.CheckOutputs.hasErrors recheckedOutputs) @>
        test <@ recheckedOutputs = freshOutputs @>
        test <@ recheckedOutputs = coldOutputs @>)
