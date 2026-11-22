/// A replayed file's status summary is its plugin's own account, never one the framework
/// writes for every plugin alike (AUTOMATION-1836).
module FsHotWatch.Tests.ReplayedSummaryTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginFramework
open FsHotWatch.PluginHost
open FsHotWatch.Tests.TestHelpers

let private terminalSummary (host: PluginHost) (name: string) =
    match host.GetStatus name with
    | Some(Completed(_, v)) -> Some v.Summary
    | Some(Failed(_, _, v)) -> Some v.Summary
    | Some _
    | None -> None

/// Seed a per-file entry for `file` under `plugin`, completed with no findings.
let private seed (cache: TaskCache.ITaskCache) (plugin: string) (key: ContentHash) (file: string) =
    cache.Set
        { Plugin = plugin
          File = Some(CachePathIdentity.keyOf (Some "/tmp") file) }
        key
        { CacheKey = key
          Errors = [ file, [] ]
          Status = TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 5.0)
          EmittedEvents = [] }

/// A state that says, in words no other plugin uses, how its run met its files.
type private OwnWords =
    { Files: int }

    interface IFileReplayState<OwnWords> with
        member this.ReplaySummary tally =
            $"own words over %d{this.Files} files: %d{tally.Examined} examined, %d{tally.Replayed} replayed"

        member this.Replayed _ _ _ _ = { Files = this.Files + 1 }

/// A per-file plugin whose every event is a cache hit, over `init`.
let private replayOnly<'State> (name: string) (init: 'State) (key: ContentHash) : PluginHandler<'State, unit> =
    { Name = PluginName.create name
      Init = init
      Update = fun _ _ _ -> failwith "every event here is a cache hit"
      PrepareCommit = None
      Commands = []
      Subscriptions = Set.singleton SubscribeFileChecked
      CacheKey = Some(fun _ _ -> Some key)
      Teardown = None }

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a per-file replay reports the summary the replayed state gives, marked cached`` () =
    let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
    host.WorkStore.PublishProjectModel fixtureModel
    let key = ContentHash.create "own-words"

    for file in [ "/tmp/replayed/A.fs"; "/tmp/replayed/B.fs" ] do
        seed cache "own-words" key file

    host.RegisterHandler(replayOnly "own-words" { Files = 0 } key)
    host.EmitFileChecked(fakeFileCheckResult "/tmp/replayed/A.fs")
    host.EmitFileChecked(fakeFileCheckResult "/tmp/replayed/B.fs")
    waitForQuiescent host 15000

    // Asked of the state the second replay LEFT (two files), with this run's tally.
    test <@ terminalSummary host "own-words" = Some "own words over 2 files: 0 examined, 2 replayed (cached)" @>

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``a per-file replay of a state with no replayed summary reports no summary for it`` () =
    // The framework has no words of its own to put in a plugin's mouth. The replayed
    // findings still land in the ledger.
    let cache = TaskCache.InMemoryTaskCache() :> TaskCache.ITaskCache
    let host = PluginHost(Unchecked.defaultof<_>, "/tmp", taskCache = cache)
    host.WorkStore.PublishProjectModel fixtureModel
    let key = ContentHash.create "silent"
    let file = "/tmp/replayed/Silent.fs"

    cache.Set
        { Plugin = "silent"
          File = Some(CachePathIdentity.keyOf (Some "/tmp") file) }
        key
        { CacheKey = key
          Errors = [ file, [ ErrorEntry.error "replayed finding" ] ]
          Status = TaskCache.CachedFileCompleted(TimeSpan.FromMilliseconds 5.0)
          EmittedEvents = [] }

    host.RegisterHandler(replayOnly "silent" 0 key)
    host.EmitFileChecked(fakeFileCheckResult file)
    waitForQuiescent host 15000

    test <@ host.GetErrorsByPlugin "silent" |> Map.containsKey file @>
    test <@ terminalSummary host "silent" = None @>

// ---------------------------------------------------------------------------
// Over every shipped plugin that keeps a per-file cache
// ---------------------------------------------------------------------------

/// Every shipped plugin handler, type-erased to what this property reads: its name, whether
/// it caches `FileChecked` per file, and its initial state.
let private shippedPlugins (root: string) : (string * bool * obj) list =
    let erase (handler: PluginHandler<'S, 'M>) =
        PluginName.value handler.Name,
        handler.Subscriptions.Contains SubscribeFileChecked && handler.CacheKey.IsSome,
        box handler.Init

    [ erase (FsHotWatch.Analyzers.AnalyzersPlugin.create None [] None DiagnosticSeverity.Hint)
      erase (FsHotWatch.Lint.LintPlugin.create None None None None)
      erase (
          FsHotWatch.TestPrune.TestPrunePlugin.create
              (IO.Path.Combine(root, "replayed-summary.db"))
              root
              None
              None
              None
              None
              None
              []
      ) ]

/// A state's replayed summary, through the one interface the framework asks.
let private replaySummaryOf (state: obj) (tally: FileReplayTally) : string option =
    let iface =
        state.GetType().GetInterfaces()
        |> Array.tryFind (fun i ->
            i.IsGenericType
            && i.GetGenericTypeDefinition() = typedefof<IFileReplayState<obj>>)

    iface
    |> Option.map (fun i -> i.GetMethod("ReplaySummary").Invoke(state, [| box tally |]) :?> string)

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``every shipped per-file plugin's replayed summary is its own, whatever the tally`` () =
    withTempDir "replayed-summary" (fun root ->
        let perFile = shippedPlugins root |> List.filter (fun (_, perFile, _) -> perFile)

        // Positive control: the plugins this ticket is about are in the list.
        test
            <@
                perFile |> List.map (fun (name, _, _) -> name) |> Set.ofList = Set.ofList
                    [ "analyzers"; "lint"; "test-prune" ]
            @>

        let tallies =
            Gen.zip (Gen.choose (0, 5000)) (Gen.choose (0, 5000))
            |> Gen.map (fun (examined, replayed) ->
                { Examined = examined
                  Replayed = replayed })
            |> Arb.fromGen

        Check.QuickThrowOnFailure(
            Prop.forAll tallies (fun tally ->
                let summaries =
                    perFile |> List.map (fun (name, _, init) -> name, replaySummaryOf init tally)

                // Every one has a summary of its own: no plugin falls back to nothing.
                summaries |> List.forall (fun (_, summary) -> summary.IsSome)
                // No two plugins say the same thing about the same tally.
                && (summaries |> List.map snd |> List.distinct |> List.length) = summaries.Length
                // Each says how this run met its files.
                && summaries
                   |> List.forall (fun (_, summary) ->
                       summary.Value.Contains(string tally.Replayed)
                       && summary.Value.Contains(string tally.Examined)))
        ))

[<Fact(Timeout = SyncHangBackstopMs)>]
let ``test-prune's replayed summary names the run its evidence rests on and that run's tally`` () =
    withTempDir "replayed-summary-tp" (fun root ->
        let handler =
            FsHotWatch.TestPrune.TestPrunePlugin.create
                (IO.Path.Combine(root, "tp.db"))
                root
                None
                None
                None
                None
                None
                []

        let summaryOf (state: FsHotWatch.TestPrune.PluginState.TestPruneState) =
            (state :> IFileReplayState<FsHotWatch.TestPrune.PluginState.TestPruneState>).ReplaySummary
                { Examined = 0; Replayed = 2861 }

        test
            <@
                summaryOf handler.Init = "symbol analysis: 0 files examined, 2861 replayed from cache; tests: no run completed in this daemon"
            @>

        let runId = Guid.NewGuid()
        let runText = runId.ToString "N"

        let results =
            Map.ofList
                [ "A.Tests.fsproj", TestsPassed("ok", false, TimeSpan.Zero)
                  "B.Tests.fsproj", TestsPassed("ok", false, TimeSpan.Zero)
                  "C.Tests.fsproj", TestsFailed("red", false, TimeSpan.Zero) ]

        let projects = results |> Map.keys |> Set.ofSeq

        let earned =
            EarnedEvidence.fromCompletion
                runId
                (Some 1L)
                (Some 1L)
                None
                (projects |> Seq.map (fun p -> p, p) |> Map.ofSeq)
                projects
                0
                None
                { RunId = runId
                  TotalElapsed = TimeSpan.Zero
                  Outcome = Normal
                  Results = results
                  Verification = RunVerification.ofResults results }

        test <@ earned.IsSome @>

        test
            <@
                summaryOf { handler.Init with Earned = earned } = $"symbol analysis: 0 files examined, 2861 replayed from cache; tests: run %s{runText}, 2 passed, 1 failed, 0 verified nothing in 3 projects"
            @>)
