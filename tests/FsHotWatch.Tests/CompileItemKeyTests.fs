/// Adding a Compile item re-keys only the checks it can reach (AUTOMATION-1613).
///
/// F# sees only the files before a file in compile order, and those are named, by path
/// and content or signature, in each check's upstream chain. What a check reads of the
/// rest of the source list is one fact: whether the file is the LAST compiland (the parser
/// treats the last file of an application differently, FS0222). So a check key names its
/// project's options WITHOUT the source list, plus that position; a file added after
/// another leaves the other's key alone unless it was the last file.
module FsHotWatch.Tests.CompileItemKeyTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.CheckCache
open FsHotWatch.CheckPipeline
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

// --- keys alone: no compiler ---

/// A library over `files` (names under `root`), with nothing outside the repository.
let private library (root: string) (extra: string list) (files: string list) : FSharpProjectOptions =
    { ProjectFileName = Path.Combine(root, "P.fsproj")
      ProjectId = None
      SourceFiles = [| for f in files -> Path.Combine(root, f) |]
      OtherOptions = Array.ofList ("--target:library" :: extra)
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UnixEpoch
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// Write `module <Name>` files for `names` under `root`.
let private writeModules (root: string) (names: string list) =
    Directory.CreateDirectory root |> ignore

    for name in names do
        let moduleName = Path.GetFileNameWithoutExtension name
        File.WriteAllText(Path.Combine(root, name), $"module %s{moduleName}\nlet value : int = 1\n")

let private storeIn (dir: string) =
    OutcomeStore.Store(
        { Root = Path.Combine(dir, "outcomes")
          Ttl = OutcomeStore.defaultTtl
          MaxBytes = OutcomeStore.defaultMaxBytes
          Log = ignore }
    )

/// The check-record key of each file of `options`, by file name.
let private recordKeys (dir: string) (root: string) (options: FSharpProjectOptions) =
    CheckRecords
        .Walk(storeIn dir, "toolchain", Some root, FileContentHasher().Hash, CheckRecords.readOnly)
        .Project(options)
    |> List.map (fun record -> Path.GetFileName record.File, record.Key.Value.Text)
    |> Map.ofList

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file appended to a project leaves every earlier check key but the previously last one`` () =
    withTempDir "compile-item-append" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ]
        let before = recordKeys dir root (library root [] [ "A.fs"; "B.fs"; "C.fs" ])
        let after = recordKeys dir root (library root [] [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ])

        test <@ after["A.fs"] = before["A.fs"] @>
        test <@ after["B.fs"] = before["B.fs"] @>
        // C is no longer the last compiland: the parser reads it differently.
        test <@ after["C.fs"] <> before["C.fs"] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file inserted in the middle re-keys the files after it and none before`` () =
    withTempDir "compile-item-middle" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "X.fs"; "C.fs" ]
        let before = recordKeys dir root (library root [] [ "A.fs"; "B.fs"; "C.fs" ])
        let after = recordKeys dir root (library root [] [ "A.fs"; "B.fs"; "X.fs"; "C.fs" ])

        test <@ after["A.fs"] = before["A.fs"] @>
        test <@ after["B.fs"] = before["B.fs"] @>
        // C now sees X.
        test <@ after["C.fs"] <> before["C.fs"] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a project flag re-keys every check`` () =
    withTempDir "compile-item-flag" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs" ]
        let files = [ "A.fs"; "B.fs"; "C.fs" ]
        let before = recordKeys dir root (library root [] files)
        let after = recordKeys dir root (library root [ "--define:EXTRA" ] files)

        for file in files do
            test <@ after[file] <> before[file] @>)

// --- the position term's fallback to the whole list: one case each ---

/// The `position` term of each file's check-record key, by file name.
let private positions (dir: string) (root: string) (options: FSharpProjectOptions) =
    CheckRecords
        .Walk(storeIn dir, "toolchain", Some root, FileContentHasher().Hash, CheckRecords.readOnly)
        .Project(options)
    |> List.map (fun record ->
        Path.GetFileName record.File, record.Key.Inputs |> List.find (fun (name, _) -> name = "position") |> snd)
    |> Map.ofList

/// The whole-list term for `options`: what every file before an unusual later file names.
let private wholeList (root: string) (options: FSharpProjectOptions) =
    "list:" + getProjectOptionsHashRelativeTo (Some root) options

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file listed twice keys it, and every file before its second listing, by the whole source list`` () =
    withTempDir "compile-item-twice" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs"; "D.fs"; "E.fs" ]
        let twice = library root [] [ "A.fs"; "B.fs"; "A.fs"; "C.fs"; "D.fs" ]
        let named = positions dir root twice

        test <@ named["A.fs"] = wholeList root twice @>
        test <@ named["B.fs"] = wholeList root twice @>
        // Control: C, after the second listing, reasons as usual.
        test <@ named["C.fs"] = "followed" @>
        // Control: the same shape with a distinct third file reasons as usual too.
        let once =
            positions dir root (library root [] [ "A.fs"; "B.fs"; "E.fs"; "C.fs"; "D.fs" ])

        test <@ once["A.fs"] = "followed" && once["B.fs"] = "followed" @>

        // So a file appended after them moves A's and B's keys, and not C's.
        let before = recordKeys dir root twice

        let after =
            recordKeys
                dir
                root
                { twice with
                    SourceFiles = Array.append twice.SourceFiles [| Path.Combine(root, "E.fs") |] }

        test <@ after["A.fs"] <> before["A.fs"] && after["B.fs"] <> before["B.fs"] @>
        test <@ after["C.fs"] = before["C.fs"] @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a missing later file keys every file before it by the whole source list`` () =
    withTempDir "compile-item-missing" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs" ]
        let missing = library root [] [ "A.fs"; "B.fs"; "Missing.fs" ]
        let named = positions dir root missing

        test <@ named["A.fs"] = wholeList root missing @>
        test <@ named["B.fs"] = wholeList root missing @>
        // Control: the same list with the file present reasons as usual.
        let present = positions dir root (library root [] [ "A.fs"; "B.fs"; "C.fs" ])
        test <@ present["A.fs"] = "followed" && present["B.fs"] = "followed" @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a later compile item that is not F# source keys every file before it by the whole source list`` () =
    withTempDir "compile-item-not-fsharp" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs" ]
        File.WriteAllText(Path.Combine(root, "Notes.txt"), "not source")
        let notes = library root [] [ "A.fs"; "B.fs"; "Notes.txt" ]
        let named = positions dir root notes

        test <@ named["A.fs"] = wholeList root notes @>
        test <@ named["B.fs"] = wholeList root notes @>
        // Control: an F# file in its place reasons as usual.
        let source = positions dir root (library root [] [ "A.fs"; "B.fs"; "C.fs" ])
        test <@ source["A.fs"] = "followed" && source["B.fs"] = "followed" @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the in-memory check key and the signature chain stand for the files before an added one`` () =
    withTempDir "compile-item-memory" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeModules root [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ]
        let before = library root [] [ "A.fs"; "B.fs"; "C.fs" ]
        let after = library root [] [ "A.fs"; "B.fs"; "C.fs"; "D.fs" ]
        let a = Path.Combine(root, "A.fs")
        let c = Path.Combine(root, "C.fs")
        let fingerprints = UpstreamFingerprints(Some root)
        let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider

        let memoryKey (options: FSharpProjectOptions) file =
            makeCacheKeyWith
                provider
                "toolchain"
                (keyedOptionsOf (Some root) fingerprints.HashFile options file)
                "upstream"
                file

        test <@ memoryKey after a = memoryKey before a @>
        test <@ memoryKey after c <> memoryKey before c @>

        let apiEntry (options: FSharpProjectOptions) =
            let hash = fingerprints.HashFile

            let table, _ =
                apiUpstreamFingerprintsWith
                    hash
                    fileStampText
                    (fun opts -> upstreamFingerprints hash (Some root) opts)
                    (fun _ _ _ _ _ -> None)
                    (Some root)
                    options

            table[c]

        test <@ apiEntry after = apiEntry before @>)

// --- through the compiler: a replay equals what FCS reports under the new list ---

/// An application over `files` under `root`, against the framework a script resolves.
let private application (checker: FSharpChecker) (root: string) (files: string list) =
    let script = Path.Combine(root, "Base.fsx")
    File.WriteAllLines(script, [| "let placeholder = 0" |])

    let baseOptions, _ =
        scriptProjectOptions (checker, script, SourceText.ofString (File.ReadAllText script))
        |> Async.RunSynchronously

    let framework =
        baseOptions.OtherOptions
        |> Array.filter (fun o -> not (o.EndsWith ".fsx") && not (o.StartsWith "--target:"))

    { baseOptions with
        ProjectFileName = Path.Combine(root, "App.fsproj")
        SourceFiles = [| for f in files -> Path.Combine(root, f) |]
        OtherOptions = Array.append framework [| "--target:exe" |]
        ReferencedProjects = [||]
        UseScriptResolutionRules = false
        ProjectId = None
        Stamp = None }

let private toolchain = "toolchain-under-test"

let private pipelineOver (store: OutcomeStore.Store option) (options: FSharpProjectOptions) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()

    let pipeline =
        match store with
        | Some store ->
            CheckPipeline(
                checker,
                repoRoot = Path.GetDirectoryName options.ProjectFileName,
                checkRecords = store,
                checkToolchain = toolchain
            )
        | None -> CheckPipeline(checker, repoRoot = Path.GetDirectoryName options.ProjectFileName)

    pipeline.RegisterProject(options.ProjectFileName, options)
    pipeline

let private outputsOf (root: string) (pipeline: CheckPipeline) (file: string) =
    match pipeline.CheckFile(AbsFilePath.create file) |> Async.RunSynchronously with
    | Some result -> CheckRecords.CheckOutputs.ofResult (Some root) result
    | None -> failwith $"no check result for %s{file}"

/// `Main.fs` and `Tail.fs`, an anonymous module: legal only as an application's last file.
let private writeApplication (root: string) =
    Directory.CreateDirectory root |> ignore
    File.WriteAllText(Path.Combine(root, "Main.fs"), "module Main\nlet v : int = 1\n")
    File.WriteAllText(Path.Combine(root, "Tail.fs"), "let t : int = Main.v + 1\n")
    File.WriteAllText(Path.Combine(root, "Added.fs"), "module Added\nlet a : int = 2\n")

[<Fact(Timeout = 240000)>]
let ``a file appended after filed checks replays the earlier ones as FCS reports them, and re-checks the old last file``
    ()
    =
    withTempDir "compile-item-fcs" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeApplication root
        let store = storeIn dir
        let seedChecker = FsHotWatch.Daemon.Daemon.createChecker ()
        let before = application seedChecker root [ "Main.fs"; "Tail.fs" ]

        let after =
            { before with
                SourceFiles = Array.append before.SourceFiles [| Path.Combine(root, "Added.fs") |] }

        let main = Path.Combine(root, "Main.fs")
        let tail = Path.Combine(root, "Tail.fs")

        let seeding = pipelineOver (Some store) before
        let seededTail = outputsOf root seeding tail
        outputsOf root seeding main |> ignore
        test <@ seeding.PersistCheckRecords() = 2 @>

        let next = pipelineOver (Some store) after
        let replayedMain = next.ReplayCheck(AbsFilePath.create main, after)
        test <@ replayedMain.IsSome @>
        test <@ next.ReplayCheck(AbsFilePath.create tail, after) = None @>
        test <@ next.CheckStarts = 0L @>

        // What FCS reports under the new list: Main as filed, Tail no longer legal.
        let forced = pipelineOver None after
        test <@ replayedMain = outputsOf root forced main @>

        let errorNumbers (outputs: CheckOutputs option) =
            outputs
            |> Option.map (fun o -> o.Diagnostics |> List.map _.ErrorNumber)
            |> Option.defaultValue []

        // Positive control: the position the key names changes what a check reports.
        test <@ not (errorNumbers seededTail |> List.contains 222) @>
        test <@ errorNumbers (outputsOf root forced tail) |> List.contains 222 @>)

[<Fact(Timeout = 240000)>]
let ``within one process a file appended to a registered project re-checks none of the files before the old last`` () =
    withTempDir "compile-item-memory-fcs" (fun dir ->
        let root = Path.Combine(dir, "repo")
        writeApplication root
        File.WriteAllText(Path.Combine(root, "Second.fs"), "module Second\nlet s : int = Main.v\n")
        let checker = FsHotWatch.Daemon.Daemon.createChecker ()
        let before = application checker root [ "Main.fs"; "Second.fs"; "Tail.fs" ]

        let pipeline =
            CheckPipeline(
                checker,
                cacheBackend =
                    (FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(100) :> FsHotWatch.CheckCache.ICheckCacheBackend),
                repoRoot = root
            )

        pipeline.RegisterProject(before.ProjectFileName, before)
        let main = Path.Combine(root, "Main.fs")
        let second = Path.Combine(root, "Second.fs")

        for file in before.SourceFiles do
            outputsOf root pipeline file |> ignore

        let checks = pipeline.CheckStarts
        // Positive control: every file was checked once.
        test <@ checks = 3L @>

        let after =
            { before with
                SourceFiles = Array.append before.SourceFiles [| Path.Combine(root, "Added.fs") |] }

        pipeline.RegisterProject(after.ProjectFileName, after)
        outputsOf root pipeline main |> ignore
        outputsOf root pipeline second |> ignore
        test <@ pipeline.CheckStarts = checks @>)
