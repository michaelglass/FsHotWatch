module FsHotWatch.PathFilter

open System.IO
open System.Threading
open Ignore

let private normalize (path: string) = path.Replace('\\', '/')

/// True if the path is inside an obj/ or bin/ directory, read from the WHOLE path. A
/// checkout under a directory named `bin` or `obj` reads as build output here; callers
/// that know the repository or the project use `isGeneratedUnder` / `isGeneratedFor`.
let isGeneratedPath (path: string) =
    let n = normalize path
    n.Contains("/obj/") || n.Contains("/bin/")

/// `isGeneratedPath` read relative to `scope` (the repository root, or a project's
/// directory). Only the `obj/` and `bin/` below `scope` count: the directories ABOVE it
/// are where the checkout happens to live, and a checkout under `~/bin/` or `/srv/obj/`
/// would otherwise read every one of its own files as build output. A relative `path`
/// is read as given; a top-level `bin/` in it counts.
let isGeneratedUnder (scope: string) (path: string) =
    let scoped =
        if Path.IsPathRooted path && not (System.String.IsNullOrEmpty scope) then
            Path.GetRelativePath(scope, path)
        else
            path

    let n = "/" + normalize scoped
    n.Contains("/obj/") || n.Contains("/bin/")

/// A compile item its project's build generated: under `obj/` or `bin/` relative to the
/// project's own directory (an artifacts layout's `../../artifacts/obj/…` still counts).
let isGeneratedFor (projectFile: string) (source: string) =
    isGeneratedUnder (Path.GetDirectoryName projectFile) source

/// True if `path` resolves OUTSIDE `repoRoot` — a rooted or `..`-prefixed relative
/// path. The canonical case is a NuGet-cache compile item a package injects via
/// `contentFiles`/`_content` (e.g. `xunit.v3.core.mtp-v1`'s
/// `_content/DefaultRunnerReporters.fs`): compiled into the project, but never the
/// repo's own source. The analyze/lint hosts skip inspecting these — running
/// FSharpLint over xunit's `_content` crashed the analyzer host.
let isOutsideRepo (repoRoot: string) (path: string) : bool =
    let rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/')
    Path.IsPathRooted(rel) || rel.StartsWith("..", System.StringComparison.Ordinal)

/// `path` with every symbolic link along it resolved, component by component from the
/// filesystem root, so a link anywhere (the file itself, or a directory above it) is
/// followed to where it really points. A component that does not exist is kept as written.
let realPath (path: string) : string =
    // `hops` bounds the links followed in total, so a link cycle cannot loop for ever. A
    // link's target is itself resolved from its root, because its own components may be
    // links too (`/tmp` is a link to `/private/tmp` on macOS).
    let rec resolveFrom (path: string) (hops: int) : string * int =
        let full = Path.GetFullPath path
        let root = Path.GetPathRoot full

        full.Substring(root.Length).Split(Path.DirectorySeparatorChar, System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.fold
            (fun (soFar: string, hops: int) part ->
                let next = Path.Combine(soFar, part)

                match (if hops >= 40 then null else (FileInfo next).LinkTarget) with
                | null -> next, hops
                | target -> resolveFrom (Path.Combine(Path.GetDirectoryName next, target)) (hops + 1))
            (root, hops)

    fst (resolveFrom path 0)

/// True if `path`, with symbolic links resolved, lies outside `repoRoot` (also resolved):
/// a file a tool writing through `path` would change outside the repository. A link inside
/// the repository that points out of it is outside; `isOutsideRepo` alone compares the
/// path as written and would let a writer follow such a link out.
let resolvesOutsideRepo (repoRoot: string) (path: string) : bool =
    isOutsideRepo (realPath repoRoot) (realPath path)

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
    | [] -> isGeneratedUnder repoRoot
    | patterns ->
        let ig = (Ignore(), patterns) ||> List.fold (fun ig pat -> ig.Add(pat))

        let toRepoRelative (path: string) : string option =
            if isOutsideRepo repoRoot path then
                None
            else
                Some(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))

        fun path ->
            isGeneratedUnder repoRoot path
            || (match toRepoRelative path with
                | Some rel -> ig.IsIgnored(rel)
                | None -> false)

/// `isExcludedPath` for a DIRECTORY, asked without a trailing `/`. A bare path reads as
/// a file, so a directory-only pattern (`vendor/`) would miss the very directory it
/// names; the patterns are asked in directory form instead, trailing `/` dropped, as
/// `GitIgnores` does. Asking `vendor/` would not do: `Ignore` reads it as `vendor/` plus
/// an empty name, so `vendor/*` would match `vendor` itself, where git matches only
/// what is inside it.
let isExcludedDirectory (repoRoot: string) (excludePatterns: string list) : (string -> bool) =
    excludePatterns
    |> List.map (fun p -> p.TrimEnd('/'))
    |> List.filter (fun p -> p <> "" && p <> "!")
    |> isExcludedPath repoRoot

/// Load an ignore file (gitignore syntax) and return a predicate that checks
/// absolute paths against it. Returns a function that always returns false
/// if the file does not exist or cannot be read. Paths outside `repoRoot` are
/// never ignored, as in `isExcludedPath`: relative to the root they are `../...`,
/// and a pattern like `tmp/` would otherwise match any absolute path with that
/// segment (every file under Linux's `/tmp`).
let loadIgnoreFile (repoRoot: string) (ignoreFilePath: string) : (string -> bool) =
    try
        let lines = File.ReadAllLines(ignoreFilePath)

        let ig =
            (Ignore(), lines)
            ||> Array.fold (fun (ig: Ignore) (line: string) -> ig.Add(line))

        fun (absolutePath: string) ->
            not (isOutsideRepo repoRoot absolutePath)
            && ig.IsIgnored(Path.GetRelativePath(repoRoot, absolutePath).Replace('\\', '/'))
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> fun _ -> false

/// One line of the `.gitignore` in repo-relative directory `dir` ("" for the root),
/// rewritten as a pattern relative to the repository root, so every `.gitignore` in the
/// tree can be read by one matcher. `None` for a blank line or a comment.
///
/// Git anchors a pattern holding a slash (other than a trailing one) to the directory of
/// its `.gitignore`, and lets any other pattern match at any depth below it:
/// `/build` in `sub/.gitignore` is `sub/build`, and `*.log` is `sub/**/*.log`.
let internal rebaseIgnoreLine (dir: string) (line: string) : string option =
    let line =
        if line.EndsWith("\\ ", System.StringComparison.Ordinal) then
            line
        else
            line.TrimEnd()

    if line = "" || line.StartsWith("#", System.StringComparison.Ordinal) then
        None
    elif dir = "" then
        Some line
    else
        let negated = line.StartsWith("!", System.StringComparison.Ordinal)
        let body = if negated then line.Substring 1 else line
        let anchored = body.TrimEnd('/').Contains "/"

        let rebased =
            if anchored then
                dir + "/" + body.TrimStart('/')
            else
                dir + "/**/" + body

        Some((if negated then "!" else "") + rebased)

/// The repository's `.gitignore` files under `repoRoot`, read the way git reads them:
/// each applies below its own directory, and a deeper or later rule wins over a
/// shallower or earlier one. Files are read lazily, once per directory asked about, so a
/// walk pays only for the directories it visits.
///
/// Only the files in the tree are read — not `.git/info/exclude` or a user's global
/// excludes — so the answer is a function of the checkout's bytes, the same for every
/// clone and for a repository jj manages without git. Not thread-safe: build one per walk.
///
/// A DIRECTORY is matched by a second matcher, over the same rules with any trailing `/`
/// dropped and the path asked without one. `Ignore` reads a query `d/` as `d/` plus an
/// empty name, so `packages/*` would match `packages/` itself, where git matches only
/// what is inside it.
type GitIgnores(repoRoot: string) =
    let root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar)

    let rulesByDir =
        System.Collections.Generic.Dictionary<string, string list * Ignore * Ignore>()

    let matcher (lines: string list) =
        (Ignore(), lines) ||> List.fold (fun (ig: Ignore) l -> ig.Add l)

    let relative (abs: string) =
        Path.GetRelativePath(root, abs).Replace('\\', '/')

    let ownLines (dir: string) =
        let file = Path.Combine(dir, ".gitignore")

        if File.Exists file then
            try
                let rel = if dir = root then "" else relative dir
                File.ReadAllLines file |> Array.choose (rebaseIgnoreLine rel) |> List.ofArray
            with
            | :? IOException
            | :? System.UnauthorizedAccessException -> []
        else
            []

    let rec rulesFor (dir: string) : string list * Ignore * Ignore =
        match rulesByDir.TryGetValue dir with
        | true, rules -> rules
        | _ ->
            let inherited =
                if dir = root then
                    [], Ignore(), Ignore()
                else
                    rulesFor (Path.GetDirectoryName dir)

            let rules =
                match ownLines dir with
                | [] -> inherited
                | own ->
                    let lines, _, _ = inherited
                    let lines = lines @ own

                    let directoryLines =
                        lines
                        |> List.map (fun l -> l.TrimEnd('/'))
                        |> List.filter (fun l -> l <> "" && l <> "!")

                    lines, matcher lines, matcher directoryLines

            rulesByDir[dir] <- rules
            rules

    /// Whether the rules in force at `absolutePath`'s directory ignore it. A path outside
    /// the repository, or the root itself, is never ignored.
    member _.IsIgnored(absolutePath: string, isDirectory: bool) : bool =
        let abs = Path.GetFullPath absolutePath

        if abs = root || isOutsideRepo root abs then
            false
        else
            let _, files, directories = rulesFor (Path.GetDirectoryName abs)
            (if isDirectory then directories else files).IsIgnored(relative abs)

    /// Whether the directory `absoluteDir`, or any directory between it and the root, is
    /// ignored. Git never looks inside an ignored directory, so nothing under one is
    /// tracked however a later rule reads.
    member this.IsUnderIgnored(absoluteDir: string) : bool =
        let rec check (dir: string) =
            not (isNull dir)
            && dir <> root
            && not (isOutsideRepo root dir)
            && (this.IsIgnored(dir, true) || check (Path.GetDirectoryName dir))

        check (Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar))

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
    let syncRoot = Lock()
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

            Locking.locked syncRoot (fun () ->
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
