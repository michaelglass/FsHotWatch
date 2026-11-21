/// AUTOMATION-1816 step 2b, design test 3: lint's answer to "what would reuse have handed you for
/// this servable file". Lint keeps one task-cache entry per file, so the entry reuse would have
/// read is gone once this run's lint writes its own. A capturing wrapper keeps the entry a fresh
/// write displaces, WITH ITS KEY, and lint compares that key with the reused key it builds from
/// the record: equal keys compare findings; a different key, or nothing displaced, is
/// unverifiable, never equal.
[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.ReferencesShadowLintTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharpLint.Application
open FSharpLint.Framework.Suggestion
open FSharp.Compiler.Text
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.Events
open FsHotWatch.PluginHost
open FsHotWatch.TaskCache
open FsHotWatch.ReferencesShadowReport
open FsHotWatch.Lint
open FsHotWatch.Tests.TestHelpers

// --- the capturing wrapper ---------------------------------------------------------------

let private entry (key: string) (message: string) : TaskCacheResult =
    { CacheKey = ContentHash.create key
      Errors = [ "/repo/src/A.fs", [ ErrorEntry.warningWithDetail message "" ] ]
      Status = CachedFileCompleted(System.TimeSpan.FromMilliseconds 1.0)
      EmittedEvents = [] }

let private lintA: CompositeKey =
    { Plugin = "lint"
      File = Some "src/A.fs" }

/// The eviction-order case: the file task cache deletes the entry a write supersedes, so the
/// wrapper must read it BEFORE it hands the write on. Read after, it would find only the new one.
[<Fact>]
let ``a fresh write's displaced entry is kept with its key, read before the write deletes it`` () =
    withTempDir "displaced-order" (fun dir ->
        let store = FileTaskCache.FileTaskCache(dir)

        let capturing =
            DisplacementCapturingTaskCache(store, store.EntriesOf, Set.ofList [ "lint" ])

        let cache = capturing :> ITaskCache
        let k0, k1 = ContentHash.create "k0", ContentHash.create "k1"

        cache.Set lintA k0 (entry "k0" "old")
        // The first write displaced nothing.
        test <@ List.isEmpty (capturing.Displaced lintA) @>

        cache.Set lintA k1 (entry "k1" "new")

        // The store collected the old entry, and the wrapper kept it, with the key it was filed under.
        test <@ (cache.TryGet lintA k0).IsNone @>
        test <@ capturing.Displaced lintA |> List.map _.Key = [ k0 ] @>

        test
            <@
                capturing.Displaced lintA
                |> List.collect (fun e -> TaskCache.findingsFor "/repo/src/A.fs" e.Result)
                |> List.map _.Message = [ "old" ]
            @>

        // Re-writing the same key displaces nothing more.
        cache.Set lintA k1 (entry "k1" "new again")
        test <@ capturing.Displaced lintA |> List.map _.Key = [ k0 ] @>)

/// Only the named plugins are kept: nothing else pays for the capture.
[<Fact>]
let ``a write by a plugin the wrapper does not capture keeps nothing`` () =
    withTempDir "displaced-other" (fun dir ->
        let store = FileTaskCache.FileTaskCache(dir)

        let capturing =
            DisplacementCapturingTaskCache(store, store.EntriesOf, Set.ofList [ "lint" ])

        let cache = capturing :> ITaskCache
        let analyzersA = { lintA with Plugin = "analyzers" }
        cache.Set analyzersA (ContentHash.create "k0") (entry "k0" "old")
        cache.Set analyzersA (ContentHash.create "k1") (entry "k1" "new")

        test <@ List.isEmpty (capturing.Displaced analyzersA) @>
        // Everything else reaches the store unchanged.
        test <@ (cache.TryGet analyzersA (ContentHash.create "k1")).IsSome @>)

/// Successive displacements of one file are all kept, newest first, up to the bound; a whole-run
/// entry (no file) is never kept; and every read and clear reaches the store unchanged.
[<Fact>]
let ``successive displacements are kept newest first, and reads and clears reach the store`` () =
    withTempDir "displaced-many" (fun dir ->
        let store = FileTaskCache.FileTaskCache(dir)

        let capturing =
            DisplacementCapturingTaskCache(store, store.EntriesOf, Set.ofList [ "lint" ])

        let cache = capturing :> ITaskCache
        let keys = [ 0 .. DisplacedKeptPerFile + 1 ] |> List.map (fun i -> $"k%d{i}")

        for key in keys do
            cache.Set lintA (ContentHash.create key) (entry key key)

        // Every write but the last displaced its predecessor; the oldest fell past the bound.
        test
            <@
                capturing.Displaced lintA |> List.map (fun e -> ContentHash.value e.Key) = (keys
                                                                                            |> List.rev
                                                                                            |> List.tail
                                                                                            |> List.truncate
                                                                                                DisplacedKeptPerFile)
            @>

        let wholeRun: CompositeKey = { Plugin = "lint"; File = None }
        cache.Set wholeRun (ContentHash.create "r0") (entry "r0" "r0")
        cache.Set wholeRun (ContentHash.create "r1") (entry "r1" "r1")
        test <@ List.isEmpty (capturing.Displaced wholeRun) @>

        let last = ContentHash.create (List.last keys)

        test
            <@
                match cache.Lookup lintA last with
                | CacheHit _ -> true
                | CacheMiss _ -> false
            @>

        cache.ClearPluginFile "lint" "src/A.fs"
        test <@ (cache.TryGet lintA last).IsNone @>
        cache.Set lintA last (entry "again" "again")
        cache.ClearFile "src/A.fs"
        test <@ (cache.TryGet lintA last).IsNone @>
        cache.Set lintA last (entry "again" "again")
        cache.ClearPlugin "lint"
        test <@ (cache.TryGet lintA last).IsNone @>
        cache.Set lintA last (entry "again" "again")
        cache.Clear()
        test <@ (cache.TryGet lintA last).IsNone @>)

/// The file task cache lists the entries it holds for one plugin and file, with their keys.
[<Fact>]
let ``the file task cache lists one plugin and file's entries with their keys`` () =
    withTempDir "entries-of" (fun dir ->
        let store = FileTaskCache.FileTaskCache(dir)
        let cache = store :> ITaskCache
        test <@ List.isEmpty (store.EntriesOf lintA) @>
        cache.Set lintA (ContentHash.create "k0") (entry "k0" "old")
        cache.Set { lintA with File = Some "src/B.fs" } (ContentHash.create "kb") (entry "kb" "b")

        test <@ store.EntriesOf lintA |> List.map _.Key = [ ContentHash.create "k0" ] @>
        // A store opened later reads what an earlier process left.
        let reopened = FileTaskCache.FileTaskCache(dir)
        test <@ reopened.EntriesOf lintA |> List.map _.Key = [ ContentHash.create "k0" ] @>)

// --- lint, through the real plugin path ---------------------------------------------------

let private files =
    [ "src/A.fs", "module A\nlet a = 1\n"; "src/B.fs", "module B\nlet b = BAD\n" ]

let private toolchain = "test-toolchain"

/// One warning for a source containing "BAD", worded `wording`.
let private warningsFor (wording: string) (result: FileCheckResult) : Lint.LintResult =
    let file = AbsFilePath.value result.File

    if result.Source.Contains "BAD" then
        Lint.LintResult.Success
            [ { Details =
                  { Range = Range.mkRange file (Position.mkPos 2 4) (Position.mkPos 2 7)
                    Message = wording
                    SuggestedFix = None
                    TypeChecks = [] }
                ErrorText = "FL0001"
                FilePath = file
                RuleName = "TestRule"
                RuleIdentifier = "FL0001" } ]
    else
        Lint.LintResult.Success []

/// A daemon start at `root` over the shared lint store `store`, every file checked as `check`
/// (a parse-only check and a full one carry different signatures, so lint keys them apart) and
/// linted by a runner wording its warning `wording`; then `ask` against the host. With `capture`
/// the store sits under the capturing wrapper and lint compares through it, as the daemon wires
/// it when `cache.nightlyShadow` is on.
let private startThen
    (store: string)
    (root: string)
    (capture: bool)
    (check: unit -> FileCheckState)
    (wording: string)
    (ask: PluginHost -> string list -> 'a)
    : 'a =
    let files' = FileTaskCache.FileTaskCache(store, repoRoot = root)

    let capturing =
        DisplacementCapturingTaskCache(files', files'.EntriesOf, Set.ofList [ "lint" ])

    let below =
        if capture then
            capturing :> ITaskCache
        else
            files' :> ITaskCache

    let cache = ToolchainScopedTaskCache(below, toolchain) :> ITaskCache

    let displacement =
        if capture then
            DisplacementCapture.Captured
                { Displaced = capturing.Displaced
                  Stored = scopeToToolchain toolchain }
        else
            DisplacementCapture.Off

    let host = PluginHost(Unchecked.defaultof<_>, root, taskCache = cache)
    host.WorkStore.PublishProjectModel fixtureModel
    let linted = System.Collections.Concurrent.ConcurrentQueue<string>()

    let runner (result: FileCheckResult) =
        linted.Enqueue(Path.GetRelativePath(root, AbsFilePath.value result.File))
        warningsFor wording result

    host.RegisterHandler(LintPlugin.createWith displacement (Some root) None (Some runner) None)

    try
        for rel, source in files do
            let before = committedBy host "lint"

            host.EmitFileChecked(
                { fakeFileCheckResult (Path.Combine(root, rel)) with
                    Source = source
                    CheckResults = check () }
            )

            test <@ waitForCommitted host "lint" before 1L 15000 @>

        waitForQuiescent host 15000
        ask host (List.ofSeq linted |> List.sort)
    finally
        host.Teardown()

/// The servable files as the nightly's scan names them: what their stored checks were typed
/// against, which here is the parse-only check of the first start.
let private servableAs (check: FileCheckState option) (root: string) : ServableFile list =
    files
    |> List.map (fun (rel, source) ->
        let stored =
            { fakeFileCheckResult (Path.Combine(root, rel)) with
                Source = source }

        let stored =
            match check with
            | Some check -> { stored with CheckResults = check }
            | None -> stored

        { File = rel
          Project = "Fixture.fsproj"
          FcsSignature = CheckCache.fcsCheckSignature stored
          UsedSignatures =
            match CacheInputs.usedSignaturesHash (Some root) stored.ProjectOptions stored.CheckResults with
            | Some hash -> UsedSignaturesKey.Described hash
            | None -> UsedSignaturesKey.Undescribed })

let private servable (root: string) : ServableFile list = servableAs None root

let private compare (host: PluginHost) (servable: ServableFile list) : PluginComparison =
    match
        host.RunCommand(LintCommand, [| Servable.toJson servable |])
        |> Async.RunSynchronously
    with
    | Some reply ->
        match PluginReply.tryOfJson reply with
        | Some comparison -> comparison
        | None -> failwith $"not a plugin reply: %s{reply}"
    | None -> failwith "the lint plugin has no references-shadow command"

let private outcomeOf (comparison: PluginComparison) (file: string) =
    comparison.Files |> List.find (fun (f, _) -> f = file) |> snd

let private allUnverifiable (reason: UnverifiableReason) (comparison: PluginComparison) =
    comparison.Files.Length = files.Length
    && comparison.Files
       |> List.forall (fun (_, outcome) -> outcome = FileOutcome.Unverifiable reason)

/// TEST 3. The first start lints the parse-only checks; the second, at the same bytes, checks
/// them fully, so its keys differ and its writes displace the first start's entries. Reuse would
/// have keyed by the parse-only signatures: the same key as the displaced entry, so its findings
/// are compared. Equal findings are equal; ones lint now words differently are a mismatch.
[<Fact(Timeout = 120000)>]
let ``equal keys compare findings: equal, or a mismatch naming both lists`` () =
    withTempDir "refshadow-lint" (fun dir ->
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        startThen store root true fixtureParseOnly "BAD is bad" (fun _ linted ->
            test <@ linted = [ "src/A.fs"; "src/B.fs" ] @>)

        let comparison =
            startThen store root true fixtureFullCheck "BAD is worse" (fun host linted ->
                // The second start linted live: nothing was replayed under its keys.
                test <@ linted = [ "src/A.fs"; "src/B.fs" ] @>
                compare host (servable root))

        test <@ comparison.Plugin = "lint" @>
        test <@ outcomeOf comparison "src/A.fs" = FileOutcome.Equal @>

        match outcomeOf comparison "src/B.fs" with
        | FileOutcome.Mismatch(reused, fresh) ->
            test <@ reused |> List.map _.Message = [ "BAD is bad" ] @>
            test <@ fresh |> List.map _.Message = [ "BAD is worse" ] @>
        | other -> failwith $"expected a mismatch, got %A{other}")

/// The nightly keyed the file by the very key reuse would have read (here the record's check is
/// this run's): its result may be the cached entry itself, so it is `sameKey`, never equal, and
/// never unverifiable either. That holds whether this run's write displaced something (a first
/// start under other keys) or replayed the cached entry (a second start under the same keys).
[<Fact(Timeout = 120000)>]
let ``a nightly key equal to the reused key is sameKey, never equal or unverifiable`` () =
    withTempDir "refshadow-lint-samekey" (fun dir ->
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        let fullServable () =
            servableAs (Some(fixtureFullCheck ())) root

        startThen store root true fixtureParseOnly "BAD is bad" (fun _ _ -> ())

        let displacedSomething =
            startThen store root true fixtureFullCheck "BAD is worse" (fun host linted ->
                test <@ linted = [ "src/A.fs"; "src/B.fs" ] @>
                compare host (fullServable ()))

        let replayed =
            startThen store root true fixtureFullCheck "BAD is worst" (fun host linted ->
                // Served from the cache: nothing linted, so the cache is all the run has.
                test <@ List.isEmpty linted @>
                compare host (fullServable ()))

        for comparison in [ displacedSomething; replayed ] do
            test <@ comparison.Files = [ "src/A.fs", FileOutcome.SameKey; "src/B.fs", FileOutcome.SameKey ] @>

            test
                <@
                    PluginComparison.counts comparison = { Equal = 0
                                                           Mismatch = 0
                                                           Unverifiable = 0
                                                           SameKey = 2 }
                @>)

/// The displaced entry was filed under another key than the one reuse would have read (here the
/// record names other signatures): it says nothing about reuse's answer, so it is unverifiable.
[<Fact(Timeout = 120000)>]
let ``a displaced entry under another key than the reused one is unverifiable, never equal`` () =
    withTempDir "refshadow-lint-otherkey" (fun dir ->
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        startThen store root true fixtureParseOnly "BAD is bad" (fun _ _ -> ())

        let comparison =
            startThen store root true fixtureFullCheck "BAD is bad" (fun host _ ->
                compare
                    host
                    (servable root
                     |> List.map (fun s ->
                         { s with
                             UsedSignatures = UsedSignaturesKey.Described "another-shape" })))

        test <@ allUnverifiable UnverifiableReason.DisplacedKeyDiffers comparison @>)

/// No earlier entry, so the second start's writes displaced nothing: unverifiable, never equal,
/// even though the fresh findings are what reuse would have computed.
[<Fact(Timeout = 120000)>]
let ``nothing displaced is unverifiable, never equal`` () =
    withTempDir "refshadow-lint-nothing" (fun dir ->
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        let comparison =
            startThen store root true fixtureFullCheck "BAD is bad" (fun host _ -> compare host (servable root))

        test <@ allUnverifiable UnverifiableReason.NoReusedEntry comparison @>)

/// Without the capture nothing names the entry reuse would have read, and a record naming no used
/// signatures names no key: both unverifiable.
[<Fact(Timeout = 120000)>]
let ``no capture, or no reused key, is unverifiable`` () =
    withTempDir "refshadow-lint-off" (fun dir ->
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        startThen store root false fixtureParseOnly "BAD is bad" (fun _ _ -> ())

        let off =
            startThen store root false fixtureFullCheck "BAD is bad" (fun host _ -> compare host (servable root))

        let undescribed =
            startThen store root true fixtureParseOnly "BAD is bad" (fun host _ ->
                compare
                    host
                    (servable root
                     |> List.map (fun s ->
                         { s with
                             UsedSignatures = UsedSignaturesKey.Undescribed })))

        test <@ allUnverifiable UnverifiableReason.NoReusedKey off @>
        test <@ allUnverifiable UnverifiableReason.NoReusedKey undescribed @>)

/// TEST 7, lint's half (gates the land): the capture changes nothing lint checks, reports or
/// files. The same two starts, captured and not, lint the same files, leave the same ledger and
/// the same task-cache entries.
[<Fact(Timeout = 120000)>]
let ``the capture changes nothing lint checks, reports or files`` () =
    let run (capture: bool) (dir: string) =
        let store = Path.Combine(dir, "tasks")
        let root = Path.Combine(dir, "repo")

        let observe (host: PluginHost) (linted: string list) =
            let entries =
                host.GetErrorsByPlugin "lint"
                |> Map.toList
                |> List.map (fun (file, entries) -> Path.GetRelativePath(root, file), entries)

            linted, entries

        let first = startThen store root capture fixtureParseOnly "BAD is bad" observe
        let second = startThen store root capture fixtureFullCheck "BAD is worse" observe
        // A third start at the second's keys replays them: what is served must match too.
        let third = startThen store root capture fixtureFullCheck "BAD is worst" observe

        let filed =
            let reopened = FileTaskCache.FileTaskCache(store, repoRoot = root)

            files
            |> List.collect (fun (rel, _) ->
                reopened.EntriesOf
                    { Plugin = "lint"
                      File = Some(CachePathIdentity.keyOf (Some root) (Path.Combine(root, rel))) }
                |> List.map (fun e ->
                    rel, ContentHash.value e.Key, TaskCache.findingsFor (Path.Combine(root, rel)) e.Result))

        first, second, third, filed

    let captured = withTempDir "refshadow-lint-gate-on" (run true)
    let plain = withTempDir "refshadow-lint-gate-off" (run false)
    let firstOn, secondOn, thirdOn, filedOn = captured
    let firstOff, secondOff, thirdOff, filedOff = plain

    test <@ firstOn = firstOff @>
    test <@ secondOn = secondOff @>
    test <@ thirdOn = thirdOff @>
    test <@ filedOn = filedOff @>
    // The starts did work: both lint every file, the third replays and lints none.
    test
        <@
            fst firstOn = [ "src/A.fs"; "src/B.fs" ]
            && fst secondOn = [ "src/A.fs"; "src/B.fs" ]
        @>

    test <@ List.isEmpty (fst thirdOn) @>
    test <@ filedOn.Length = files.Length @>
