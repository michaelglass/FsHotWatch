/// The analyzer action as a Need trace, in SHADOW (AUTOMATION-1636, the S1/S2 pilot of
/// ADR-059 §35).
///
/// For every analysis the plugin computes both today's task key and the key the action's
/// `Need.*` trace derives, and logs where they disagree: one moved while the other held. The
/// Need key decides nothing. The positive control is an input today's key omits (the
/// project options): edit only them, and today's key holds while the Need key moves.
module FsHotWatch.Tests.AnalyzeNeedsTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Core
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.Analyzers
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

let private ok =
    function
    | Ok value -> value
    | Result.Error error -> failwith $"expected Ok, got Error %s{error.ToString()}"

let private path = RepoRelativePath.parse >> ok

/// Oracles with fixed answers; `manifest` is read at every ask.
let private oraclesWith (manifest: unit -> string) : Oracles =
    { DotnetSdk = fun () -> "10.0.100"
      Variable = fun _ -> None
      ToolManifest = fun () -> Some(System.Text.Encoding.UTF8.GetBytes(manifest ()))
      Platform = fun () -> "osx-arm64" }

let private fixedOracles = oraclesWith (fun () -> "{}")

/// What an analysis was handed, every input spelled out.
let private inputs: AnalyzeNeeds.AnalysisInputs =
    { File = path "src/A.fs"
      Source = "let x = 1\n"
      FcsSignature = "fcs"
      UsedSignatures = "used"
      Options = "options"
      AnalyzerPaths = "paths"
      AnalyzerInputs = "inputs"
      AnalyzerConfig = "config"
      FailOnSeverity = "warning"
      Checker = "checker"
      AnalyzerSdk = "sdk" }

let private observe (today: (string * string) list) (analysis: AnalyzeNeeds.AnalysisInputs) =
    AnalyzeNeeds.observe fixedOracles analysis today |> ok

let private todayTerms = [ "source", "let x = 1\n"; "file", "src/A.fs" ]

// ---------------------------------------------------------------------------------------
// The action's needs

[<Fact>]
let ``the analyzer action needs its file, every upstream output and the environment oracles`` () =
    let observation = observe todayTerms inputs

    let subjects = NeedTrace.needs observation.Needs |> List.map Needed.subject

    test
        <@
            subjects = [ "file:src/A.fs"
                         "oracle:dotnet-sdk"
                         "oracle:env:DOTNET_ROOT"
                         "oracle:env:NUGET_PACKAGES"
                         "oracle:platform"
                         "oracle:tool-manifest"
                         "upstream:analyzers/config"
                         "upstream:analyzers/fail-on-severity"
                         "upstream:analyzers/inputs"
                         "upstream:analyzers/paths"
                         "upstream:check/fcs-signature"
                         "upstream:check/used-signatures"
                         "upstream:host/analyzer-sdk"
                         "upstream:host/checker"
                         "upstream:project/options" ]
        @>

    // The action's key is what it needed.
    let key: ActionKey<AnalyzeNeeds.AnalyzeNeedsKind> =
        NeedTrace.key observation.Needs |> ok

    test <@ ActionKey.kindName key = "analyze-needs" @>

// ---------------------------------------------------------------------------------------
// The comparison: one key moved while the other held

[<Fact>]
let ``nothing to compare the first time, and two keys that hold or move together agree`` () =
    let one = observe todayTerms inputs
    test <@ AnalyzeNeeds.compare one one = AnalyzeNeeds.Comparison.Agree @>

    let edited =
        observe [ "source", "let x = 2\n"; "file", "src/A.fs" ] { inputs with Source = "let x = 2\n" }

    test <@ AnalyzeNeeds.compare one edited = AnalyzeNeeds.Comparison.Agree @>

[<Fact>]
let ``the Need key moving under a held today key names the inputs today's key missed`` () =
    let one = observe todayTerms inputs

    let other =
        observe
            todayTerms
            { inputs with
                Options = "options --define:X" }

    match AnalyzeNeeds.compare one other with
    | AnalyzeNeeds.Comparison.NeedKeyMoved changes ->
        test <@ NonEmpty.toList changes |> List.map (fun change -> change.Subject) = [ "upstream:project/options" ] @>
    | other -> failwith $"expected NeedKeyMoved, got %s{AnalyzeNeeds.describe other}"

[<Fact>]
let ``today's key moving under a held Need key names the terms the Need key does not declare`` () =
    let one = observe todayTerms inputs
    let other = observe (("plugin-version", "v9") :: todayTerms) inputs

    test
        <@
            AnalyzeNeeds.compare one other = AnalyzeNeeds.Comparison.TodayKeyMoved(
                { Head = "plugin-version"; Tail = [] }
            )
        @>

[<Fact>]
let ``the log line has a stable prefix, the file, which key moved and what differs`` () =
    let one = observe todayTerms inputs

    let options =
        observe
            todayTerms
            { inputs with
                Options = "options --define:X" }

    let schema = observe (("plugin-version", "v9") :: todayTerms) inputs

    test <@ AnalyzeNeeds.LogPrefix = "need-shadow" @>

    test
        <@
            AnalyzeNeeds.render inputs.File (AnalyzeNeeds.compare one options) = Some
                "need-shadow disagree file=src/A.fs today=held need=moved inputs=upstream:project/options"
        @>

    test
        <@
            AnalyzeNeeds.render inputs.File (AnalyzeNeeds.compare one schema) = Some
                "need-shadow disagree file=src/A.fs today=moved need=held inputs=plugin-version"
        @>

    test <@ AnalyzeNeeds.render inputs.File AnalyzeNeeds.Comparison.Agree = None @>
    test <@ AnalyzeNeeds.render inputs.File AnalyzeNeeds.Comparison.FirstSeen = None @>
    test <@ AnalyzeNeeds.describe AnalyzeNeeds.Comparison.Agree = "agree" @>
    test <@ AnalyzeNeeds.describe AnalyzeNeeds.Comparison.FirstSeen = "first-seen" @>

[<Fact>]
let ``the shadow remembers each file's last observation and logs only disagreements`` () =
    let lines = ResizeArray<string>()
    let shadow = AnalyzeNeeds.Shadow(lines.Add)
    let one = observe todayTerms inputs

    test <@ shadow.Observe(inputs.File, one) = AnalyzeNeeds.Comparison.FirstSeen @>
    test <@ shadow.Observe(inputs.File, one) = AnalyzeNeeds.Comparison.Agree @>
    test <@ lines.Count = 0 @>

    let moved =
        observe
            todayTerms
            { inputs with
                Options = "options --define:X" }

    test <@ shadow.Observe(inputs.File, moved) <> AnalyzeNeeds.Comparison.Agree @>

    test
        <@
            List.ofSeq lines = [ "need-shadow disagree file=src/A.fs today=held need=moved inputs=upstream:project/options" ]
        @>

    // Another file is its own history.
    test <@ shadow.Observe(path "src/B.fs", one) = AnalyzeNeeds.Comparison.FirstSeen @>

[<Fact>]
let ``an action that cannot run says why, under the same prefix`` () =
    test
        <@
            AnalyzeNeeds.renderError inputs.File (NeedError.Inconsistent "file:src/A.fs") = "need-shadow error file=src/A.fs reason='file:src/A.fs' answered differently within one run"
        @>

    let lines = ResizeArray<string>()
    let shadow = AnalyzeNeeds.Shadow(lines.Add)
    shadow.Record(inputs.File, Result.Error(NeedError.Inconsistent "file:src/A.fs"))
    shadow.Record(inputs.File, Ok(observe todayTerms inputs))

    test
        <@
            List.ofSeq lines = [ "need-shadow error file=src/A.fs reason='file:src/A.fs' answered differently within one run" ]
        @>

// ---------------------------------------------------------------------------------------
// The pilot in the plugin: both keys for every analysis, the Need key deciding nothing

/// A file under `root/src` whose result carries `options`' extra flags.
let private resultIn (root: string) (otherOptions: string array) =
    let file = Path.Combine(root, "src", "A.fs")
    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
    File.WriteAllText(file, "let x = 1\n")
    let result = fakeFileCheckResult file

    { result with
        Source = "let x = 1\n"
        ProjectOptions =
            { result.ProjectOptions with
                OtherOptions = Array.append result.ProjectOptions.OtherOptions otherOptions } }

let private shadowed (root: string) (oracles: Oracles) (lines: ResizeArray<string>) =
    createShadowedWithSeams
        (AnalyzeNeeds.Shadow lines.Add)
        oracles
        AnalyzerHost.current
        None
        (Some root)
        []
        Map.empty
        None
        DiagnosticSeverity.Warning
        None
        runSafely

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``positive control: an options-only edit holds today's key, moves the Need key, and is logged`` () =
    withTempDir "need-shadow-options" (fun root ->
        let lines = ResizeArray<string>()
        let handler = shadowed root fixedOracles lines
        let before = handler.CacheKey.Value handler.Init (FileChecked(resultIn root [||]))

        let after =
            handler.CacheKey.Value handler.Init (FileChecked(resultIn root [| "--define:SHADOW_CONTROL" |]))

        // Today's key omits the project options, so it holds...
        test <@ before.IsSome && before = after @>
        // ...and the shadow names the input it missed. The Need key decided nothing: the
        // key the plugin answers with is today's.
        test
            <@
                List.ofSeq lines = [ "need-shadow disagree file=src/A.fs today=held need=moved inputs=upstream:project/options" ]
            @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``negative controls: an unchanged file logs nothing, and an edit both keys see logs nothing`` () =
    withTempDir "need-shadow-agree" (fun root ->
        let lines = ResizeArray<string>()
        let handler = shadowed root fixedOracles lines

        let key result =
            handler.CacheKey.Value handler.Init (FileChecked result)

        key (resultIn root [||]) |> ignore
        key (resultIn root [||]) |> ignore

        let edited =
            { resultIn root [||] with
                Source = "let x = 2\n" }

        key edited |> ignore
        test <@ lines.Count = 0 @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``an oracle is asked again for every analysis: a tool manifest edit is a disagreement`` () =
    withTempDir "need-shadow-oracle" (fun root ->
        let lines = ResizeArray<string>()
        let mutable manifest = "{\"tools\":{}}"
        let handler = shadowed root (oraclesWith (fun () -> manifest)) lines

        let key () =
            handler.CacheKey.Value handler.Init (FileChecked(resultIn root [||]))

        let before = key ()
        manifest <- "{\"tools\":{\"fantomas\":{}}}"
        let after = key ()
        test <@ before = after @>

        test
            <@
                List.ofSeq lines = [ "need-shadow disagree file=src/A.fs today=held need=moved inputs=oracle:tool-manifest" ]
            @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the live oracles answer from this process and this repository`` () =
    withTempDir "need-shadow-live" (fun root ->
        Directory.CreateDirectory(Path.Combine(root, ".config")) |> ignore
        File.WriteAllText(Path.Combine(root, ".config", "dotnet-tools.json"), "{}")
        let live = AnalyzeNeeds.liveOracles (fun () -> "sdk-identity") root
        test <@ live.DotnetSdk() = "sdk-identity" @>
        test <@ live.ToolManifest() = Some(System.Text.Encoding.UTF8.GetBytes "{}") @>
        test <@ live.Platform() = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier @>

        let path = EnvVarName.parse "PATH" |> ok
        test <@ live.Variable path = Option.ofObj (System.Environment.GetEnvironmentVariable "PATH") @>

        File.Delete(Path.Combine(root, ".config", "dotnet-tools.json"))
        test <@ live.ToolManifest() = None @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a file outside the repository is not shadowed`` () =
    withTempDir "need-shadow-outside" (fun root ->
        withTempDir "need-shadow-elsewhere" (fun elsewhere ->
            let lines = ResizeArray<string>()
            let handler = shadowed root fixedOracles lines

            handler.CacheKey.Value handler.Init (FileChecked(resultIn elsewhere [||]))
            |> ignore

            handler.CacheKey.Value handler.Init (FileChecked(resultIn elsewhere [| "--define:X" |]))
            |> ignore

            test <@ lines.Count = 0 @>))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``the live SDK and manifest are asked once per dispatch batch, and again in the next`` () =
    withTempDir "need-shadow-batch" (fun root ->
        let mutable asked = 0

        let live =
            AnalyzeNeeds.liveOracles
                (fun () ->
                    asked <- asked + 1
                    $"sdk-%d{asked}")
                root

        let within batch (ask: unit -> 'T) =
            FsHotWatch.PluginFramework.DispatchBatch.Within(batch, ask) |> fst

        let first = within 9001L (fun () -> live.DotnetSdk(), live.DotnetSdk())
        test <@ first = ("sdk-1", "sdk-1") @>
        let next = within 9002L live.DotnetSdk
        test <@ next = "sdk-2" @>

        let manifest = Path.Combine(root, ".config", "dotnet-tools.json")
        Directory.CreateDirectory(Path.GetDirectoryName manifest) |> ignore
        File.WriteAllText(manifest, "{}")
        let before = within 9003L live.ToolManifest
        File.Delete manifest
        // The same batch keeps its answer; the next asks again.
        let sameBatch = within 9003L live.ToolManifest
        let nextBatch = within 9004L live.ToolManifest
        test <@ before = Some(System.Text.Encoding.UTF8.GetBytes "{}") @>
        test <@ sameBatch = before @>
        test <@ nextBatch = None @>)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a shadow that throws never touches today's key: it says why and the key is answered`` () =
    withTempDir "need-shadow-throws" (fun root ->
        let lines = ResizeArray<string>()
        let handler = shadowed root fixedOracles lines
        // A result checked against no project options: hashing them throws.
        let result =
            { resultIn root [||] with
                ProjectOptions = Unchecked.defaultof<_> }

        let shadowedKey = handler.CacheKey.Value handler.Init (FileChecked result)
        test <@ shadowedKey.IsSome @>
        test <@ lines.Count = 1 && lines[0].StartsWith "need-shadow error file=src/A.fs reason=" @>)

[<Fact>]
let ``describe names which key moved and what differs, and the world serves only the analysed file`` () =
    let one = observe todayTerms inputs

    let options =
        observe
            todayTerms
            { inputs with
                Options = "options --define:X" }

    let schema = observe (("plugin-version", "v9") :: todayTerms) inputs
    test <@ AnalyzeNeeds.describe (AnalyzeNeeds.compare one options) = "need moved: upstream:project/options" @>
    test <@ AnalyzeNeeds.describe (AnalyzeNeeds.compare one schema) = "today moved: plugin-version" @>

    let world = AnalyzeNeeds.worldOf fixedOracles inputs
    test <@ world.ReadFile inputs.File = Some(System.Text.Encoding.UTF8.GetBytes inputs.Source) @>
    test <@ world.ReadFile(path "src/B.fs") = None @>
