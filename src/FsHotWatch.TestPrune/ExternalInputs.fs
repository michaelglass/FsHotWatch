/// Inputs outside the F# sources that key test results: dependsOn globs and the project structure hash.
module FsHotWatch.TestPrune.ExternalInputs

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Xml.Linq
open FSharp.Compiler.Diagnostics
open FsHotWatch.Events
open FsHotWatch
open FsHotWatch.FcsSuppression
open FsHotWatch.Logging
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginActivity
open FsHotWatch.PluginFramework
open FsHotWatch.StringHelpers
open TestPrune.AstAnalyzer
open TestPrune.Coverage
open TestPrune.Database
open TestPrune.Domain
open TestPrune.Extensions
open TestPrune.ImpactAnalysis
open TestPrune.SymbolDiff
open FsHotWatch.TestPrune.CoverageArtifacts
open FsHotWatch.TestPrune.CoverageIngest
open FsHotWatch.TestPrune.PluginTypes
open FsHotWatch.TestPrune.RuntimeCoverageSelection
open FsHotWatch.TestPrune.InputTrees
open FsHotWatch.TestPrune.EvidenceReceipts
open FsHotWatch.TestPrune.PluginState
open FsHotWatch.TestPrune.RunLaunch

/// Translate a repo-root-relative glob (`*`, `?`, `**`, `/`) into a regex
/// anchored against a repo-relative path. `**` matches across directory
/// separators (including none); a single `*`/`?` does NOT cross `/`. A trailing
/// `dir/**` also matches `dir` itself (zero segments). Paths are normalised to
/// `/` before matching so the same config works on every OS.
let internal dependsOnGlobToRegex (glob: string) : System.Text.RegularExpressions.Regex =
    // Collapse a trailing `/**` to a sentinel so the leading `/` becomes
    // optional (`dir/**` matches `dir`, `dir/x`, `dir/x/y`). Done before the
    // char scan so the `/` isn't emitted as a mandatory literal.
    let normalized = glob.Replace('\\', '/').TrimStart('/')

    let normalized, trailingDoubleStar =
        if normalized.EndsWith("/**") then
            normalized.Substring(0, normalized.Length - 3), true
        else
            normalized, false

    let sb = System.Text.StringBuilder()
    sb.Append('^') |> ignore
    let mutable i = 0

    while i < normalized.Length do
        let c = normalized.[i]

        if c = '*' then
            if i + 1 < normalized.Length && normalized.[i + 1] = '*' then
                // `**` — match any chars including `/`. A `**/ ` (cross-dir,
                // zero-or-more leading segments) makes the following separator
                // optional so `a/**/b` matches `a/b` too.
                if i + 2 < normalized.Length && normalized.[i + 2] = '/' then
                    sb.Append("(?:.*/)?") |> ignore
                    i <- i + 3
                else
                    sb.Append(".*") |> ignore
                    i <- i + 2
            else
                // single `*` — any run of non-separator chars
                sb.Append("[^/]*") |> ignore
                i <- i + 1
        elif c = '?' then
            sb.Append("[^/]") |> ignore
            i <- i + 1
        else
            sb.Append(System.Text.RegularExpressions.Regex.Escape(string c)) |> ignore
            i <- i + 1

    // Re-attach the trailing `/**`: an OPTIONAL `/<anything>` so the bare dir
    // and any descendant both match.
    if trailingDoubleStar then
        sb.Append("(?:/.*)?") |> ignore

    sb.Append('$') |> ignore

    System.Text.RegularExpressions.Regex(
        sb.ToString(),
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        ||| System.Text.RegularExpressions.RegexOptions.CultureInvariant
    )

/// What the `dependsOn` globs resolve to on disk.
type internal DependsOnResolution =
    {
        /// Sorted, distinct absolute paths of the matching files.
        Files: string list
        /// Repo-relative directories (or literal paths) the resolution reaches but could not
        /// read: their contents are unknown, so each salts the key as a hole. Sorted.
        Unreadable: string list
    }

/// Resolve the on-disk files under `repoRoot` whose repo-relative path matches
/// any of the `dependsOn` globs, and the directories that could hold a match but
/// could not be read. Deterministic: sorted, distinct. Globs that match nothing
/// contribute nothing; a glob that is a plain existing file path resolves to that
/// one file. A directory the walk could not see is never read as empty
/// (AUTOMATION-865): it is reported, and `externalDependencyHash` keys it as a hole.
let internal resolveDependsOnFiles (repoRoot: string) (dependsOn: string list) : DependsOnResolution =
    let rootFull = Path.GetFullPath(repoRoot)

    let toRel (abs: string) =
        Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

    let normalize (g: string) = g.Replace('\\', '/').TrimStart('/')

    // A plain glob with no wildcard meta is a direct file reference — resolve
    // it without walking the whole tree (cheap + handles files outside any
    // enumerable subdir uniformly).
    let isLiteral (g: string) =
        not (g.Contains('*') || g.Contains('?'))

    let literals, globs = dependsOn |> List.partition isLiteral

    let literalPaths =
        literals
        |> List.map (fun g -> Path.GetFullPath(Path.Combine(rootFull, normalize g)))

    let literalHits = literalPaths |> List.filter File.Exists

    // One that is not a file may be one that could not be looked up.
    let literalHoles =
        literalPaths
        |> List.filter (File.Exists >> not)
        |> List.choose SafeWalk.unreachable

    let globPatterns = globs |> List.map dependsOnGlobToRegex

    // Repo-root-rooted walk of EVERY file. `SearchOption.AllDirectories` here would
    // follow `.devenv/profile` into the /nix/store symlink cycle, so SafeWalk owns the
    // recursion (no symlinked-dir descent, depth-capped).
    let entries =
        if globPatterns.IsEmpty then
            []
        else
            SafeWalk.enumerateEntries SafeWalk.ToolingExcludedDirs "*" rootFull
            |> List.ofSeq

    let globHits =
        entries
        |> List.choose (function
            | SafeWalk.Found f -> Some f.FullName
            | SafeWalk.Skipped _ -> None)
        |> List.filter (fun abs ->
            let rel = toRel abs
            globPatterns |> List.exists (fun rx -> rx.IsMatch(rel)))

    // A hole matters to a glob when it could hold a match: it lies under the glob's
    // literal leading directories, or contains them. Compared as the globs are matched,
    // ignoring case.
    let prefixes =
        globs
        |> List.map (fun g -> (normalize g).Split('/') |> Array.takeWhile isLiteral |> String.concat "/")

    let under (path: string) (dir: string) =
        dir = ""
        || String.Equals(path, dir, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)

    let globHoles =
        entries
        |> List.choose (function
            | SafeWalk.Skipped s -> Some(toRel s.Path)
            | SafeWalk.Found _ -> None)
        |> List.filter (fun hole -> prefixes |> List.exists (fun p -> under hole p || under p hole))

    { Files = literalHits @ globHits |> List.distinct |> List.sort
      Unreadable =
        (literalHoles |> List.map (fun h -> toRel h.Path)) @ globHoles
        |> List.distinct
        |> List.sort }

/// Deterministic content hash of the files matched by the `dependsOn` globs.
/// Editing, adding, or deleting a matched file changes this hash, which salts
/// the test cache key so an external input (a DB migration, a generated file,
/// a schema) that test-prune's symbol diff can't see still invalidates a stale
/// cached test verdict. Empty `dependsOn` → empty string (NO salt: the key is
/// byte-identical to the pre-feature key, so existing caches keep hitting).
/// Missing files are skipped; a glob matching nothing contributes nothing. A
/// directory that could not be read is an entry of its own, under a key no file can
/// have (a trailing `/`), so the salt moves when one appears or goes.
let internal externalDependencyHash (repoRoot: string) (dependsOn: string list) : string =
    let resolved = resolveDependsOnFiles repoRoot dependsOn

    if resolved.Files.IsEmpty && resolved.Unreadable.IsEmpty then
        ""
    else
        let sb = System.Text.StringBuilder()

        let entry (rel: string) (h: string) =
            sb.Append(rel.Length).Append(':').Append(rel).Append('@').Append(h).Append('\n')
            |> ignore

        for path in resolved.Files do
            let rel = Path.GetRelativePath(Path.GetFullPath(repoRoot), path).Replace('\\', '/')

            let h =
                try
                    FsHotWatch.CheckCache.sha256Hex (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes path))
                with
                | :? IOException
                | :? UnauthorizedAccessException -> "unreadable"

            entry rel h

        for hole in resolved.Unreadable do
            entry (hole + "/") "unreadable-directory"


        FsHotWatch.CheckCache.sha256Hex (sb.ToString())

/// The files that DECLARE what gets compiled — `FsHotWatch.StructureFiles.allPatterns`,
/// not a second copy of it.
///
/// It WAS a second copy, and it was already wrong: `Directory.Build.targets` and
/// `Directory.Packages.props` are implicit MSBuild imports exactly as
/// `Directory.Build.props` is, and a `<Compile Include=…>` in either adds a file to
/// every project in the repo without moving one project file. Sharing the list with the
/// BUILD plugin's inputs merkle is the point: two caches that disagree about what
/// "structural" means is how one misses while the other replays.
let private structureFilePatterns = FsHotWatch.StructureFiles.allPatterns

/// Content merkle of the files that decide WHAT IS COMPILED.
///
/// The `BuildCompleted` cache key is a merkle over the CHANGED
/// SYMBOLS, and on a scan `BuildCompleted` is dispatched BEFORE the FCS pass — so that
/// term is empty whatever the tree holds. A tree that has just GAINED a test file and
/// its `<Compile Include=…>` therefore computes the SAME key as the tree without it,
/// hits the entry an earlier green wrote, and replays it: the handler is skipped, no
/// test process starts, `LastCoverage` still describes the earlier run, and the verdict
/// reports that run's full-suite green over a tree it never saw. Observed 2026-08-12 —
/// 21 new tests, none executed, `outcome: green`, `scope: {kind: full, 6/6}`.
///
/// Hashing the project files closes exactly that hole: a compile item cannot be added,
/// removed, or reordered without moving this hash, so a STRUCTURAL change is a
/// guaranteed cache MISS and the handler runs.
///
/// DELIBERATELY NOT the whole tree. A source EDIT is already covered by the symbol-diff
/// pipeline that runs after `BuildCompleted` and supersedes the entry; what that
/// pipeline cannot rescue is a file it has never seen. Hashing source content here would
/// invalidate every cached verdict on every keystroke for a guarantee already held.
///
/// TOTAL, and fails toward a re-run: `ContentHash.ofFile` answers with its unreadable
/// sentinel rather than throwing, and that sentinel differs from the file's readable
/// hash — so a project file we cannot read MOVES the key (a miss, a genuine run) rather
/// than being skipped as if it did not exist. Build output (`bin`/`obj`) is excluded via
/// `SourceExcludedDirs`, so a restore that regenerates project files under `obj/` cannot
/// invalidate every entry in the repo.
///
/// The same holds one level up, for a DIRECTORY the walk could not see: it is an entry
/// under its own path plus a trailing `/` (a relative path no file can have), hashed to
/// the same unreadable sentinel — `TreeHash.compute`'s rule for a hole. Built on the
/// best-effort walk, an unreadable directory contributed nothing and left this hash
/// unchanged, so the scan-skip guard replayed a project graph built from a tree it never
/// fully saw — the exact hazard this hash exists to close.
let internal projectStructureHash (repoRoot: string) : string =
    let rootFull = Path.GetFullPath repoRoot

    let relativeTo (abs: string) =
        Path.GetRelativePath(rootFull, abs).Replace('\\', '/')

    let walks =
        structureFilePatterns
        |> List.map (fun pattern -> SafeWalk.walk SafeWalk.SourceExcludedDirs pattern rootFull)

    let hashedFiles =
        walks
        |> List.collect (fun w -> w.Files |> List.map (fun f -> f.FullName))
        |> List.distinct
        |> List.map (fun abs -> relativeTo abs, ContentHash.ofFile abs)

    // Distinct: every pattern walks the same tree, so each one reports the same hole.
    let hashedHoles =
        walks
        |> List.collect (fun w -> w.Skipped |> List.map (fun s -> relativeTo s.Path + "/"))
        |> List.distinct
        |> List.map (fun rel -> rel, ContentHash.UnhashableContent)

    let entries =
        hashedFiles @ hashedHoles
        // Ordinal, so the merkle is reproducible across machines and locales.
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    let sb = System.Text.StringBuilder()

    for (rel, hash) in entries do
        // Length-prefixed, like every other merkle here: a separator that can occur
        // inside a field lets two different trees produce one byte stream.
        sb.Append(rel.Length) |> ignore
        sb.Append(':') |> ignore
        sb.Append(rel) |> ignore
        sb.Append('@') |> ignore
        sb.Append(hash) |> ignore
        sb.Append('\n') |> ignore

    FsHotWatch.CheckCache.sha256Hex (sb.ToString())
