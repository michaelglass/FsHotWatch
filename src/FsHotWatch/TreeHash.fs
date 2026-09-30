/// The content address of the tree fshw verifies.
///
/// A verdict is a claim about a PARTICULAR tree, so it carries the hash of the tree it
/// verified and the consumer's rule is total: if `treeHash ≠ hash(current tree)`, the
/// verdict does not apply — never reuse it.
///
/// WHAT IS HASHED is decided by ONE rule, and `VerdictInputs` states it: a file
/// belongs in the tree hash iff changing it can change what a check concludes.
/// Applied, that is three sources, unioned:
///
///   * everything under the discovery roots (`src/`, `tests/`) that is not build
///     output, tooling state, or excluded by config — including CONTENT/FIXTURE
///     files, deliberately: a changed fixture MSBuild declines to re-copy can
///     otherwise let a suite run green against the OLD fixture;
///   * `VerdictInputs.toolKnownInputs` — `.fshw.json`, and the root-level toolchain
///     and dependency files that decide what the compiler does at all;
///   * `VerdictInputs` DECLARED by the repo in `.fshw.json` — the coverage floors,
///     the analyzer rules, the baselines a finding is measured against. fshw cannot
///     derive these; the repo names them, each with a reviewable `why`.
///
/// A declared input that resolves to NO file is an ENTRY of its own, not a silent
/// zero — see `VerdictInputs.AbsentDeclaration`. A declaration nobody honours is
/// the fail-open this rule was introduced to close, and a declaration honoured only when
/// the file happens to be there is the same bug wearing a fix.
///
/// The hash is over CONTENT, never mtimes: a checkout, a `touch`, or a filesystem with
/// coarse timestamps all move the mtime without changing what the compiler sees, and
/// MSBuild's up-to-date check trusts exactly that mtime.
module FsHotWatch.TreeHash

open System
open System.IO
open System.Text

/// Identifies the hashing scheme, so a consumer can tell a hash it understands
/// from one it doesn't rather than comparing two incomparable strings. A change
/// to the recipe below — including a change to WHAT IS HASHED — MUST bump this.
///
/// v2: a directory the walk could not see is now an entry of its
/// own. Under v1 it contributed nothing, so a tree with a permission hole in it
/// hashed identically to the same tree fully readable, and a verdict earned over
/// the part we could see applied to the part we could not.
///
/// v3: the set is now derived from `VerdictInputs`' rule instead
/// of inherited from the watcher. Tool-known root files (`Directory.Build.props`,
/// `global.json`, …) and the repo's DECLARED inputs (coverage floors, analyzer
/// rules) are in it; a declaration that matches nothing is a sentinel entry. Under
/// v2 all of those contributed nothing, so weakening a check — lowering a floor,
/// editing a rule, flipping `TreatWarningsAsErrors` off — left the green earned
/// under the STRONGER check still reporting `Applies`.
[<Literal>]
let Algorithm = "fshw-tree-sha256-v3"

/// The content address of a tree, with the number of files that went into it and
/// the number of directories the walk could not see (a bare hash is unfalsifiable;
/// the counts make an empty/misrooted walk — and a TRUNCATED one — visible).
type Tree =
    {
        Hash: string
        FileCount: int
        SkippedCount: int
        /// How many of `FileCount` came from `verdictInputs.hashed` rather than from
        /// the discovery walk. Zero in a repo that declares nothing — which is a fact
        /// worth being able to READ, since it is also what a silently-ignored
        /// declaration would look like.
        DeclaredCount: int
        /// How many declarations resolved to no file at all and contributed a
        /// sentinel. Non-zero means a declared input is missing or misspelled: the
        /// hash is still sound (it moves when the file appears), but the repo is not
        /// gating on what it thinks it is.
        AbsentDeclarationCount: int
    }

/// What a walk of the verified tree found: its files as (repo-relative path,
/// absolute path) pairs, and the repo-relative path of every directory it could
/// NOT see. Both sorted by relative path (ordinal) — the sort is what makes the
/// hash reproducible across machines and filesystems.
type Walked =
    {
        Files: (string * string) list
        Skipped: string list
        /// Repo-relative paths that `.fshw.json` DECLARED as verdict inputs and that
        /// matched nothing on disk. Carried, never dropped: dropping them is what
        /// would let a typo'd declaration contribute zero while reading as protection.
        AbsentDeclarations: string list
        /// How many of `Files` came from declarations rather than from the walk.
        DeclaredCount: int
        /// The discovery roots the walk covered, repo-relative (`src`, `tests`).
        Roots: string list
        /// How many of `Files` are tool-known root inputs (`.fshw.json`, toolchain files).
        ToolKnownCount: int
    }

/// Everything that makes up "the tree fshw verifies", INCLUDING the holes in it.
///
/// A hole is a directory `SafeWalk` could not enumerate, or one past its depth cap.
/// It is carried here rather than dropped because dropping it is what made a
/// partially-seen tree hash like a fully-seen one; `compute` folds it into the hash.
/// Excluded paths are filtered out of the holes too: a directory the config says we
/// do not verify is not a hole in what we do.
///
/// The WALK half uses the SAME primitives the daemon uses to decide what it watches
/// (`Discovery.existingDiscoveryRoots`, `SafeWalk`, `PathFilter.isExcludedPath`), so
/// the hashed set cannot drift away from the checked set. The other two halves come
/// from `VerdictInputs`, which owns the rule for what else belongs.
///
/// `exclude` filters the WALK only. A declared input outranks it: `exclude` says
/// "do not go looking here", a declaration says "changing this changes the answer",
/// and the specific statement wins over the general one.
let files (repoRoot: string) (excludePatterns: string list) : Walked =
    let isExcluded = PathFilter.isExcludedPath repoRoot excludePatterns

    let relativeTo (abs: string) =
        Path.GetRelativePath(repoRoot, abs).Replace('\\', '/')

    let sorted paths =
        paths |> List.sortWith (fun a b -> String.CompareOrdinal(fst a, fst b))

    let walked =
        Discovery.existingDiscoveryRoots repoRoot
        |> List.map (fun root -> SafeWalk.walk SafeWalk.SourceExcludedDirs "*" root)

    let filePaths =
        walked
        |> List.collect (fun w -> w.Files |> List.map (fun f -> f.FullName))
        |> List.filter (isExcluded >> not)

    let declaration = VerdictInputs.read repoRoot
    let resolved = VerdictInputs.resolve repoRoot declaration

    // Deduplicated by ABSOLUTE path before relativising: a repo may well declare a
    // file the walk already found (a source file that is also a rule input), and
    // hashing it twice would make the entry list disagree with the file count.
    let toolKnown = VerdictInputs.toolKnownInputs repoRoot

    let walkAndToolKnown = filePaths @ toolKnown |> List.distinct

    let alreadyHashed = Set.ofList walkAndToolKnown
    let declaredOnly = resolved.Files |> List.filter (alreadyHashed.Contains >> not)

    { Files =
        walkAndToolKnown @ declaredOnly
        |> List.map (fun abs -> relativeTo abs, abs)
        |> sorted
      AbsentDeclarations = resolved.Absent
      DeclaredCount = List.length declaredOnly
      Roots = Discovery.existingDiscoveryRoots repoRoot |> List.map relativeTo
      ToolKnownCount =
        toolKnown
        |> List.filter (fun f -> not (List.contains f filePaths))
        |> List.length
      Skipped =
        walked
        |> List.collect (fun w -> w.Skipped |> List.map (fun s -> s.Path))
        |> List.filter (isExcluded >> not)
        |> List.map relativeTo
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)) }

/// The content address of `entries` — the pure half, so the recipe is testable
/// without a filesystem.
///
/// RECIPE (this is a documented contract; consumers may reimplement it):
///   for each (relPath, contentHash), ordered by relPath (ordinal):
///       relPath + NUL + contentHash + LF
///   treeHash = "sha256:" + lowerhex(sha256(utf8(that concatenation)))
///
/// The separator is NUL, not a space: a path may contain spaces, and a separator
/// that can occur inside a field lets two different trees produce one byte stream.
let hashEntries (entries: (string * string) list) : string =
    let sb = StringBuilder()

    for (rel, contentHash) in entries do
        sb.Append(rel).Append('\000').Append(contentHash).Append('\n') |> ignore

    "sha256:" + ContentHash.ofText (sb.ToString())

/// The content address of the repo's current tree.
///
/// A directory the walk could not see is an ENTRY, hashed to
/// `ContentHash.UnhashableContent` under its own path with a trailing `/` (a
/// relative path a FILE can never have, so a hole and a file never collide).
/// That is the same fail-closed answer `ContentHash` gives for an unreadable
/// FILE, applied one level up: a tree we could not fully see must not hash like
/// the tree we could, or a verdict earned over the visible part silently covers
/// the invisible part.
let ofWalked (walked: Walked) : Tree =
    // `ContentHash` owns the sentinel policy for an unreadable file.
    let hashedFiles =
        walked.Files |> List.map (fun (rel, abs) -> rel, ContentHash.ofFile abs)

    let hashedHoles =
        walked.Skipped |> List.map (fun rel -> rel + "/", ContentHash.UnhashableContent)

    // A declaration that matched nothing is an ENTRY, under a key a
    // real path cannot produce. The alternative — dropping it — is the same
    // fail-open as dropping an unreadable file one level up: the declaration reads
    // as protection while contributing zero, and the hash does not move when the
    // file it names finally appears.
    let hashedAbsent =
        walked.AbsentDeclarations
        |> List.map (fun rel -> VerdictInputs.SentinelPrefix + rel, VerdictInputs.AbsentDeclaration)

    let entries =
        hashedFiles @ hashedHoles @ hashedAbsent
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    { Hash = hashEntries entries
      FileCount = List.length hashedFiles
      SkippedCount = List.length hashedHoles
      DeclaredCount = walked.DeclaredCount
      AbsentDeclarationCount = List.length hashedAbsent }

/// The content address of the repo's current tree: `files`, then `ofWalked`.
let compute (repoRoot: string) (excludePatterns: string list) : Tree =
    ofWalked (files repoRoot excludePatterns)

/// What a tree hash covered, read off the walk that produced it, so the description
/// cannot claim more (or less) than the hash does. Reporting it changes nothing about
/// WHAT is hashed, so `Algorithm` stays where it is.
type Coverage =
    {
        /// Files in the key: the walk, the tool-known inputs and the declared inputs.
        FileCount: int
        /// The discovery roots walked, repo-relative.
        Roots: string list
        /// Tool-known root inputs outside the roots (`.fshw.json`, toolchain files).
        ToolKnownCount: int
        /// Declared inputs outside the roots and the tool-known files.
        DeclaredCount: int
        /// Declared paths that matched nothing (each hashed as a sentinel).
        Absent: string list
        /// Top-level directories outside the roots that hold only declared inputs,
        /// with how many.
        DeclaredOnly: (string * int) list
        /// Top-level directories no file in the key lives under.
        Outside: string list
    }

/// Read `Coverage` off a walk. Only the top-level directory listing is new I/O; every
/// count and path comes from `walked`, the list the hash is computed over.
let coverage (repoRoot: string) (walked: Walked) : Coverage =
    let topLevel (rel: string) =
        match rel.IndexOf '/' with
        | -1 -> None
        | i -> Some(rel.Substring(0, i))

    let perDirectory =
        walked.Files |> List.choose (fst >> topLevel) |> List.countBy id |> Map.ofList

    // A root that cannot be listed names no neighbours: this is a description of the
    // key, and the hash itself already carries any hole the walk met.
    let others =
        SafeWalk.subdirectories repoRoot
        |> Result.defaultValue [||]
        |> Array.map Path.GetFileName
        |> Array.filter (fun name ->
            not (name.StartsWith(".", StringComparison.Ordinal))
            && not (List.contains name walked.Roots)
            && not (SafeWalk.SourceExcludedDirs.Contains name))
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> Array.toList

    { FileCount = List.length walked.Files
      Roots = walked.Roots
      ToolKnownCount = walked.ToolKnownCount
      DeclaredCount = walked.DeclaredCount
      Absent = walked.AbsentDeclarations
      DeclaredOnly =
        others
        |> List.choose (fun d -> perDirectory |> Map.tryFind d |> Option.map (fun n -> d, n))
      Outside = others |> List.filter (fun d -> not (Map.containsKey d perDirectory)) }

/// The human lines for a `Coverage`: what the key holds, then what lies outside it.
let describeCoverage (c: Coverage) : string list =
    let roots =
        match c.Roots with
        | [] -> "no source roots"
        | roots -> roots |> List.map (fun r -> r + "/") |> String.concat " and "

    let declared =
        match c.DeclaredCount, c.Absent with
        | 0, [] -> ""
        | n, [] -> $", %d{n} declared input(s)"
        | n, absent ->
            let listed = String.concat ", " absent
            $", %d{n} declared input(s) (%d{List.length absent} declared path(s) match nothing: %s{listed})"

    let outside =
        (c.DeclaredOnly
         |> List.map (fun (d, n) -> $"%s{d}/ (%d{n} declared file(s) only)"))
        @ (c.Outside |> List.map (fun d -> d + "/"))

    [ $"key: %d{c.FileCount} file(s) — everything under %s{roots}, %d{c.ToolKnownCount} toolchain file(s)%s{declared}"
      if not (List.isEmpty outside) then
          let listed = String.concat ", " outside
          $"not in the key: %s{listed} — a change there does not make this verdict stale" ]
