/// AUTOMATION-1816 step 2: the references key in SHADOW. For every no-entry miss the
/// references key would have served, the daemon still checks the file cold, and records
/// whether the verdict the key would have served agrees with the cold one. It never serves.
///
/// Each "daemon" here is a fresh pipeline with an empty in-memory check cache, as a land's
/// daemon starts cold every land, sharing only the persisted alternative-key store.
module FsHotWatch.Tests.ReferencesShadowTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.Text
open FsHotWatch.CheckPipeline
open FsHotWatch.CheckCacheTally
open FsHotWatch.AlternativeKeys
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// How one cold daemon's scan of `files` was measured.
[<RequireQualifiedAccess>]
type private Mode =
    /// An fshw that records no alternative upstreams.
    | Unmeasured
    /// The references key off: step 1's measurement only.
    | Measure of IAlternativeKeyStore
    /// The references key in shadow.
    | Shadow of IAlternativeKeyStore

/// What one cold daemon's scan did with its cache.
type private Scan =
    {
        Tally: CacheTally
        /// Entries its cache holds after the scan.
        Stored: int
        /// FCS checks it started.
        CheckStarts: int64
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

/// One cold daemon's scan: a fresh pipeline and cache in `mode`, checking `files` in
/// compile order and settling as a scan does at its end.
let private coldScan checker (options: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions) tmpDir mode files =
    let cache = FsHotWatch.InMemoryCheckCache.InMemoryCheckCache(100)
    let pipeline = CheckPipeline(checker, cacheBackend = cache, repoRoot = tmpDir)

    match mode with
    | Mode.Unmeasured -> ()
    | Mode.Measure store -> pipeline.MeasureAlternativeKeys store
    | Mode.Shadow store -> pipeline.ShadowReferencesKey store

    pipeline.RegisterProject(Path.Combine(tmpDir, "D.fsproj"), options)
    pipeline.BeginGeneration()
    let before = pipeline.CacheTally
    let startsBefore = pipeline.CheckStarts

    for file in files do
        pipeline.CheckFile(AbsFilePath.create (Path.Combine(tmpDir, file)))
        |> Async.RunSynchronously
        |> ignore

    pipeline.SettleApiIndex()

    { Tally = CacheTally.since before pipeline.CacheTally
      Stored = cache.Count
      CheckStarts = pipeline.CheckStarts - startsBefore }

let private shadow (scan: Scan) = ReferencesShadowScan.ofTally scan.Tally

/// A.fs first; B.fs references nothing in it; C.fs references `A.x`.
let private abc =
    [ "A.fs", [ "module A"; "let x = 1"; "let helper () = 42" ]
      "B.fs", [ "module B"; "let b = 2" ]
      "C.fs", [ "module C"; "let y = A.x" ] ]

let private files = [ "A.fs"; "B.fs"; "C.fs" ]

let private edit (tmpDir: string) (name: string) (lines: string list) =
    File.WriteAllLines(Path.Combine(tmpDir, name), Array.ofList lines)

/// A body-only edit to A.fs: B's and C's references are unchanged.
let private editBody tmpDir =
    edit tmpDir "A.fs" [ "module A"; "let x = 1"; "let helper () = 43" ]

let private noUnverified: ShadowUnverified =
    { NoCachedVerdict = 0L
      NoColdVerdict = 0L }

/// A store that files every record as `inner` would, except that it alters with `alter`
/// the verdict recorded for an entry that references `referenced`: an entry whose cached
/// verdict differs from what a cold check of the same inputs reports.
type private AlteringStore(inner: IAlternativeKeyStore, referenced: string, alter: CheckVerdict -> CheckVerdict) =
    interface IAlternativeKeyStore with
        member _.TryRead key = inner.TryRead key

        member _.Write key record =
            match record with
            | AlternativeRecord.WithVerdict(upstreams, verdict) when
                upstreams.References |> List.exists (fun r -> r.EndsWith referenced)
                ->
                inner.Write key (AlternativeRecord.WithVerdict(upstreams, alter verdict))
            | _ -> inner.Write key record

[<Fact(Timeout = 180000)>]
let ``shadow: every miss the references key would serve is verified, and an equal verdict is an agreement`` () =
    withTempDir "shadow-agree" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore

        coldScan checker options tmpDir (Mode.Shadow store) files |> ignore
        editBody tmpDir
        let scan = coldScan checker options tmpDir (Mode.Shadow store) files

        let measured = NoEntryAlternatives.ofTally scan.Tally
        test <@ measured.ReferencesUnchanged = 2L @>

        // B and C would have been served; both cold verdicts agree with what was stored.
        test <@ ReferencesShadowScan.wouldServe (shadow scan) = measured.ReferencesUnchanged @>
        test <@ (shadow scan).Agreed = 2L @>
        test <@ List.isEmpty (shadow scan).Disagreements @>
        test <@ (shadow scan).Unverified = noUnverified @>)

[<Fact(Timeout = 180000)>]
let ``shadow: a disagreement names the file and every check that reads what disagreed`` () =
    withTempDir "shadow-disagree" (fun tmpDir ->
        let checker, options = project tmpDir abc

        // C's entry (the one that references A) is recorded with diagnostics a cold check
        // of its inputs does not report.
        let store =
            AlteringStore(
                InMemoryAlternativeKeyStore(),
                "/A.fs",
                fun verdict ->
                    { verdict with
                        Diagnostics = "not what a cold check says" }
            )
            :> IAlternativeKeyStore

        coldScan checker options tmpDir (Mode.Shadow store) files |> ignore
        editBody tmpDir
        let scan = coldScan checker options tmpDir (Mode.Shadow store) files

        test <@ (shadow scan).Agreed = 1L @>

        test
            <@
                (shadow scan).Disagreements = [ { File = "${repo}/C.fs"
                                                  Facets = [ VerdictFacet.Diagnostics ] } ]
            @>

        test <@ VerdictFacet.checks VerdictFacet.Diagnostics = [ VerdictCheck.Compiler ] @>)

[<Fact(Timeout = 180000)>]
let ``shadow: an entry recorded with the references key off has no cached verdict, and says so`` () =
    withTempDir "shadow-unverified" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore

        // The previous daemon ran with the switch off: upstreams only.
        coldScan checker options tmpDir (Mode.Measure store) files |> ignore
        editBody tmpDir
        let scan = coldScan checker options tmpDir (Mode.Shadow store) files

        test <@ ReferencesShadowScan.wouldServe (shadow scan) = 2L @>
        test <@ (shadow scan).Agreed = 0L @>

        test
            <@
                (shadow scan).Unverified = { noUnverified with
                                               NoCachedVerdict = 2L }
            @>)

[<Fact(Timeout = 240000)>]
let ``shadow never serves: the same lookups, misses, checks and stores as the switch off`` () =
    let run (shadowed: bool) =
        withTempDir "shadow-never-serves" (fun tmpDir ->
            let checker, options = project tmpDir abc
            let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore

            let mode = if shadowed then Mode.Shadow store else Mode.Measure store

            coldScan checker options tmpDir mode files |> ignore
            editBody tmpDir
            let second = coldScan checker options tmpDir mode files

            // What the cache did, without the shadow's own counts.
            { second.Tally with
                ReferencesShadow = ShadowTally.empty },
            second.Stored,
            second.CheckStarts,
            shadow second)

    let shadowTally, shadowStored, shadowStarts, shadowed = run true
    let offTally, offStored, offStarts, _ = run false

    test <@ shadowTally = offTally @>
    test <@ shadowStored = offStored @>
    // Every file was checked cold, the two the references key would have served included.
    test <@ shadowStarts = offStarts @>
    test <@ shadowStarts = 3L @>
    test <@ shadowTally.Hits = 0L @>
    test <@ ReferencesShadowScan.wouldServe shadowed = 2L @>

[<Fact(Timeout = 240000)>]
let ``the switch off records no verdict, counts no shadow, and holds nothing for it`` () =
    withTempDir "shadow-off" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let inner = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let written = System.Collections.Concurrent.ConcurrentBag<AlternativeRecord>()

        let store =
            { new IAlternativeKeyStore with
                member _.TryRead key = inner.TryRead key

                member _.Write key record =
                    written.Add record
                    inner.Write key record }

        coldScan checker options tmpDir (Mode.Measure store) files |> ignore
        editBody tmpDir
        let scan = coldScan checker options tmpDir (Mode.Measure store) files

        test <@ not written.IsEmpty @>

        test
            <@
                written
                |> Seq.forall (function
                    | AlternativeRecord.Upstreams _ -> true
                    | AlternativeRecord.WithVerdict _ -> false)
            @>

        test <@ scan.Tally.ReferencesShadow = ShadowTally.empty @>
        test <@ (shadow scan).HeldBytesPeak = 0L @>)

[<Fact(Timeout = 180000)>]
let ``shadow accounts for the bytes it holds between settles`` () =
    withTempDir "shadow-held" (fun tmpDir ->
        let checker, options = project tmpDir abc
        let store = InMemoryAlternativeKeyStore() :> IAlternativeKeyStore
        let scan = coldScan checker options tmpDir (Mode.Shadow store) files

        // Three cold verdicts were held until the settle recorded them.
        let held = (shadow scan).HeldBytesPeak
        test <@ held >= 3L * CheckVerdict.heldBytes (CheckVerdict.ofParts "" "" "" "") @>
        test <@ held < 3L * 4096L @>)

// --- The verdict and its record, without FCS ---

let private verdict = CheckVerdict.ofParts "diagnostics" "fcs" "used" "api"

let private upstreams: AlternativeUpstreams =
    { Signature = "sig"
      References = [ "${repo}/A.fs" ]
      ReferencesFingerprint = "refs" }

[<Fact>]
let ``a verdict that differs names each differing facet, and the checks that read each`` () =
    test <@ verify "f" (AlternativeRecord.WithVerdict(upstreams, verdict)) (Some verdict) = ShadowOutcome.Agreed @>

    let differs = CheckVerdict.ofParts "diagnostics" "fcs2" "used2" "api"

    test
        <@
            verify "f" (AlternativeRecord.WithVerdict(upstreams, verdict)) (Some differs) = ShadowOutcome.Disagreed
                { File = "f"
                  Facets = [ VerdictFacet.FcsSignature; VerdictFacet.UsedSignatures ] }
        @>

    test
        <@
            verify "f" (AlternativeRecord.Upstreams upstreams) (Some verdict) = ShadowOutcome.Unverified
                ShadowUnverifiedReason.NoCachedVerdict
        @>

    test
        <@
            verify "f" (AlternativeRecord.WithVerdict(upstreams, verdict)) None = ShadowOutcome.Unverified
                ShadowUnverifiedReason.NoColdVerdict
        @>

    // What a plugin keys a file's result by: a served check moves every plugin with it.
    let plugins = [ VerdictCheck.Analyzers; VerdictCheck.Lint; VerdictCheck.TestPrune ]
    test <@ VerdictFacet.checks VerdictFacet.FcsSignature = plugins @>
    test <@ VerdictFacet.checks VerdictFacet.UsedSignatures = plugins @>
    // What later files are keyed and typed against.
    test <@ VerdictFacet.checks VerdictFacet.ApiSignature = [ VerdictCheck.Compiler ] @>

[<Fact>]
let ``a verdict is read from a check's outputs, absent signatures included`` () =
    let outputs: CheckOutputs =
        { Diagnostics = []
          FcsSignature = "fcs"
          UsedSignatures = None
          ApiSignature = None }

    let none = CheckVerdict.ofOutputs None outputs

    let some =
        CheckVerdict.ofOutputs
            None
            { outputs with
                UsedSignatures = Some "used"
                ApiSignature = Some "api" }

    test <@ none.FcsSignature = "fcs" @>
    test <@ none.UsedSignatures <> some.UsedSignatures @>
    test <@ none.ApiSignature <> some.ApiSignature @>
    test <@ none.Diagnostics = some.Diagnostics @>

[<Fact>]
let ``a record with a verdict round-trips, and a step-1 record reads as upstreams only`` () =
    let withVerdict = AlternativeRecord.WithVerdict(upstreams, verdict)
    test <@ tryOfJson (toJson withVerdict) = Some withVerdict @>

    let stepOne = AlternativeRecord.Upstreams upstreams
    test <@ tryOfJson (toJson stepOne) = Some stepOne @>

    // Exactly what fshw 0.14.0-alpha.156 wrote.
    let alpha156 =
        """{"schema":"fshw-alternative-upstreams-v1","signature":"sig","references":["${repo}/A.fs"],"referencesFingerprint":"refs"}"""

    test <@ tryOfJson alpha156 = Some stepOne @>

    // A verdict record missing a facet is not this schema.
    let partial =
        """{"schema":"fshw-alternative-upstreams-v2","signature":"sig","references":[],"referencesFingerprint":"refs","verdict":{"diagnostics":"d"}}"""

    test <@ tryOfJson partial = None @>

[<Fact>]
let ``the shadow tally takes one scan's share of disagreements and held bytes`` () =
    let counter = CacheTallyCounter()

    let earlier: ShadowDisagreement =
        { File = "old"
          Facets = [ VerdictFacet.Diagnostics ] }

    counter.Shadowed(ShadowOutcome.Disagreed earlier)
    counter.ShadowHeld 900L
    let atScanStart = counter.Snapshot

    let first: ShadowDisagreement =
        { File = "a"
          Facets = [ VerdictFacet.ApiSignature ] }

    let second: ShadowDisagreement =
        { File = "b"
          Facets = [ VerdictFacet.UsedSignatures ] }

    counter.Shadowed ShadowOutcome.Agreed
    counter.Shadowed(ShadowOutcome.Disagreed first)
    counter.Shadowed(ShadowOutcome.Unverified ShadowUnverifiedReason.NoColdVerdict)
    counter.Shadowed(ShadowOutcome.Disagreed second)
    counter.ShadowHeld 300L
    counter.ShadowHeld 500L

    let scan =
        ReferencesShadowScan.ofTally (CacheTally.since atScanStart counter.Snapshot)

    test <@ scan.Agreed = 1L @>
    // In the order they were found.
    test <@ scan.Disagreements = [ first; second ] @>
    test <@ scan.Unverified = { noUnverified with NoColdVerdict = 1L } @>
    test <@ scan.HeldBytesPeak = 500L @>
    test <@ ReferencesShadowScan.wouldServe scan = 4L @>

[<Fact>]
let ``the references key serves exactly the misses whose referenced signatures are unchanged`` () =
    test <@ referencesKeyServes NoEntryAlternative.SignatureUnchanged @>
    test <@ referencesKeyServes NoEntryAlternative.OnlyReferencesUnchanged @>
    test <@ not (referencesKeyServes NoEntryAlternative.NeitherUnchanged) @>
    test <@ not (referencesKeyServes NoEntryAlternative.Unknown) @>

[<Fact>]
let ``a verdict's diagnostics name each diagnostic, with in-repository paths relative`` () =
    let diagnostic (root: string) : StoredDiagnostic =
        { FileName = root + "/src/A.fs"
          StartLine = 1
          StartColumn = 2
          EndLine = 1
          EndColumn = 5
          ErrorNumber = 1
          Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error
          Subcategory = "typecheck"
          Message = $"no %s{root}/src/B.fs" }

    let outputs (diagnostics: StoredDiagnostic list) : CheckOutputs =
        { Diagnostics = diagnostics
          FcsSignature = "fcs"
          UsedSignatures = None
          ApiSignature = None }

    let at (root: string) diagnostics =
        (CheckVerdict.ofOutputs (Some root) (outputs diagnostics)).Diagnostics

    // Two checkouts of one tree report the same verdict.
    test <@ at "/one" [ diagnostic "/one" ] = at "/two" [ diagnostic "/two" ] @>
    test <@ at "/one" [ diagnostic "/one" ] <> at "/one" [] @>

    test
        <@
            at "/one" [ diagnostic "/one" ]
            <> at
                "/one"
                [ { diagnostic "/one" with
                      ErrorNumber = 2 } ]
        @>
