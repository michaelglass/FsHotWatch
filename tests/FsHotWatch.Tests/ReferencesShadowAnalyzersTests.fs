/// AUTOMATION-1816 step 2b, design test 2: the analyzers plugin's answer to "what would reuse
/// have handed you for this servable file", through the real plugin path: a host, the handler
/// filing `analyze` records in a disk outcome store, and the plugin's own command.
module FsHotWatch.Tests.ReferencesShadowAnalyzersTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.Analyzers
open FsHotWatch.Analyzers.AnalyzersPlugin
open FsHotWatch.Tests.TestHelpers

/// A live check of `rel` under `root` with `source`, over the shared fixture's real check.
let private liveAt (root: string) (rel: string) (source: string) : FileCheckResult =
    { fakeFileCheckResult (Path.Combine(root, rel)) with
        Source = source
        ProjectOptions = fixtureOptions ()
        CheckResults = fixtureFullCheck () }

/// The file as the nightly's scan would name it servable: the signatures of its stored check,
/// which for an unchanged check are this one's.
let private servableOf (root: string) (rel: string) (source: string) : ServableFile =
    let result = liveAt root rel source

    { File = rel
      Project = "Fixture.fsproj"
      FcsSignature = CheckCache.fcsCheckSignature result
      UsedSignatures =
        match CacheInputs.usedSignaturesHash (Some root) result.ProjectOptions result.CheckResults with
        | Some hash -> UsedSignaturesKey.Described hash
        | None -> UsedSignaturesKey.Undescribed }

/// One Error finding for a source that contains "BAD", worded by `wording`.
let private findingsFor (wording: string) (source: string) : FSharp.Analyzers.SDK.Message list =
    if source.Contains "BAD" then
        [ { Type = "Fixture"
            Message = wording
            Code = "FIX-BAD"
            Severity = FSharp.Analyzers.SDK.Severity.Error
            Range =
              FSharp.Compiler.Text.Range.mkRange
                  "Fixture.fs"
                  (FSharp.Compiler.Text.Position.mkPos 1 0)
                  (FSharp.Compiler.Text.Position.mkPos 1 3)
            Fixes = [] } ]
    else
        []

let private files =
    [ "src/A.fs", "module A\nlet a = 1\n"; "src/B.fs", "module B\nlet b = BAD\n" ]

/// A daemon start at `root` with records in `storeRoot` and its own task cache (a fresh
/// `start` name is a checkout whose entries are gone, so only the records hold the analyses): each of `files`
/// analysed live by analyzers that word their finding `wording`, then `ask` run against the
/// host before it is torn down.
let private startThen
    (start: string)
    (storeRoot: string)
    (reuseKeys: AnalyzeRecords.ReuseKeys)
    (root: string)
    (wording: string)
    (ask: PluginHost -> string list -> 'a)
    : 'a =
    let cache =
        FsHotWatch.FileTaskCache.FileTaskCache(Path.Combine(storeRoot, "..", start, "tasks"), repoRoot = root)
        :> FsHotWatch.TaskCache.ITaskCache

    let host = PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)
    host.WorkStore.PublishProjectModel fixtureModel

    let sources =
        files |> List.map (fun (rel, src) -> Path.Combine(root, rel), src) |> Map.ofList

    let analyzed = System.Collections.Concurrent.ConcurrentQueue<string>()

    let runner: AnalyzerRunner =
        fun _clients context ->
            async {
                analyzed.Enqueue(Path.GetRelativePath(root, context.FileName))

                return
                    [ { AnalyzerName = "Fixture"
                        Output = Result.Ok(findingsFor wording sources[context.FileName]) } ]
            }

    let store =
        OutcomeStore.Store(
            { Root = storeRoot
              Ttl = OutcomeStore.defaultTtl
              MaxBytes = OutcomeStore.defaultMaxBytes
              Log = ignore }
        )

    // Shadow: the records are filed and never answer, so every file is analysed live here.
    let wiring: AnalyzeRecords.Wiring =
        { Store = store
          RecordMode = AnalyzeRecords.Shadow
          Toolchain = "test-toolchain"
          Log = ignore
          Trace = ignore
          ReuseKeys = reuseKeys }

    host.RegisterHandler(
        createRecordedWithSeams (Some wiring) (Some root) [] Map.empty None DiagnosticSeverity.Warning None runner
    )

    try
        for rel, source in files do
            let before = committedBy host "analyzers"
            host.EmitFileChecked(liveAt root rel source)
            test <@ waitForCommitted host "analyzers" before 1L 15000 @>

        waitForQuiescent host 15000
        ask host (List.ofSeq analyzed |> List.sort)
    finally
        host.Teardown()

/// The analyzers plugin's comparison over `servable`, through its command.
let private compare (host: PluginHost) (servable: ServableFile list) : PluginComparison =
    match
        host.RunCommand(AnalyzersCommand, [| Servable.toJson servable |])
        |> Async.RunSynchronously
    with
    | Some reply ->
        match PluginReply.tryOfJson reply with
        | Some comparison -> comparison
        | None -> failwith $"not a plugin reply: %s{reply}"
    | None -> failwith "the analyzers plugin has no references-shadow command"

let private outcomeOf (comparison: PluginComparison) (file: string) =
    comparison.Files |> List.find (fun (f, _) -> f = file) |> snd

/// TEST 2. The first start files each analysis; the second, at the same tree, finds what it
/// finds. A stored analysis equal to the second start's findings is equal; one the analyzers now
/// word differently is a mismatch naming the file, the plugin and both findings; a deleted
/// record is unverifiable.
[<Fact(Timeout = 120000)>]
let ``the analyzers answer equal, mismatch or unverifiable for what reuse would have served`` () =
    withTempDir "refshadow-analyzers" (fun dir ->
        let storeRoot = Path.Combine(dir, "outcomes")
        let root = Path.Combine(dir, "repo")
        let servable = files |> List.map (fun (rel, src) -> servableOf root rel src)

        startThen "first" storeRoot AnalyzeRecords.ReuseKeys.Kept root "BAD is bad" (fun _ _ -> ())

        let comparison, afterDelete =
            startThen "second" storeRoot AnalyzeRecords.ReuseKeys.Kept root "BAD is worse" (fun host _ ->
                let comparison = compare host servable
                Directory.Delete(storeRoot, true)
                comparison, compare host servable)

        test <@ comparison.Plugin = "analyzers" @>
        test <@ outcomeOf comparison "src/A.fs" = FileOutcome.Equal @>

        match outcomeOf comparison "src/B.fs" with
        | FileOutcome.Mismatch(reused, fresh) ->
            test <@ reused |> List.map _.Message = [ "BAD is bad" ] @>
            test <@ fresh |> List.map _.Message = [ "BAD is worse" ] @>
        | other -> failwith $"expected a mismatch, got %A{other}"

        test
            <@
                PluginComparison.mismatches comparison |> List.map (fun m -> m.Plugin, m.File) = [ "analyzers",
                                                                                                   "src/B.fs" ]
            @>

        test
            <@
                afterDelete.Files
                |> List.forall (fun (_, outcome) ->
                    outcome = FileOutcome.Unverifiable UnverifiableReason.NoReusedEntry)
            @>)

/// Reuse keys a result only by signatures its record names: a record whose check's shape was
/// not described would have served nothing, and a handler that does not keep the key parts
/// cannot rebuild any key.
[<Fact(Timeout = 120000)>]
let ``no reused key is unverifiable, never equal`` () =
    withTempDir "refshadow-analyzers-nokey" (fun dir ->
        let storeRoot = Path.Combine(dir, "outcomes")
        let root = Path.Combine(dir, "repo")
        let servable = files |> List.map (fun (rel, src) -> servableOf root rel src)

        let undescribed =
            startThen "undescribed" storeRoot AnalyzeRecords.ReuseKeys.Kept root "BAD is bad" (fun host _ ->
                compare
                    host
                    (servable
                     |> List.map (fun s ->
                         { s with
                             UsedSignatures = UsedSignaturesKey.Undescribed })))

        let notKept =
            startThen "not-kept" storeRoot AnalyzeRecords.ReuseKeys.NotKept root "BAD is bad" (fun host _ ->
                compare host servable)

        for comparison in [ undescribed; notKept ] do
            test
                <@
                    comparison.Files
                    |> List.forall (fun (_, outcome) ->
                        outcome = FileOutcome.Unverifiable UnverifiableReason.NoReusedKey)
                @>

            test <@ comparison.Files.Length = 2 @>)

/// TEST 7, the analyzers' half (gates the land): keeping the key parts for the nightly shadow
/// changes nothing the plugin analyses, reports, files or serves. The same two starts, with the
/// parts kept and not, analyse the same files, report the same findings and leave the same records.
[<Fact(Timeout = 120000)>]
let ``keeping the key parts changes nothing the analyzers analyse, report, file or serve`` () =
    let run (reuseKeys: AnalyzeRecords.ReuseKeys) (dir: string) =
        let storeRoot = Path.Combine(dir, "outcomes")
        let root = Path.Combine(dir, "repo")

        let observe (host: PluginHost) (analyzed: string list) =
            let entries =
                host.GetErrorsByPlugin "analyzers"
                |> Map.toList
                |> List.map (fun (file, entries) -> Path.GetRelativePath(root, file), entries)

            analyzed, entries

        let first = startThen "first" storeRoot reuseKeys root "BAD is bad" observe
        let second = startThen "second" storeRoot reuseKeys root "BAD is worse" observe

        let records =
            (OutcomeStore.readAll storeRoot).Records
            |> List.map (fun r -> r.Key.Value.Text, r.Subject, $"%A{r.Outcome}")
            |> List.sort

        first, second, records

    let kept = withTempDir "refshadow-gate-kept" (run AnalyzeRecords.ReuseKeys.Kept)

    let notKept =
        withTempDir "refshadow-gate-notkept" (run AnalyzeRecords.ReuseKeys.NotKept)

    let firstKept, secondKept, recordsKept = kept
    let firstNot, secondNot, recordsNot = notKept

    test <@ firstKept = firstNot @>
    test <@ secondKept = secondNot @>
    test <@ recordsKept = recordsNot @>
    // The starts did work: every file analysed in each, and records filed.
    test
        <@
            fst firstKept = [ "src/A.fs"; "src/B.fs" ]
            && fst secondKept = [ "src/A.fs"; "src/B.fs" ]
        @>

    test <@ not recordsKept.IsEmpty @>
