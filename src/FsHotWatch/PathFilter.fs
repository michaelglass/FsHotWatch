module FsHotWatch.PathFilter

open System.IO
open System.Threading
open Ignore

let private normalize (path: string) = path.Replace('\\', '/')

/// True if the path is inside an obj/ or bin/ directory.
let isGeneratedPath (path: string) =
    let n = normalize path
    n.Contains("/obj/") || n.Contains("/bin/")

/// True if `path` resolves OUTSIDE `repoRoot` — a rooted or `..`-prefixed relative
/// path. The canonical case is a NuGet-cache compile item a package injects via
/// `contentFiles`/`_content` (e.g. `xunit.v3.core.mtp-v1`'s
/// `_content/DefaultRunnerReporters.fs`): compiled into the project, but never the
/// repo's own source. The analyze/lint hosts skip inspecting these — running
/// FSharpLint over xunit's `_content` crashed the analyzer host.
let isOutsideRepo (repoRoot: string) (path: string) : bool =
    let rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/')
    Path.IsPathRooted(rel) || rel.StartsWith("..", System.StringComparison.Ordinal)

/// `isOutsideRepo` lifted over an optional repo root: `Some root` → true when
/// `path` is outside `root`; `None` → false (no repo scope, so nothing is
/// "outside" — everything is included). The report-producing plugins (analyzers,
/// lint) call this to skip third-party out-of-repo compile items by default, and
/// pass `None` for the `.fshw.json` `includeOutsideRepo` override.
let isOutsideRepoScoped (repoRoot: string option) (path: string) : bool =
    repoRoot |> Option.exists (fun root -> isOutsideRepo root path)

/// True if the path matches any of the given exclude patterns or is inside obj/bin.
/// Exclude patterns use gitignore-style glob syntax interpreted relative to
/// `repoRoot`. Files outside `repoRoot` are not subject to user excludes
/// (only the implicit obj/bin filter still applies). This matches gitignore
/// semantics: patterns like `.workspaces/` only match paths that are
/// repo-relative `.workspaces/...`, not arbitrary absolute paths that happen
/// to contain that segment.
let isExcludedPath (repoRoot: string) (excludePatterns: string list) : (string -> bool) =
    match excludePatterns with
    | [] -> isGeneratedPath
    | patterns ->
        let ig = (Ignore(), patterns) ||> List.fold (fun ig pat -> ig.Add(pat))

        let toRepoRelative (path: string) : string option =
            if isOutsideRepo repoRoot path then
                None
            else
                Some(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))

        fun path ->
            isGeneratedPath path
            || (match toRepoRelative path with
                | Some rel -> ig.IsIgnored(rel)
                | None -> false)

/// Load an ignore file (gitignore syntax) and return a predicate that checks
/// absolute paths against it. Returns a function that always returns false
/// if the file does not exist or cannot be read.
let loadIgnoreFile (repoRoot: string) (ignoreFilePath: string) : (string -> bool) =
    try
        let lines = File.ReadAllLines(ignoreFilePath)

        let ig =
            (Ignore(), lines)
            ||> Array.fold (fun (ig: Ignore) (line: string) -> ig.Add(line))

        fun (absolutePath: string) ->
            let relativePath = Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/')
            ig.IsIgnored(relativePath)
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> fun _ -> false

/// Collect ignore rules from .gitignore and .fantomasignore in the given directory.
/// Returns a predicate that returns true if a file should be ignored by either.
let collectIgnoreRules (repoRoot: string) : (string -> bool) =
    let gitignore = loadIgnoreFile repoRoot (Path.Combine(repoRoot, ".gitignore"))

    let fantomasignore =
        loadIgnoreFile repoRoot (Path.Combine(repoRoot, ".fantomasignore"))

    fun (absolutePath: string) -> gitignore absolutePath || fantomasignore absolutePath

let private getFileTimestamp (path: string) =
    try
        File.GetLastWriteTimeUtc(path).Ticks
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> 0L

[<NoComparison; NoEquality>]
type private CacheEntry =
    { RepoRoot: string
      Filter: string -> bool
      GitignoreTimestamp: int64
      FantomasignoreTimestamp: int64 }

/// Cache for collectIgnoreRules, keyed by repo root.
/// Automatically reloads when .gitignore or .fantomasignore change on disk.
type IgnoreFilterCache() =
    // Double-checked locking so concurrent startup misses don't all run
    // collectIgnoreRules: on miss, acquire syncRoot and re-check Volatile.Read
    // before rebuilding.
    let mutable cached: CacheEntry option = None
    let syncRoot = obj ()
    // Test seam: invoked after the outer Volatile.Read miss but before the lock is
    // acquired, so a test can deterministically pin two threads into the
    // lock-contention path and hit the inner "isFresh" branch.
    let mutable testHookAfterOuterMiss: unit -> unit = ignore

    member internal _.SetTestHookAfterOuterMiss(hook: unit -> unit) = testHookAfterOuterMiss <- hook

    member _.Get(repoRoot: string) =
        let gitignorePath = Path.Combine(repoRoot, ".gitignore")
        let fantomasignorePath = Path.Combine(repoRoot, ".fantomasignore")
        let gitTs = getFileTimestamp gitignorePath
        let fantTs = getFileTimestamp fantomasignorePath

        let isFresh entry =
            entry.RepoRoot = repoRoot
            && entry.GitignoreTimestamp = gitTs
            && entry.FantomasignoreTimestamp = fantTs

        match Volatile.Read(&cached) with
        | Some entry when isFresh entry -> entry.Filter
        | _ ->
            testHookAfterOuterMiss ()

            lock syncRoot (fun () ->
                match Volatile.Read(&cached) with
                | Some entry when isFresh entry -> entry.Filter
                | _ ->
                    let filter = collectIgnoreRules repoRoot

                    Volatile.Write(
                        &cached,
                        Some
                            { RepoRoot = repoRoot
                              Filter = filter
                              GitignoreTimestamp = gitTs
                              FantomasignoreTimestamp = fantTs }
                    )

                    filter)
