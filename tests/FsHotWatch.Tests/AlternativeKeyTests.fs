/// AUTOMATION-1816 step 1: for every no-entry miss, would an alternative check-result key
/// have served it? Measurement only: the key and what it serves must not change.
///
/// Each "daemon" here is a fresh pipeline with an empty in-memory check cache, as a land's
/// daemon starts cold every land, sharing only the persisted alternative-key store.
module FsHotWatch.Tests.AlternativeKeyTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.CheckCacheTally
open FsHotWatch.AlternativeKeys
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// What one cold daemon's scan of `files`, in compile order, did with its cache.
type private Scan =
    {
        Tally: CacheTally
        /// Entries its cache holds after the scan.
        Stored: int
    }

/// A project of `files` (name, lines), loaded in order by a script, in `tmpDir`.
let private project (tmpDir: string) (files: (string * string list) list) =
    let checker = FsHotWatch.Daemon.Daemon.createChecker ()

    for name, lines in files do
        File.WriteAllLines(Path.Combine(tmpDir, name), Array.ofList lines)

    let loads =
        files |> List.map (fun (name, _) -> $"\"%s{name}\"") |> String.concat " "

    let script = Path.Combine(tmpDir, "D.fsx")
    File.WriteAllLines(script, [| $"#load %s{loads}" |])

    let options, _ =
        scriptProjectOptions (checker, script, SourceText.ofString (File.ReadAllText script))
        |> Async.RunSynchronously

    checker, options

/// One cold daemon's scan: a fresh pipeline and cache, measuring with `store` when given
/// (None is an fshw that predates the measurement), checking `files` in compile order and
/// settling as a scan does at its end.
let private coldScan checker (options: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions) tmpDir store files =
    let cache = FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(100)
    let pipeline = CheckPipeline(checker, cacheBackend = cache, repoRoot = tmpDir)
    store |> Option.iter pipeline.MeasureAlternativeKeys
    pipeline.RegisterProject(Path.Combine(tmpDir, "D.fsproj"), options)
    pipeline.BeginGeneration()
    let before = pipeline.CacheTally

    for file in files do
        pipeline.CheckFile(AbsFilePath.create (Path.Combine(tmpDir, file)))
        |> Async.RunSynchronously
        |> ignore

    pipeline.SettleApiIndex()

    { Tally = CacheTally.since before pipeline.CacheTally
      Stored = cache.Count }

let private noEntry (scan: Scan) =
    (CacheMissReasons.ofTally scan.Tally).NoEntry

let private alternatives (scan: Scan) = NoEntryAlternatives.ofTally scan.Tally

/// A.fs first; B.fs references nothing in it; C.fs references `A.x`.
let private abc =
    [ "A.fs", [ "module A"; "let x = 1"; "let helper () = 42" ]
      "B.fs", [ "module B"; "let b = 2" ]
      "C.fs", [ "module C"; "let y = A.x" ] ]

let private edit (tmpDir: string) (name: string) (lines: string list) =
    File.WriteAllLines(Path.Combine(tmpDir, name), Array.ofList lines)

[<Fact(Timeout = 180000)>]
let ``an implementation-only edit to an early file: the later files' misses keep their signature and references`` () =
    withTempDir "alt-body-edit" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let files = [ "A.fs"; "B.fs"; "C.fs" ]

        coldScan checker options tmpDir (Some store) files |> ignore

        // A function body changes; A's signature does not.
        edit tmpDir "A.fs" [ "module A"; "let x = 1"; "let helper () = 43" ]
        let scan = coldScan checker options tmpDir (Some store) files

        // Every lookup still misses: the content key names A's text.
        test <@ scan.Tally.Hits = 0L @>
        test <@ noEntry scan = 3L @>
        // B and C would have hit under either key. A's own bytes are new, so nothing
        // recorded says what an entry for them was typed against.
        test
            <@
                alternatives scan = { SignatureUnchanged = 2L
                                      ReferencesUnchanged = 2L
                                      Unknown = 1L }
            @>)

[<Fact(Timeout = 180000)>]
let ``a signature change in an early file a later file does not reference: unchanged references, moved signature`` () =
    withTempDir "alt-unreferenced-signature" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let files = [ "A.fs"; "B.fs" ]

        coldScan checker options tmpDir (Some store) files |> ignore

        // A declares something new; B names nothing in A.
        edit tmpDir "A.fs" [ "module A"; "let x = 1"; "let helper () = 42"; "let z = 3" ]
        let scan = coldScan checker options tmpDir (Some store) files

        test <@ noEntry scan = 2L @>

        test
            <@
                alternatives scan = { SignatureUnchanged = 0L
                                      ReferencesUnchanged = 1L
                                      Unknown = 1L }
            @>)

[<Fact(Timeout = 180000)>]
let ``a signature change a later file references: neither alternative key would have served it`` () =
    withTempDir "alt-referenced-signature" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let files = [ "A.fs"; "C.fs" ]

        coldScan checker options tmpDir (Some store) files |> ignore

        // `A.x` changes type, and C's inferred `y` with it; C still compiles.
        edit tmpDir "A.fs" [ "module A"; "let x = 1L"; "let helper () = 42" ]
        let scan = coldScan checker options tmpDir (Some store) files

        test <@ noEntry scan = 2L @>

        test
            <@
                alternatives scan = { SignatureUnchanged = 0L
                                      ReferencesUnchanged = 0L
                                      Unknown = 1L }
            @>)

[<Fact(Timeout = 180000)>]
let ``entries stored without alternative upstreams count as unknown, never as a hit or a miss`` () =
    withTempDir "alt-unknown" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let files = [ "A.fs"; "B.fs"; "C.fs" ]

        // An fshw from before the measurement stored these entries and recorded nothing.
        coldScan checker options tmpDir None files |> ignore

        edit tmpDir "A.fs" [ "module A"; "let x = 1"; "let helper () = 43" ]
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let scan = coldScan checker options tmpDir (Some store) files

        test <@ noEntry scan = 3L @>

        test
            <@
                alternatives scan = { SignatureUnchanged = 0L
                                      ReferencesUnchanged = 0L
                                      Unknown = 3L }
            @>)

[<Fact(Timeout = 240000)>]
let ``measuring changes neither the cache key nor what the cache serves`` () =
    // The same two cold scans, with the measurement and without it: the same hits, the same
    // misses for the same reasons, the same stores. A measured signature-unchanged miss
    // is still a miss: nothing is served by an alternative key.
    let run (measure: bool) =
        withTempDir "alt-regression" (fun tmpDir ->
            let checker, options = project tmpDir abc

            let store =
                if measure then
                    Some(InMemoryAlternativeKeyStore() :> IAlternativeKeyStore)
                else
                    None

            let files = [ "A.fs"; "B.fs"; "C.fs" ]
            let first = coldScan checker options tmpDir store files
            edit tmpDir "A.fs" [ "module A"; "let x = 1"; "let helper () = 43" ]
            let second = coldScan checker options tmpDir store files

            // What the cache did, without the measurement's own counts.
            let served (scan: Scan) =
                { scan.Tally with
                    NoEntryAlternatives = Map.empty },
                scan.Stored

            served first, served second, alternatives second)

    let measuredFirst, measuredSecond, measured = run true
    let plainFirst, plainSecond, plain = run false

    test <@ measuredFirst = plainFirst @>
    test <@ measuredSecond = plainSecond @>
    test <@ (fst measuredSecond).Hits = 0L @>
    // The measurement ran (two signature-unchanged misses), and without it every miss is unknown.
    test <@ measured.SignatureUnchanged = 2L @>
    test <@ plain.Unknown = 3L @>

[<Fact>]
let ``the file store keeps alternative upstreams across daemons, and reads a damaged record as none`` () =
    withTempDir "alt-file-store" (fun tmpDir ->
        let dir = Path.Combine(tmpDir, "alternative-keys")

        let recorded =
            { Signature = "sig"
              References = [ "${repo}/src/A.fs"; "${repo}/src/B.fs" ]
              ReferencesFingerprint = "refs" }

        (FileAlternativeKeyStore(dir) :> IAlternativeKeyStore).Write "k1" (AlternativeRecord.Upstreams recorded)

        // A later daemon's store over the same directory.
        let later = FileAlternativeKeyStore(dir) :> IAlternativeKeyStore
        test <@ later.TryRead "k1" = Some(AlternativeRecord.Upstreams recorded) @>
        test <@ later.TryRead "k2" = None @>

        for file in Directory.GetFiles dir do
            File.WriteAllText(file, "{ not json")

        test <@ later.TryRead "k1" = None @>)

[<Fact>]
let ``a record that is not exactly this schema reads as none`` () =
    let recorded =
        { Signature = "sig"
          References = [ "${repo}/src/A.fs" ]
          ReferencesFingerprint = "refs" }

    test <@ tryOfJson (toJson (AlternativeRecord.Upstreams recorded)) = Some(AlternativeRecord.Upstreams recorded) @>

    let noReferences = { recorded with References = [] }

    test
        <@
            tryOfJson (toJson (AlternativeRecord.Upstreams noReferences)) = Some(
                AlternativeRecord.Upstreams noReferences
            )
        @>

    for text in
        [ "{ not json"
          "[]"
          """{"schema":"fshw-alternative-upstreams-v0"}"""
          """{"schema":"fshw-alternative-upstreams-v1"}"""
          """{"schema":5}""" ] do
        test <@ tryOfJson text = None @>

[<Fact>]
let ``the file store drops a record it cannot write, and leaves nothing half-written`` () =
    withTempDir "alt-file-store-fail" (fun tmpDir ->
        let recorded =
            { Signature = "sig"
              References = []
              ReferencesFingerprint = "refs" }

        // Its directory is a file: nothing can be created under it.
        let blocked = Path.Combine(tmpDir, "blocked")
        File.WriteAllText(blocked, "")
        let store = FileAlternativeKeyStore(blocked) :> IAlternativeKeyStore
        store.Write "k" (AlternativeRecord.Upstreams recorded)
        test <@ store.TryRead "k" = None @>

        // The record's own path is a directory: the rename fails after the write.
        let dir = Path.Combine(tmpDir, "alternative-keys")
        Directory.CreateDirectory(Path.Combine(dir, "k.json")) |> ignore
        let occupied = FileAlternativeKeyStore(dir) :> IAlternativeKeyStore
        occupied.Write "k" (AlternativeRecord.Upstreams recorded)
        test <@ occupied.TryRead "k" = None @>
        test <@ Directory.GetFiles dir |> Array.isEmpty @>)

[<Fact>]
let ``a check that is not live has no declaration files to record`` () =
    let replayed =
        Replayed
            { Diagnostics = []
              FcsSignature = "fcs"
              UsedSignatures = None
              ApiSignature = None }

    test <@ FsHotWatch.CacheInputs.usedDeclarationFiles None Unchecked.defaultof<_> (fixtureParseOnly ()) = None @>
    test <@ FsHotWatch.CacheInputs.usedDeclarationFiles None Unchecked.defaultof<_> replayed = None @>

[<Fact>]
let ``the references fingerprint names only referenced files of the chain, and each non-F# reference by its stamp`` () =
    let options references =
        { makeProjectOptions "/repo/App.fsproj" [ "/repo/A.fs"; "/repo/B.fs"; "/repo/App.fs" ] [] with
            ReferencedProjects = references }

    let parts a b =
        readOnlyDict [ "/repo/A.fs", a; "/repo/B.fs", b; "/repo/App.fs", "content:app" ]

    let fingerprint references named files =
        FsHotWatch.CheckCache.referencesFingerprintWith
            (fun _ -> "hash")
            (fun _ -> "stamp")
            (Some "/repo")
            (options references)
            named
            "/repo/App.fs"
            files

    let usual = parts "api:a" "api:b"
    let onlyA = fingerprint [||] usual [ "/repo/A.fs" ]

    // Order, repeats, the file itself and files outside the chain do not move it.
    test <@ fingerprint [||] usual [ "/repo/App.fs"; "/nuget/X.fs"; "/repo/A.fs"; "/repo/A.fs" ] = onlyA @>
    // A referenced file's signature moves it; an unreferenced file's does not.
    test <@ fingerprint [||] (parts "api:a2" "api:b") [ "/repo/A.fs" ] <> onlyA @>
    test <@ fingerprint [||] (parts "api:a" "api:b2") [ "/repo/A.fs" ] = onlyA @>

    let il (stamp: System.DateTime) =
        FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.ILModuleReference(
            "/repo/obj/Cs.dll",
            (fun () -> stamp),
            fun () -> failwith "a fingerprint never reads the module"
        )

    let pe (stamp: System.DateTime) =
        FSharp.Compiler.CodeAnalysis.FSharpReferencedProject.PEReference(
            (fun () -> stamp),
            FSharp.Compiler.CodeAnalysis.DelayedILModuleReader(
                "/repo/obj/Pe.dll",
                fun _ -> failwith "a fingerprint never reads the module"
            )
        )

    let day1, day2 = System.DateTime(2020, 1, 1), System.DateTime(2020, 1, 2)
    test <@ fingerprint [| il day1 |] usual [] <> fingerprint [| il day2 |] usual [] @>
    test <@ fingerprint [| pe day1 |] usual [] <> fingerprint [| pe day2 |] usual [] @>
