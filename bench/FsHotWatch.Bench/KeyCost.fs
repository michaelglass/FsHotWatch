/// The analyzers cache key's cross-file slots on a real repository: what the
/// `used-signatures` slot costs per file, and how many files' keys an upstream edit moves
/// with and without it.
///
/// Every file of a project and of the F# projects it references is checked through a
/// checker configured like the daemon's. The key is computed twice per file: the key
/// without `used-signatures` (source, config, file, `fcs-signature`), and the same key
/// with it. Then each `Edit` is applied IN MEMORY — the project snapshot serves the
/// edited text under a version named by its content, so nothing on disk is written —
/// everything is checked again, and the keys that moved are counted.
///
/// A number, not a gate: see `HashCost` for why a timing on a shared box decides nothing.
module FsHotWatch.Bench.KeyCost

// The project-snapshot API is marked experimental; it is how the daemon checks too, and
// how an in-memory edit gets a version of its own.
#nowarn "57"

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.Text
open Ionide.ProjInfo
open FsHotWatch
open FsHotWatch.Events

/// One textual replacement in one file: `Before` must occur in it exactly once.
type Edit =
    { Label: string
      File: string
      Before: string
      After: string }

/// Parse `<before>=><after>`.
let parseReplacement (text: string) : Result<string * string, string> =
    match text.Split("=>", 2) with
    | [| before; after |] when before <> "" -> Ok(before, after)
    | _ -> Error $"expected <before>=><after>, got %s{text}"

/// Point every referenced F# project at an output that does not exist, so the checker
/// types each project against its referenced projects' sources — which is where an
/// in-memory edit lives — rather than against a build on disk.
let private fromSources (projects: FSharpProjectOptions list) : FSharpProjectOptions list =
    let memo = Dictionary<string, FSharpProjectOptions>()

    let rec retarget (options: FSharpProjectOptions) =
        match memo.TryGetValue options.ProjectFileName with
        | true, done' -> done'
        | false, _ ->
            let renamed = Dictionary<string, string>()

            let references =
                options.ReferencedProjects
                |> Array.map (fun reference ->
                    match reference with
                    | FSharpReferencedProject.FSharpReference(output, referenced) ->
                        let unbuilt = Path.ChangeExtension(output, ".unbuilt.dll")
                        renamed[output] <- unbuilt
                        FSharpReferencedProject.FSharpReference(unbuilt, retarget referenced)
                    | other -> other)

            let otherOptions =
                options.OtherOptions
                |> Array.map (fun option ->
                    match option.StartsWith("-r:", StringComparison.Ordinal) with
                    | true ->
                        match renamed.TryGetValue(option.Substring 3) with
                        | true, unbuilt -> "-r:" + unbuilt
                        | false, _ -> option
                    | false -> option)

            let result =
                { options with
                    ReferencedProjects = references
                    OtherOptions = otherOptions }

            memo[options.ProjectFileName] <- result
            result

    projects |> List.map retarget

/// The keys of one file in one pass.
type private FileKeys =
    {
        Without: ContentHash
        /// None when the slot is unavailable: the plugin then has no key and always runs.
        With: ContentHash option
        /// Time spent on the key without `used-signatures`.
        BaseTicks: int64
        /// Time spent on the `used-signatures` slot alone.
        SlotTicks: int64
    }

type private Pass =
    { Keys: Map<string, FileKeys>
      CheckTime: TimeSpan }

let private percentile (p: float) (values: float list) =
    Summary.percentile p values |> Option.defaultValue 0.0

let private ms (ticks: int64) =
    float ticks * 1000.0 / float Stopwatch.Frequency

/// Measure `project` (an `.fsproj` under `repo`) and its referenced projects, and the
/// keys each of `edits` moves. Returns the report lines.
let measure (repo: string) (project: string) (edits: Edit list) : string list =
    let repo = Path.GetFullPath repo
    eprintfn "loading %s" project
    let toolsPath = Init.init (DirectoryInfo repo) None
    let loader = WorkspaceLoader.Create(toolsPath, [])

    let projects =
        loader.LoadProjects [ Path.GetFullPath project ]
        |> FCS.mapManyOptions
        |> Seq.toList
        |> fromSources

    eprintfn "loaded %d projects" projects.Length
    let overrides = Dictionary<string, string>()

    let textOf (path: string) =
        match overrides.TryGetValue path with
        | true, text -> text
        | false, _ -> File.ReadAllText path

    let checker = Daemon.Daemon.createChecker ()

    // Each file is versioned by its content, so an in-memory edit is a new version.
    let snapshots = Dictionary<string, FSharpProjectSnapshot>()

    let snapshotOf (options: FSharpProjectOptions) =
        match snapshots.TryGetValue options.ProjectFileName with
        | true, snapshot -> snapshot
        | false, _ ->
            let snapshot =
                FSharpProjectSnapshot.FromOptions(
                    options,
                    fun _ path ->
                        async {
                            let text = textOf path

                            return
                                FSharpFileSnapshot.Create(
                                    path,
                                    CheckCache.sha256Hex text,
                                    fun () -> Task.FromResult(SourceTextNew.ofString text)
                                )
                        }
                )
                |> Async.RunSynchronously

            snapshots[options.ProjectFileName] <- snapshot
            snapshot

    let files =
        [ for options in projects do
              for file in options.SourceFiles do
                  if not (PathFilter.isGeneratedPath file) then
                      options, file ]

    let configHash (file: string) =
        [ ".editorconfig", "editorconfig"; "fsharplint.json", "fsharplint" ]
        |> List.collect (fun (name, label) -> CacheInputs.configChainInputs repo name label [ file ])
        |> List.map (fun (label, content) -> $"%s{label} %s{CheckCache.sha256Hex content}")
        |> String.concat "\n"
        |> CheckCache.sha256Hex

    let pass (version: int) =
        eprintfn "pass %d over %d files" version files.Length
        snapshots.Clear()
        let checkClock = Stopwatch()

        let keys =
            files
            |> List.map (fun (options, file) ->
                let source = textOf file
                checkClock.Start()

                let _, answer =
                    checker.ParseAndCheckFileInProject(file, snapshotOf options)
                    |> Async.RunSynchronously

                checkClock.Stop()

                let state =
                    match answer with
                    | FSharpCheckFileAnswer.Succeeded results -> FullCheck results
                    | FSharpCheckFileAnswer.Aborted -> ParseOnly

                let started = Stopwatch.GetTimestamp()

                let slots =
                    [ "plugin-version", "bench"
                      "fail-on-severity", "warning"
                      "analyzer-config", configHash file
                      "file", CachePathIdentity.keyOf (Some repo) file
                      "source", source
                      "fcs-signature", CheckCache.fcsCheckSignature state ]

                let without = TaskCache.merkleCacheKey slots
                let slotStarted = Stopwatch.GetTimestamp()

                let used = CacheInputs.usedSignaturesHash (Some repo) options state
                let slotEnded = Stopwatch.GetTimestamp()

                let withSlot =
                    used
                    |> Option.map (fun used -> TaskCache.merkleCacheKey (slots @ [ "used-signatures", used ]))

                file,
                { Without = without
                  With = withSlot
                  BaseTicks = slotStarted - started
                  SlotTicks = slotEnded - slotStarted })
            |> Map.ofList

        { Keys = keys
          CheckTime = checkClock.Elapsed }

    let relative (path: string) = Path.GetRelativePath(repo, path)

    let costLines (label: string) (p: Pass) =
        let baseMs = p.Keys |> Seq.sumBy (fun kv -> ms kv.Value.BaseTicks)
        let slots = p.Keys |> Seq.map (fun kv -> ms kv.Value.SlotTicks) |> Seq.toList

        [ $"%s{label}: checks %.1f{p.CheckTime.TotalSeconds} s; key pass without used-signatures %.0f{baseMs} ms, with %.0f{baseMs + List.sum slots} ms"
          $"  files with no key (used-signatures unavailable): %d{p.Keys |> Seq.filter (fun kv -> kv.Value.With.IsNone) |> Seq.length}"
          $"  used-signatures per file: mean %.2f{List.average slots} ms, p50 %.2f{percentile 50.0 slots} ms, p95 %.2f{percentile 95.0 slots} ms, max %.2f{List.max slots} ms" ]

    let lines = List<string>()
    lines.Add $"%s{relative project}: %d{projects.Length} projects, %d{files.Length} files"

    // The first pass compiles everything and warms the JIT; the second is the baseline.
    pass 0 |> costLines "cold pass" |> lines.AddRange
    let baseline = pass 1
    baseline |> costLines "baseline pass" |> lines.AddRange

    edits
    |> List.iteri (fun index edit ->
        let path = Path.GetFullPath(Path.Combine(repo, edit.File))
        let original = File.ReadAllText path
        let at = original.IndexOf(edit.Before, StringComparison.Ordinal)

        if at < 0 || original.IndexOf(edit.Before, at + 1, StringComparison.Ordinal) >= 0 then
            failwith $"%s{edit.Label}: %s{edit.File} must contain the replaced text exactly once"

        overrides[path] <-
            original.Substring(0, at)
            + edit.After
            + original.Substring(at + edit.Before.Length)

        let edited = pass (index + 2)
        overrides.Remove path |> ignore

        let moved (select: FileKeys -> 'key) =
            baseline.Keys
            |> Seq.filter (fun kv -> kv.Key <> path && select edited.Keys[kv.Key] <> select kv.Value)
            |> Seq.map (fun kv -> kv.Key)
            |> Seq.toList

        let without = moved (fun k -> k.Without)
        let withSlot = moved (fun k -> k.With)
        let others = files.Length - 1
        let share (n: int) = 100.0 * float n / float others

        lines.AddRange(costLines $"%s{edit.Label} (%s{edit.File})" edited)

        lines.Add
            $"  other files whose key moved: without used-signatures %d{without.Length}/%d{others} (%.1f{share without.Length}%%), with %d{withSlot.Length}/%d{others} (%.1f{share withSlot.Length}%%)"

        for file in withSlot |> List.truncate 10 do
            lines.Add $"    %s{relative file}")

    List.ofSeq lines
