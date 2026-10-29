/// The persisted check-result store (ADR-059 §14, AUTOMATION-1361 phase 1): a check
/// filed in one process is served to a later one only while every input it was typed
/// against is unchanged, and what is served equals what a forced re-check reports.
module FsHotWatch.Tests.CheckRecordsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// Counts the checks a pipeline actually ran in FCS: a replay logs no start.
type private CheckCounter() =
    let mutable started = 0
    member _.Started = started

    interface FsHotWatch.PluginActivity.IActivitySink with
        member _.StartSubtask(_, _) = ()
        member _.UpdateSubtask(_, _) = ()
        member _.EndSubtask _ = ()

        member _.Log line =
            if line.StartsWith "check start " then
                started <- started + 1

        member _.SetSummary _ = ()

/// Library A (A1.fs, A2.fs) and B (B1.fs, B2.fs), B referencing A as an F# project.
/// A2 carries a type error and B2 a warning, so the store is exercised on diagnostics
/// that are not empty.
type private Tree =
    { Root: string
      A1: string
      A2: string
      B1: string
      B2: string
      ADll: string
      Framework: string array
      Base: FSharpProjectOptions }

    member this.A = this.Project "A" [ this.A1; this.A2 ] [] []

    member this.B = this.BWith []

    member this.BWith(extra: string list) =
        this.Project "B" [ this.B1; this.B2 ] [ this.ADll, this.A ] extra

    member this.Project name (files: string list) refs (extra: string list) =
        { this.Base with
            ProjectFileName = Path.Combine(this.Root, $"%s{name}.fsproj")
            SourceFiles = Array.ofList files
            OtherOptions =
                Array.concat
                    [ this.Framework
                      [| for out, _ in refs -> $"-r:%s{out}" |]
                      Array.ofList extra ]
            ReferencedProjects = [| for out, o in refs -> FSharpReferencedProject.FSharpReference(out, o) |]
            UseScriptResolutionRules = false
            ProjectId = None
            Stamp = None }

let private a1Text = [| "module A1"; "let v : int = 1" |]

let private a2Text =
    [| "module A2"; "let w : int = A1.v + 1"; "let bad : int = \"s\"" |]

let private b1Text = [| "module B1"; "let y : int = A1.v + A2.w" |]

let private b2Text =
    [| "module B2"; "let f (x: int) ="; "    match x with"; "    | 1 -> B1.y" |]

let private treeIn (checker: FSharpChecker) (root: string) =
    Directory.CreateDirectory root |> ignore
    let path name = Path.Combine(root, name)
    File.WriteAllLines(path "A1.fs", a1Text)
    File.WriteAllLines(path "A2.fs", a2Text)
    File.WriteAllLines(path "B1.fs", b1Text)
    File.WriteAllLines(path "B2.fs", b2Text)
    let baseScript = path "Base.fsx"
    File.WriteAllLines(baseScript, [| "let placeholder = 0" |])

    let baseOptions, _ =
        checker.GetProjectOptionsFromScript(
            baseScript,
            SourceText.ofString (File.ReadAllText baseScript),
            assumeDotNetFramework = false
        )
        |> Async.RunSynchronously

    let aDll = Path.Combine(root, "A", "bin", "A.dll")
    Directory.CreateDirectory(Path.GetDirectoryName aDll) |> ignore

    { Root = root
      A1 = path "A1.fs"
      A2 = path "A2.fs"
      B1 = path "B1.fs"
      B2 = path "B2.fs"
      ADll = aDll
      Framework = baseOptions.OtherOptions |> Array.filter (fun o -> not (o.EndsWith ".fsx"))
      Base = baseOptions }

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = Path.Combine(dir, "outcomes")
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

/// A fresh process's view: a new checker, a new pipeline, the same store on disk.
let private restart (tree: Tree) (store: OutcomeStore.Store option) (toolchain: string) (b: FSharpProjectOptions) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let counter = CheckCounter()

    let pipeline =
        match store with
        | Some store ->
            CheckPipeline(
                checker,
                activity = counter,
                repoRoot = tree.Root,
                checkRecords = store,
                checkToolchain = toolchain
            )
        | None -> CheckPipeline(checker, activity = counter, repoRoot = tree.Root)

    pipeline.RegisterProject(tree.A.ProjectFileName, tree.A)
    pipeline.RegisterProject(b.ProjectFileName, b)
    pipeline, counter

let private toolchain = "toolchain-under-test"

let private optionsFor (tree: Tree) (file: string) =
    if file = tree.A1 || file = tree.A2 then tree.A else tree.B

/// Check `files` in order through `pipeline` and return each one's projection.
let private checkAll (tree: Tree) (pipeline: CheckPipeline) (files: string list) =
    [ for file in files do
          match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
          | Some result -> file, CheckRecords.CheckOutputs.ofResult (Some tree.Root) result
          | None -> failwith $"no check result for %s{file}" ]

/// What a forced check reports: a new checker, no store, nothing cached.
let private forced (tree: Tree) (b: FSharpProjectOptions) (files: string list) =
    let pipeline, _ = restart tree None toolchain b
    checkAll tree pipeline files |> Map.ofList

let private replay (tree: Tree) (pipeline: CheckPipeline) (b: FSharpProjectOptions) (file: string) =
    let options =
        if file = tree.B1 || file = tree.B2 then
            b
        else
            optionsFor tree file

    pipeline.ReplayCheck(AbsFilePath.create file, options)

/// Check every file and file the records: the state a daemon leaves behind.
let private seeded (dir: string) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()
    let tree = treeIn checker (Path.Combine(dir, "repo"))
    let store = storeIn dir
    let pipeline, _ = restart tree (Some store) toolchain tree.B
    let files = [ tree.A1; tree.A2; tree.B1; tree.B2 ]
    let checkedNow = checkAll tree pipeline files |> Map.ofList
    let filed = pipeline.PersistCheckRecords()
    tree, store, files, checkedNow, filed

[<Fact(Timeout = 240000)>]
let ``a restart replays every filed check, equal to a forced re-check, without running FCS`` () =
    withTempDir "check-records-replay" (fun dir ->
        let tree, store, files, checkedNow, filed = seeded dir
        test <@ filed = 4 @>

        // Positive controls: the store is exercised on an error and a warning.
        let severities file =
            match checkedNow[file] with
            | Some outputs -> outputs.Diagnostics |> List.map _.Severity
            | None -> []

        test <@ severities tree.A2 |> List.contains "Error" @>
        test <@ severities tree.B2 |> List.contains "Warning" @>

        let pipeline, counter = restart tree (Some store) toolchain tree.B

        let replayed =
            [ for file in files -> file, replay tree pipeline tree.B file ] |> Map.ofList

        test <@ counter.Started = 0 @>
        test <@ replayed |> Map.forall (fun _ outputs -> outputs.IsSome) @>

        let fresh = forced tree tree.B files

        for file in files do
            test <@ replayed[file] = fresh[file] @>)

[<Fact(Timeout = 240000)>]
let ``an edited file misses and a comment edit upstream is cut off once the edited file is re-checked`` () =
    withTempDir "check-records-comment" (fun dir ->
        let tree, store, files, _, _ = seeded dir
        File.AppendAllLines(tree.A1, [| "// a comment changes the bytes, not the API" |])

        let pipeline, counter = restart tree (Some store) toolchain tree.B
        test <@ replay tree pipeline tree.B tree.A1 = None @>

        // Lazily, in dependency order: re-check the miss, file it, and walk on.
        checkAll tree pipeline [ tree.A1 ] |> ignore
        test <@ pipeline.PersistCheckRecords() = 1 @>
        let checks = counter.Started

        let replayed =
            [ for file in files -> file, replay tree pipeline tree.B file ] |> Map.ofList

        test <@ counter.Started = checks @>
        test <@ replayed |> Map.forall (fun _ outputs -> outputs.IsSome) @>
        let fresh = forced tree tree.B files

        for file in files do
            test <@ replayed[file] = fresh[file] @>)

[<Fact(Timeout = 240000)>]
let ``an API edit upstream misses every file after it, in its project and downstream`` () =
    withTempDir "check-records-api" (fun dir ->
        let tree, store, files, _, _ = seeded dir
        // `v` changes type: A2 and B1 now report errors they did not before.
        File.WriteAllLines(tree.A1, [| "module A1"; "let v : string = \"one\"" |])

        let pipeline, _ = restart tree (Some store) toolchain tree.B
        checkAll tree pipeline [ tree.A1 ] |> ignore
        test <@ pipeline.PersistCheckRecords() = 1 @>
        test <@ replay tree pipeline tree.B tree.A1 |> Option.isSome @>

        for file in [ tree.A2; tree.B1; tree.B2 ] do
            test <@ replay tree pipeline tree.B file = None @>

        // Re-checked and filed in order, they are served again, and equal a forced check.
        checkAll tree pipeline [ tree.A2; tree.B1; tree.B2 ] |> ignore
        test <@ pipeline.PersistCheckRecords() = 3 @>
        let fresh = forced tree tree.B files

        for file in files do
            test <@ replay tree pipeline tree.B file = fresh[file] @>)

[<Fact(Timeout = 240000)>]
let ``a changed project option, toolchain or referenced output misses exactly the files it reaches`` () =
    withTempDir "check-records-inputs" (fun dir ->
        let tree, store, files, _, _ = seeded dir

        // A define on B reaches B's files only.
        let defined = tree.BWith [ "--define:EXTRA" ]
        let pipeline, _ = restart tree (Some store) toolchain defined
        test <@ replay tree pipeline defined tree.A1 |> Option.isSome @>
        test <@ replay tree pipeline defined tree.A2 |> Option.isSome @>
        test <@ replay tree pipeline defined tree.B1 = None @>
        test <@ replay tree pipeline defined tree.B2 = None @>

        // Another checker build reaches every file.
        let other, _ = restart tree (Some store) "another-toolchain" tree.B

        for file in files do
            test <@ replay tree other tree.B file = None @>

        // A's output appearing (or its bytes changing) reaches B, whose check may type
        // against it, and not A.
        File.WriteAllText(tree.ADll, "not the bytes B was checked against")
        let rebuilt, _ = restart tree (Some store) toolchain tree.B
        test <@ replay tree rebuilt tree.B tree.A1 |> Option.isSome @>
        test <@ replay tree rebuilt tree.B tree.B1 = None @>
        test <@ replay tree rebuilt tree.B tree.B2 = None @>)

[<Fact(Timeout = 240000)>]
let ``a check whose input moved before it was filed is not filed`` () =
    withTempDir "check-records-moved" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let tree = treeIn checker (Path.Combine(dir, "repo"))
        let store = storeIn dir
        let pipeline, _ = restart tree (Some store) toolchain tree.B
        checkAll tree pipeline [ tree.A1; tree.A2 ] |> ignore
        // A1 edited after its check and before the records are filed.
        File.AppendAllLines(tree.A1, [| "let later = 2" |])
        test <@ pipeline.PersistCheckRecords() = 0 @>

        let restarted, _ = restart tree (Some store) toolchain tree.B
        test <@ replay tree restarted tree.B tree.A1 = None @>
        test <@ replay tree restarted tree.B tree.A2 = None @>)

[<Fact(Timeout = 240000)>]
let ``a reverted edit is served again: the store is addressed by content`` () =
    withTempDir "check-records-revert" (fun dir ->
        let tree, store, files, _, _ = seeded dir
        let original = File.ReadAllText tree.B1
        File.WriteAllText(tree.B1, original + "\nlet z = 3\n")
        let edited, _ = restart tree (Some store) toolchain tree.B
        test <@ replay tree edited tree.B tree.B1 = None @>
        File.WriteAllText(tree.B1, original)
        let reverted, _ = restart tree (Some store) toolchain tree.B

        for file in files do
            test <@ replay tree reverted tree.B file |> Option.isSome @>)

let private sampleOutputs (root: string) : CheckRecords.CheckOutputs =
    let bFile = Path.Combine(root, "src", "B.fs")

    { Diagnostics =
        [ { FileName = Path.Combine(root, "src", "A.fs")
            StartLine = 3
            StartColumn = 4
            EndLine = 3
            EndColumn = 9
            ErrorNumber = 1
            Severity = "Error"
            Subcategory = "typecheck"
            Message = $"The type in %s{bFile} does not match \"int\"" }
          { FileName = "/elsewhere/C.fs"
            StartLine = 1
            StartColumn = 0
            EndLine = 1
            EndColumn = 1
            ErrorNumber = 25
            Severity = "Warning"
            Subcategory = "typecheck"
            Message = "Incomplete pattern matches" } ]
      FcsSignature = "fcs"
      UsedSignatures = Some "used"
      ApiSignature = None }

[<Fact>]
let ``outputs name in-repository paths relatively and are read back in the reading checkout`` () =
    let rootA = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fshw-records-a"))
    let rootB = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fshw-records-b"))
    let text = CheckRecords.CheckOutputs.toJson (Some rootA) (sampleOutputs rootA)
    test <@ not (text.Contains rootA) @>
    test <@ CheckRecords.CheckOutputs.tryOfJson (Some rootA) text = Some(sampleOutputs rootA) @>
    test <@ CheckRecords.CheckOutputs.tryOfJson (Some rootB) text = Some(sampleOutputs rootB) @>

    let noRoot = sampleOutputs rootA
    test <@ CheckRecords.CheckOutputs.tryOfJson None (CheckRecords.CheckOutputs.toJson None noRoot) = Some noRoot @>

[<Fact>]
let ``outputs of another schema, or malformed, are a miss`` () =
    let text = CheckRecords.CheckOutputs.toJson None (sampleOutputs "/r")
    test <@ CheckRecords.CheckOutputs.tryOfJson None (text.Replace(CheckRecords.OutputsSchema, "other")) = None @>
    test <@ CheckRecords.CheckOutputs.tryOfJson None "{" = None @>
    test <@ CheckRecords.CheckOutputs.tryOfJson None $"""{{"schema":"%s{CheckRecords.OutputsSchema}"}}""" = None @>
    test <@ CheckRecords.CheckOutputs.hasErrors (sampleOutputs "/r") @>

    test
        <@
            not (
                CheckRecords.CheckOutputs.hasErrors
                    { sampleOutputs "/r" with
                        Diagnostics = [] }
            )
        @>

[<Fact>]
let ``a pipeline with no store files and replays nothing`` () =
    withTempDir "check-records-none" (fun dir ->
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let tree = treeIn checker (Path.Combine(dir, "repo"))
        let pipeline, _ = restart tree None toolchain tree.B
        test <@ pipeline.PersistCheckRecords() = 0 @>
        test <@ pipeline.ReplayCheck(AbsFilePath.create tree.A1, tree.A) = None @>
        test <@ List.isEmpty (pipeline.ReplayProject tree.A) @>)
