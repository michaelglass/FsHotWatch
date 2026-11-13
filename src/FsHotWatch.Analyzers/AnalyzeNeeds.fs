/// The analyzer action as a Need trace, in SHADOW: the pilot of S1/S2 of ADR-059 §35
/// (AUTOMATION-1636).
///
/// The action is written with `action { … }` and reads its inputs only through `Need.*`: its
/// file, the outputs produced upstream of it (the check's signatures, the project's options,
/// the analyzer set and its configuration, the host build), and the environment oracles
/// (the SDK, the variables below, the tool manifest, the platform). Its key is what it
/// needed.
///
/// For every analysis the plugin computes today's task key AND this trace, and compares each
/// with the same file's previous analysis in this process. Two correct keys move together;
/// where one moved and the other held, the shadow logs a line starting `need-shadow
/// disagree` naming which inputs differ (`render`). The Need key DECIDES NOTHING: the plugin
/// answers with today's key whatever the shadow says.
module FsHotWatch.Analyzers.AnalyzeNeeds

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open FsHotWatch.Core

/// The pilot's record kind: the Need trace of one analysis.
[<Sealed>]
type AnalyzeNeedsKind private () =
    interface IRecordKind with
        static member Name = "analyze-needs"
        static member SchemaVersion = 1

/// Every line the shadow logs starts with this, then ` disagree ` or ` error `.
[<Literal>]
let LogPrefix = "need-shadow"

/// What one analysis was handed: its file and the text of every output it consumes, as the
/// plugin already computes them for today's key.
type AnalysisInputs =
    {
        /// The file analysed.
        File: RepoRelativePath
        /// The source text the check analysed: the file as the action reads it.
        Source: string
        /// The check's own signature (its diagnostics).
        FcsSignature: string
        /// The shape of what the check resolved in other files.
        UsedSignatures: string
        /// The project options, relative to the repository (`AnalyzeRecords.optionsHashOf`).
        Options: string
        /// The configured analyzer paths.
        AnalyzerPaths: string
        /// The loaded analyzer set's bytes.
        AnalyzerInputs: string
        /// The analyzer configuration files of the file's chain.
        AnalyzerConfig: string
        /// The severity at which a finding fails.
        FailOnSeverity: string
        /// The checker build (FCS and FSharp.Core).
        Checker: string
        /// The analyzer SDK release.
        AnalyzerSdk: string
    }

/// The upstream outputs the action consumes, each by its label. The labels are constants,
/// parsed once here; `AnalyzeNeedsTests` pins the full list, so a label that failed to
/// parse (and was dropped) fails that test.
let private upstream: (OutputLabel * (AnalysisInputs -> string)) list =
    let reads: (string * (AnalysisInputs -> string)) list =
        [ "check/fcs-signature", _.FcsSignature
          "check/used-signatures", _.UsedSignatures
          "project/options", _.Options
          "analyzers/paths", _.AnalyzerPaths
          "analyzers/inputs", _.AnalyzerInputs
          "analyzers/config", _.AnalyzerConfig
          "analyzers/fail-on-severity", _.FailOnSeverity
          "host/checker", _.Checker
          "host/analyzer-sdk", _.AnalyzerSdk ]

    reads
    |> List.choose (fun (text, read) ->
        OutputLabel.parse text
        |> Result.toOption
        |> Option.map (fun label -> label, read))

/// The environment variables that matter to an analysis: where the SDK's reference
/// assemblies resolve from, and where analyzer packages resolve from. Pinned by the same
/// test.
let private variables: EnvVarName list =
    [ "DOTNET_ROOT"; "NUGET_PACKAGES" ]
    |> List.choose (EnvVarName.parse >> Result.toOption)

let rec private needEach (needs: Action<unit> list) : Action<unit> =
    match needs with
    | [] -> action { return () }
    | need :: rest ->
        action {
            let! () = need
            return! needEach rest
        }

let private ignored (need: Action<'T>) : Action<unit> =
    action {
        let! _ = need
        return ()
    }

/// The analyzer action's reads, as the key-deriving part of the action: everything it
/// needs to analyse `file`.
let analysis (file: RepoRelativePath) : Action<unit> =
    needEach (
        [ ignored (Need.file file) ]
        @ (upstream |> List.map (fst >> Need.upstream >> ignored))
        @ [ ignored (Need.oracle Oracle.dotnetSdk)
            ignored (Need.oracle Oracle.toolManifest)
            ignored (Need.oracle Oracle.platform) ]
        @ (variables |> List.map (Need.env >> ignored))
    )

/// One analysis as the shadow compares it: today's key and its terms, and the Need trace.
type Observation =
    {
        /// Today's key's terms, by label: the host's and the analysis's.
        TodayTerms: Map<string, string>
        /// What the action needed. Its key (`NeedTrace.key`) is a function of it alone, so
        /// two traces are equal exactly when their keys are.
        Needs: NeedTrace
    }

let private utf8 (text: string) = Encoding.UTF8.GetBytes text

/// The world one analysis reads: its file is the source the check analysed (nothing else
/// is a file it reads), its upstream outputs are `inputs`', and the environment is `oracles`.
let internal worldOf (oracles: Oracles) (inputs: AnalysisInputs) : World =
    let outputs =
        upstream
        |> List.map (fun (label, read) -> label, OutputDigest.ofBytes (utf8 (read inputs)))
        |> Map.ofList

    { ReadFile =
        fun path ->
            if path = inputs.File then
                Some(utf8 inputs.Source)
            else
                None
      Upstream = fun label -> Map.tryFind label outputs
      Oracles = oracles }

/// The analysis `inputs` describe, observed: today's `todayTerms` and the action's trace.
let observe
    (oracles: Oracles)
    (inputs: AnalysisInputs)
    (todayTerms: (string * string) list)
    : Result<Observation, NeedError> =
    Action.run (worldOf oracles inputs) (analysis inputs.File)
    |> Result.map (fun ((), needs) ->
        { TodayTerms = Map.ofList todayTerms
          Needs = needs })

/// How a file's analysis compares with its previous one. There is no "first seen" case:
/// a comparison needs two analyses, so a first sighting is `Observed.FirstSeen`, and
/// `compare`, `render` and `describe` cannot be handed one (AUTOMATION-1635).
[<RequireQualifiedAccess>]
type Comparison =
    /// Both keys held, or both moved.
    | Agree
    /// Today's key held while the Need key moved: today's key may have missed these inputs.
    | NeedKeyMoved of changes: NonEmpty<NeedChange>
    /// Today's key moved while the Need key held: the action does not declare these terms.
    | TodayKeyMoved of terms: NonEmpty<string>

/// What the shadow made of an analysis: the file's first in this process, or compared with
/// the previous one.
[<RequireQualifiedAccess>]
type Observed =
    /// No previous analysis of the file in this process: nothing to compare.
    | FirstSeen
    /// Compared with the file's previous analysis.
    | Compared of Comparison

/// The labels whose values differ between two term maps (present in one only included).
let private changedTerms (before: Map<string, string>) (after: Map<string, string>) : string list =
    Set.union (before.Keys |> Set.ofSeq) (after.Keys |> Set.ofSeq)
    |> Set.toList
    |> List.filter (fun label -> Map.tryFind label before <> Map.tryFind label after)

/// `after` against `before`, the same file's previous analysis.
let compare (before: Observation) (after: Observation) : Comparison =
    match
        NonEmpty.ofList (NeedTrace.diff before.Needs after.Needs),
        NonEmpty.ofList (changedTerms before.TodayTerms after.TodayTerms)
    with
    | None, None
    | Some _, Some _ -> Comparison.Agree
    | Some changes, None -> Comparison.NeedKeyMoved changes
    | None, Some terms -> Comparison.TodayKeyMoved terms

/// The comparison in one word or line, for test failures.
let describe (comparison: Comparison) : string =
    match comparison with
    | Comparison.Agree -> "agree"
    | Comparison.NeedKeyMoved changes ->
        "need moved: " + String.Join(",", NonEmpty.toList changes |> List.map _.Subject)
    | Comparison.TodayKeyMoved terms -> "today moved: " + String.Join(",", NonEmpty.toList terms)

/// The log line for a disagreement; `None` when there is nothing to say. Its form is fixed:
///   need-shadow disagree file=<path> today=held need=moved inputs=<subject>,…
///   need-shadow disagree file=<path> today=moved need=held inputs=<term>,…
let render (file: RepoRelativePath) (comparison: Comparison) : string option =
    let line today need (inputs: string list) =
        Some
            $"%s{LogPrefix} disagree file=%s{file.Text} today=%s{today} need=%s{need} inputs=%s{String.Join(',', inputs)}"

    match comparison with
    | Comparison.Agree -> None
    | Comparison.NeedKeyMoved changes -> line "held" "moved" (NonEmpty.toList changes |> List.map _.Subject)
    | Comparison.TodayKeyMoved terms -> line "moved" "held" (NonEmpty.toList terms)

/// The log line for an analysis whose action produced no trace.
let renderError (file: RepoRelativePath) (error: NeedError) : string =
    $"%s{LogPrefix} error file=%s{file.Text} reason=%s{error.ToString()}"

/// Each file's last observation in this process, compared with the next and logged on a
/// disagreement through `log`.
type Shadow(log: string -> unit) =
    let seen = ConcurrentDictionary<RepoRelativePath, Observation>()

    /// Compares `observation` with `file`'s previous one, logs a disagreement, and keeps it.
    member _.Observe(file: RepoRelativePath, observation: Observation) : Observed =
        let observed =
            match seen.TryGetValue file with
            | true, previous -> Observed.Compared(compare previous observation)
            | false, _ -> Observed.FirstSeen

        seen[file] <- observation

        match observed with
        | Observed.Compared comparison -> render file comparison |> Option.iter log
        | Observed.FirstSeen -> ()

        observed

    /// Logs that `file`'s action produced no trace.
    member _.Fail(file: RepoRelativePath, error: NeedError) : unit = log (renderError file error)

    /// `Record` what `observe` returns; if it throws, log `need-shadow error` naming the
    /// exception instead of raising it.
    member this.Guard(file: RepoRelativePath, observe: unit -> Result<Observation, NeedError>) : unit =
        // The shadow decides nothing, so nothing it throws may reach the caller's key.
        let observed =
            try
                Ok(observe ())
            with ex ->
                Error ex

        match observed with
        | Ok result -> this.Record(file, result)
        | Error ex -> log $"%s{LogPrefix} error file=%s{file.Text} reason=%s{ex.GetType().Name}: %s{ex.Message}"

    /// `Observe` an analysis that produced a trace, `Fail` one that did not.
    member this.Record(file: RepoRelativePath, observed: Result<Observation, NeedError>) : unit =
        match observed with
        | Ok observation -> this.Observe(file, observation) |> ignore
        | Error error -> this.Fail(file, error)

/// `ask`, answered once per dispatch batch (`DispatchBatch.Current`): a run asks again, the
/// events of one batch share its answer. Outside a batch (0) every call asks.
let private oncePerBatch (ask: unit -> 'T) : unit -> 'T =
    let mutable memo: (int64 * 'T) option = None

    fun () ->
        let batch = FsHotWatch.PluginFramework.DispatchBatch.Current

        match memo with
        | Some(asked, answer) when batch <> 0L && asked = batch -> answer
        | _ ->
            let answer = ask ()
            memo <- Some(batch, answer)
            answer

/// The oracles as this process answers them, for the repository at `root`: the SDK as
/// `dotnetSdk` resolves it, the variables from this process's environment, the tool manifest
/// at `root/.config/dotnet-tools.json`, the platform as .NET names it. Asked again on every
/// run; the SDK and the manifest, which read files, once per dispatch batch.
let liveOracles (dotnetSdk: unit -> string) (root: string) : Oracles =
    let manifest = Path.Combine(root, ".config", "dotnet-tools.json")

    { DotnetSdk = oncePerBatch dotnetSdk
      Variable = fun name -> Option.ofObj (Environment.GetEnvironmentVariable name.Text)
      ToolManifest =
        oncePerBatch (fun () ->
            if File.Exists manifest then
                Some(File.ReadAllBytes manifest)
            else
                None)
      Platform = fun () -> Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier }
