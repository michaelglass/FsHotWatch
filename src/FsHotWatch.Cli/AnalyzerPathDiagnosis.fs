/// Why a configured `analyzers.paths` entry contributed no analyzers.
///
/// The fail-loud guard (`DaemonConfig.analyzerPathFailures`) decides THAT the daemon must
/// not start; this module says WHY, per path, from what is actually on disk — so the
/// message distinguishes "not built yet" from "built in the other configuration" from
/// "built, but nothing in it is an analyzer" instead of offering one guess for all three.
module FsHotWatch.Cli.AnalyzerPathDiagnosis

open System
open System.IO

/// What the filesystem says about one configured analyzer path that loaded 0 analyzers.
[<RequireQualifiedAccess>]
type AnalyzerPathProblem =
    /// The directory does not exist, and no Debug/Release twin of it does either — the
    /// analyzer outputs have not been built (they are gitignored build products, so a
    /// freshly created workspace starts here).
    | Missing
    /// The directory does not exist, but the same path with its `Debug`/`Release` segment
    /// swapped does: the analyzers were built in the other configuration.
    | BuiltInOtherConfiguration of existingPath: string
    /// The directory exists and holds no `*.dll` at all.
    | Empty
    /// The directory holds `*.dll` files, yet none of them loaded an analyzer: a build in
    /// the wrong configuration or a stale output, or assemblies that are not analyzers.
    | NoAnalyzersInDlls of dllCount: int

/// One configured path that contributed no analyzers, with its classification and the
/// repository's own bootstrap command for it (`analyzers.bootstrapHints`), if any.
type UnresolvedAnalyzerPath =
    { Path: string
      Problem: AnalyzerPathProblem
      BootstrapHint: string option }

/// The configuration-name segments whose twin is worth probing for.
let private configurationTwins = [ "Release", "Debug"; "Debug", "Release" ]

/// Every path equal to `path` with ONE `Debug`/`Release` segment swapped for the other.
let internal otherConfigurationCandidates (path: string) : string list =
    let separators = [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]
    // A rooted path splits with a leading "" segment, so the join restores the root.
    let segments = path.Split(separators)

    segments
    |> Array.toList
    |> List.indexed
    |> List.collect (fun (index, segment) ->
        configurationTwins
        |> List.filter (fun (from, _) -> String.Equals(segment, from, StringComparison.OrdinalIgnoreCase))
        |> List.map (fun (_, twin) ->
            let swapped = Array.copy segments
            swapped[index] <- twin
            String.Join(string Path.DirectorySeparatorChar, swapped)))

/// Classify a configured analyzer path that loaded 0 analyzers, from the filesystem.
let classify (path: string) : AnalyzerPathProblem =
    if Directory.Exists path then
        match Directory.GetFiles(path, "*.dll").Length with
        | 0 -> AnalyzerPathProblem.Empty
        | count -> AnalyzerPathProblem.NoAnalyzersInDlls count
    else
        match otherConfigurationCandidates path |> List.tryFind Directory.Exists with
        | Some twin -> AnalyzerPathProblem.BuiltInOtherConfiguration twin
        | None -> AnalyzerPathProblem.Missing

/// Classify every zero-loading path, attaching the configured bootstrap hint.
let diagnose (hintFor: string -> string option) (paths: string list) : UnresolvedAnalyzerPath list =
    paths
    |> List.map (fun path ->
        { Path = path
          Problem = classify path
          BootstrapHint = hintFor path })

/// The one-line explanation of a classification.
let describeProblem (problem: AnalyzerPathProblem) : string =
    match problem with
    | AnalyzerPathProblem.Missing -> "MISSING — the directory does not exist; the analyzer outputs have not been built"
    | AnalyzerPathProblem.BuiltInOtherConfiguration existing ->
        $"WRONG CONFIGURATION — the directory does not exist, but %s{existing} does; the analyzers were built in the other configuration"
    | AnalyzerPathProblem.Empty -> "EMPTY — the directory exists but contains no .dll files"
    | AnalyzerPathProblem.NoAnalyzersInDlls count ->
        $"NO ANALYZERS — the directory contains %d{count} .dll file(s), none of which loaded an analyzer; \
          a build in the wrong configuration, a stale build, or assemblies that are not analyzers"

/// The full, user-facing refusal. Plain text by construction: it names each unresolved
/// path, its classification and its bootstrap hint, and nothing else — no exception type,
/// no stack frame.
let render (unresolved: UnresolvedAnalyzerPath list) : string =
    let lines =
        unresolved
        |> List.collect (fun u ->
            [ yield $"  - %s{u.Path}"
              yield $"    %s{describeProblem u.Problem}"
              match u.BootstrapHint with
              | Some hint -> yield $"    to build it: %s{hint}"
              | None -> () ])

    [ yield
          $"Analyzer path(s) loaded 0 analyzers — %d{unresolved.Length} of the .fshw.json analyzers.paths entries contributed none:"
      yield! lines
      yield
          "Nothing was checked: a repository that declares analyzers is never checked without them. \
           Build the analyzer outputs (or correct .fshw.json analyzers.paths vs the build configuration), then re-run." ]
    |> String.concat "\n"
