/// What a persisted record's key names, pinned (AUTOMATION-1616).
///
/// A record filed by one fshw build is read by the next. Its key must move when what it
/// would answer moves — a different compiler service, FSharp.Core, or a change in how fshw
/// computes or files it — and at no other time: a key that moves every release turns every
/// release into a cold scan of every workspace. fshw's own build is therefore NOT a term;
/// a hand-bumped schema constant carries its duty, and the goldens here are what make
/// forgetting the bump loud: any change to how a key is computed fails one of them.
module FsHotWatch.Tests.RecordKeyGoldenTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.CheckCache
open FsHotWatch.Tests.TestHelpers

/// Fails with the instruction a changed key needs, naming what changed.
let private golden (what: string) (expected: 'T) (actual: 'T) =
    if actual <> expected then
        failwith
            $"%s{what} changed. A record key is a contract with every record an earlier fshw filed: \
              bump the schema constant, then update the golden.\nexpected: %A{expected}\nactual:   %A{actual}"

/// A checker build with every component spelled out, so a key under it is the same on
/// every machine and every build of fshw.
let private buildN: CheckerBuild =
    { Fcs = "FSharp.Compiler.Service@43.12.402.0/11111111-1111-1111-1111-111111111111"
      FSharpCore = "FSharp.Core@10.0.0.0/22222222-2222-2222-2222-222222222222"
      Fshw = "FsHotWatch@0.10.0.110/33333333-3333-3333-3333-333333333333"
      RecordSchema = CheckRecordSchema }

/// The next fshw build: same FCS, FSharp.Core and schema, a new version and module id.
let private buildNPlus1 =
    { buildN with
        Fshw = "FsHotWatch@0.10.0.111/44444444-4444-4444-4444-444444444444" }

// --- the checker build a check record is keyed by ---

[<Fact(Timeout = 15000)>]
let ``toolchainIdentity is this process's checker build: the exact FCS and FSharp.Core builds and the schema`` () =
    let current = CheckerBuild.current
    test <@ toolchainIdentity = CheckerBuild.keyIdentity current @>
    test <@ current.Fcs = assemblyIdentity typeof<FSharpChecker>.Assembly @>
    test <@ current.FSharpCore = assemblyIdentity typeof<option<int>>.Assembly @>
    test <@ current.Fshw = assemblyIdentity typeof<CacheKey>.Assembly @>
    test <@ current.RecordSchema = CheckRecordSchema @>

    // A module version id changes with every build of an assembly, so it names the
    // checker's code more exactly than a version.
    let mvid (t: Type) =
        string t.Assembly.ManifestModule.ModuleVersionId

    test <@ toolchainIdentity.Contains(mvid typeof<FSharpChecker>) @>
    test <@ toolchainIdentity.Contains(mvid typeof<option<int>>) @>

[<Fact(Timeout = 15000)>]
let ``a new fshw build with the same FCS, FSharp.Core and schema keys its checks alike`` () =
    test <@ CheckerBuild.keyIdentity buildNPlus1 = CheckerBuild.keyIdentity buildN @>
    test <@ not ((CheckerBuild.keyIdentity buildN).Contains "FsHotWatch@") @>

[<Fact(Timeout = 15000)>]
let ``a schema bump, an FCS version or an FSharp.Core version each re-key every check`` () =
    let keyed = CheckerBuild.keyIdentity buildN

    let moved =
        [ { buildN with
              RecordSchema = CheckRecordSchema + "-bumped" }
          { buildN with
              Fcs = "FSharp.Compiler.Service@43.12.403.0/11111111-1111-1111-1111-111111111111" }
          { buildN with
              FSharpCore = "FSharp.Core@10.0.1.0/22222222-2222-2222-2222-222222222222" } ]

    for build in moved do
        test <@ CheckerBuild.keyIdentity build <> keyed @>

[<Fact(Timeout = 15000)>]
let ``provenance names fshw's own build, which the key leaves out`` () =
    test <@ (CheckerBuild.provenance buildN).Contains buildN.Fshw @>
    test <@ CheckerBuild.provenance buildNPlus1 <> CheckerBuild.provenance buildN @>
    test <@ producerIdentity = CheckerBuild.provenance CheckerBuild.current @>

// --- the golden check-record key ---

/// A two-file library with nothing outside the repository in its options, so every
/// component of its keys is a function of the bytes below and the fixed checker build.
let private goldenProject (root: string) : FSharpProjectOptions =
    let a = Path.Combine(root, "A.fs")
    let b = Path.Combine(root, "B.fs")
    File.WriteAllText(a, "module A\nlet v : int = 1\n")
    File.WriteAllText(b, "module B\nlet w : int = A.v + 1\n")

    { ProjectFileName = Path.Combine(root, "Golden.fsproj")
      ProjectId = None
      SourceFiles = [| a; b |]
      OtherOptions = [| "--target:library"; "--define:GOLDEN" |]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = DateTime.UnixEpoch
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

/// The fixture's options hash, in-repo paths relative.
[<Literal>]
let private GoldenOptions =
    "56905e9d4f44d6e128cddaf4cef807a2906b587a1a7420c86e738086cec70384"

/// No referenced project: the digest of the empty string.
[<Literal>]
let private NoProjectOutputs =
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

let private goldenToolchain =
    "FSharp.Compiler.Service@43.12.402.0/11111111-1111-1111-1111-111111111111;\
     FSharp.Core@10.0.0.0/22222222-2222-2222-2222-222222222222;\
     fshw-check-schema=fshw-check-semantics-v3"

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``GOLDEN: the check-record key of a fixture project and every component it is folded from`` () =
    withTempDir "golden-check-key" (fun dir ->
        let root = Path.Combine(dir, "repo")
        Directory.CreateDirectory root |> ignore
        let options = goldenProject root

        let store =
            OutcomeStore.Store(
                { Root = Path.Combine(dir, "outcomes")
                  Ttl = OutcomeStore.defaultTtl
                  MaxBytes = OutcomeStore.defaultMaxBytes
                  Log = ignore }
            )

        golden "The checker build a check is keyed by" goldenToolchain (CheckerBuild.keyIdentity buildN)

        let records =
            CheckRecords
                .Walk(
                    store,
                    CheckerBuild.keyIdentity buildN,
                    Some root,
                    FileContentHasher().Hash,
                    CheckRecords.readOnly
                )
                .Project(options)

        let keys = records |> List.map (fun r -> Path.GetFileName r.File, r.Key)

        golden
            "The check-record key's components"
            [ "A.fs",
              [ "content", "d0c34d1ef5d2b2a7d63e3c059a01e397a9910c7938ba3e1e2ca0622adb748747"
                "file", "repo:A.fs"
                "options", GoldenOptions
                "project-outputs", NoProjectOutputs
                "toolchain", goldenToolchain
                "upstream", "5d165528df7e099050eb1e7be9b23d96070570be2400cc335826a84063450e47" ]
              "B.fs",
              [ "content", "bad3ec53512a110420de668c95a871a266ea9923220f3cf66a776473ccd3dd8d"
                "file", "repo:B.fs"
                "options", GoldenOptions
                "project-outputs", NoProjectOutputs
                "toolchain", goldenToolchain
                "upstream", "af4bdd5a53f6e8396eb5cc9f22d1b595acfd223e2db4805a5491be8456d6855d" ] ]
            (keys |> List.map (fun (file, key) -> file, key.Inputs))

        golden
            "The check-record key"
            [ "A.fs", "ak1:b483f355ea89fb5c358a883ccf832ce3be8d077f1689e1215c33414898e35ef2"
              "B.fs", "ak1:5c79e92b37e28d7319103b90cba07c87e92afb09b55d229f35936222a6dcc461" ]
            (keys |> List.map (fun (file, key) -> file, key.Value.Text))

        // The in-memory cache key folds the same toolchain, by a recipe of its own.
        let provider = TimestampCacheKeyProvider() :> ICacheKeyProvider

        let memoryKey =
            makeCacheKeyWith
                provider
                (CheckerBuild.keyIdentity buildN)
                (getProjectOptionsHashRelativeTo (Some root) options)
                "upstream"
                options.SourceFiles[0]
            |> Option.map (fun k -> ContentHash.value k.ProjectOptionsHash)

        golden
            "The in-memory check cache key's options half"
            (Some "d72cd4fc258615bd197feaa537c071fc7bb76c19e69d9949500190d4cf0ec670")
            memoryKey)

// --- the golden options-record key ---

let private goldenOptionsToolchain =
    "Ionide.ProjInfo@0.74.2.0/55555555-5555-5555-5555-555555555555;\
     Microsoft.Build@15.1.0.0/66666666-6666-6666-6666-666666666666;\
     fshw-options-schema=fshw-options-semantics-v1"

[<Fact(Timeout = 15000)>]
let ``GOLDEN: the options-record keys of a fixed request and every component they are folded from`` () =
    let toolchain =
        OptionsInputs.toolchainOf
            "Ionide.ProjInfo@0.74.2.0/55555555-5555-5555-5555-555555555555"
            "Microsoft.Build@15.1.0.0/66666666-6666-6666-6666-666666666666"
            OptionsInputs.OptionsRecordSchema

    golden "The toolchain an options record is keyed by" goldenOptionsToolchain toolchain

    let request: OptionsRecords.Request =
        { Root = "/golden/repo"
          Projects = [ "/golden/repo/A.fsproj" ]
          Toolchain = toolchain
          Sdk = "/sdk/10.0.401"
          GlobalProperties = [ "Configuration", "Debug" ] }

    let requestKey = OptionsRecords.requestKey request

    golden
        "The options-inputs request key's components"
        [ "global-properties", "4dc2ad2376bfe544d9e4e970336e875be9a7838400c1e87530bf2edaa4bbc553"
          "projects", "a8bd81deda5d3cf8ab75a57120f3217a4a42f01c80064dfe3d107f67f939d625"
          "root", "/golden/repo"
          "sdk", "/sdk/10.0.401"
          "toolchain", goldenOptionsToolchain ]
        requestKey.Inputs


    let optionsKey =
        OptionsRecords.optionsKey
            request
            { Reads = []
              Probes = []
              Listings = []
              EnvNames = [] }
            []

    golden
        "The options-inputs request key and the options-record key"
        ("ak1:5d27b2fa53c41b9ee7f7c0c9aa47f96a1f706bbd317debf3010ffaa08fc826e3",
         "ak1:5041e894015bfdec089ad7242230414053e82970b9a1ce97ace1cc437c536b49")
        (requestKey.Value.Text, optionsKey.Value.Text)

// AUTOMATION-1602 (b): the portable request key names no checkout root, so one tree's
// request is the same key in every checkout of it, whatever the root's length.
[<Fact(Timeout = 15000)>]
let ``GOLDEN: the portable options request key names no root and is one key for every checkout of a tree`` () =
    let toolchain =
        OptionsInputs.toolchainOf
            "Ionide.ProjInfo@0.74.2.0/55555555-5555-5555-5555-555555555555"
            "Microsoft.Build@15.1.0.0/66666666-6666-6666-6666-666666666666"
            OptionsInputs.OptionsRecordSchema

    let requestAt (root: string) : OptionsRecords.Request =
        { Root = root
          Projects = [ root + "/A.fsproj" ]
          Toolchain = toolchain
          Sdk = "/sdk/10.0.401"
          GlobalProperties = [ "Configuration", "Debug"; "SolutionDir", root + "/" ] }

    let short = OptionsPortable.requestKey (requestAt "/golden/repo")

    let long =
        OptionsPortable.requestKey (requestAt "/golden/a-much-longer-checkout-root")

    test <@ short = long @>

    golden
        "The portable options request key's components"
        [ "global-properties", "8d9179da762f77804197e51dff6612241a9d227f920f90a845f45a5a146903a8"
          "path-token", "fshw-root-token-v1"
          "projects", "f3274e397cf3720ae9545289c63dcdc8d15a7a3a1ee10228ee61d78cbf63a194"
          "sdk", "/sdk/10.0.401"
          "toolchain", goldenOptionsToolchain ]
        short.Inputs

    golden
        "The portable options request key"
        "ak1:a6fed0d49ebcdd7e3f9b8049f84661d371cfce459bee414b7ed216b45e67fbd2"
        short.Value.Text

// --- the golden test-prune analysis-record key ---

let private testPruneCore (version: string) =
    Reflection.AssemblyName($"TestPrune.Core, Version=%s{version}")

[<Fact(Timeout = 15000)>]
let ``an analysis identity moves with TestPrune.Core's release and the schema, and nothing else`` () =
    let identity =
        FsHotWatch.TestPrune.AnalysisRecords.analyzerIdentityOf (testPruneCore "16.1.1.0")

    let schema = FsHotWatch.TestPrune.AnalysisRecords.AnalysisSchema
    test <@ identity schema = identity schema @>
    test <@ identity (schema + "-bumped") <> identity schema @>

    test
        <@
            FsHotWatch.TestPrune.AnalysisRecords.analyzerIdentityOf (testPruneCore "16.2.0.0") schema
            <> identity schema
        @>

[<Fact(Timeout = 15000)>]
let ``GOLDEN: the analysis-record key of a fixed analysis and every component it is folded from`` () =
    let identity =
        FsHotWatch.TestPrune.AnalysisRecords.analyzerIdentityOf
            (testPruneCore "16.1.1.0")
            FsHotWatch.TestPrune.AnalysisRecords.AnalysisSchema

    golden
        "The analyser identity an analysis record is keyed by"
        "TestPrune.Core@16.1.1.0;fshw-testprune-analysis-schema=fshw-testprune-analysis-semantics-v1"
        identity

    let key =
        FsHotWatch.TestPrune.AnalysisRecords.keyWith identity "analysis-digest" "src/P.fs" "P"

    golden
        "The analysis-record key's components"
        [ "analysis", "analysis-digest"
          "analyzer", identity
          "file", "src/P.fs"
          "project", "P" ]
        key.Inputs

    golden
        "The analysis-record key"
        "ak1:212ef02189cf452edbe9b4acab8d2edb9d1e5c092e21211bc8e0cfbedde2af29"
        key.Value.Text

    // `keyOf` is `keyWith` under this process's analyser.
    let live =
        FsHotWatch.TestPrune.AnalysisRecords.keyOf "analysis-digest" "src/P.fs" "P"

    let expected =
        FsHotWatch.TestPrune.AnalysisRecords.keyWith
            FsHotWatch.TestPrune.AnalysisRecords.analyzerIdentity
            "analysis-digest"
            "src/P.fs"
            "P"

    test <@ live = expected @>
