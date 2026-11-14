/// What a required plugin's outcome is a function of, as its configuration declares it
/// (AUTOMATION-1577, ADR-074; ADR-059 §3's "each plugin's own declared inputs").
///
/// The lookup answers a plugin that sat idle on a tree with its LAST outcome only when the
/// plugin declares its own inputs and they are unchanged: the record is keyed by those inputs,
/// so a tree that changed only other files finds it, and a change to one of them finds nothing.
/// A plugin that declares no inputs is keyed by the whole tree, as before: only a run on this
/// tree answers for it, and when none did, the lookup NAMES it as such rather than reporting a
/// bare miss. Nothing here makes an absent record green: a key with no record is missing.
module FsHotWatch.PluginInputs

open System
open System.IO

/// One set of inputs a plugin declares.
[<RequireQualifiedAccess; NoComparison>]
type InputSpec =
    /// Every file under the repository root whose path `pattern` matches, outside tooling
    /// directories and build output: the files whose change runs the plugin (its watcher's
    /// scope, `Watcher.acceptsUnderRoot`). `entry` digests the plugin's own configuration entry,
    /// so editing its command re-keys it and nothing else.
    | Matching of pattern: Watcher.FilePattern * entry: string

/// Whether a plugin's last outcome may answer for a tree it did not run on.
[<RequireQualifiedAccess; NoComparison>]
type LookupEligibility =
    /// Keyed by its own declared inputs: its newest outcome answers while they are unchanged.
    /// At least one set, by construction (FsHotWatch.Core is not referenced here, so no `NonEmpty`).
    | DeclaredInputs of first: InputSpec * rest: InputSpec list
    /// Declares no inputs of its own, so it is keyed by the whole tree: only a run on this tree
    /// answers. `reason` says why, and is what a lookup that misses it reports.
    | NoDeclaredInputs of reason: string

/// A plugin a green requires, by the name it reports its status under.
[<NoComparison>]
type RequiredPlugin =
    { Name: string
      Eligibility: LookupEligibility }

module RequiredPlugin =
    /// A plugin that reads the whole tree.
    let wholeTree (name: string) (reason: string) : RequiredPlugin =
        { Name = name
          Eligibility = LookupEligibility.NoDeclaredInputs reason }

/// The digest of a file command's own configuration entry: the fields that decide what it runs.
let entryDigest (name: string) (pattern: string) (command: string) (args: string) (timeoutSec: int option) =
    TreeHash.hashEntries
        [ "name", name
          "pattern", pattern
          "command", command
          "args", args
          "timeoutSec",
          (match timeoutSec with
           | Some s -> s.ToString(Globalization.CultureInfo.InvariantCulture)
           | None -> "none") ]

/// The directories a pattern's scope never reaches, by name: those its watcher's stream drops in
/// the kernel (`Watcher.kernelExcludedDirs`) and build output (`Watcher.acceptsUnderRoot` drops
/// generated paths). Exactly the watcher's scope, so the key covers what runs the plugin.
let internal ScopeExcludedDirs =
    Set.union (set Watcher.kernelExcludedDirs) SafeWalk.BuildOutputDirs

/// The files `pattern` declares under `repoRoot`, as (repo-relative path, absolute path), sorted
/// ordinally, and the repo-relative directories the walk could not see: each may hold a match,
/// so each is part of every key a pattern declares.
let matches (repoRoot: string) (pattern: Watcher.FilePattern) : (string * string) list * string list =
    let root = Path.GetFullPath repoRoot

    let relative (abs: string) =
        Path.GetRelativePath(root, abs).Replace('\\', '/')

    // The OS narrows by the pattern's own glob; `FilePattern.matches` then applies the daemon's
    // exact rule, so the two can only ever agree on less, never more.
    let walked =
        SafeWalk.walk ScopeExcludedDirs (Watcher.FilePattern.toString pattern) root

    let files =
        walked.Files
        |> List.map _.FullName
        |> List.filter (fun abs ->
            Watcher.FilePattern.matches pattern abs
            && not (PathFilter.isGeneratedUnder root abs))
        |> List.map (fun abs -> relative abs, abs)
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

    let holes =
        walked.Skipped
        |> List.map (fun s -> relative s.Path)
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

    files, holes

/// The digest of one declared input set in `repoRoot` as it is now: each match's content, each
/// hole as unreadable (fail closed), the plugin's configuration entry, and the toolchain every
/// command runs under (the tool-known root files, `.fshw.json` aside: the entry stands for it).
let digest (repoRoot: string) (spec: InputSpec) : string =
    match spec with
    | InputSpec.Matching(pattern, entry) ->
        let files, holes = matches repoRoot pattern
        let config = FsHwPaths.configFile (Path.GetFullPath repoRoot)

        let toolchain =
            VerdictInputs.toolKnownInputs repoRoot
            |> List.filter (fun abs -> abs <> config)
            |> List.map (fun abs ->
                "toolchain:"
                + Path.GetRelativePath(Path.GetFullPath repoRoot, abs).Replace('\\', '/'),
                ContentHash.ofFile abs)

        [ for rel, abs in files do
              yield "match:" + rel, ContentHash.ofFile abs
          for rel in holes do
              yield "hole:" + rel + "/", ContentHash.UnhashableContent
          yield "entry", entry
          yield! toolchain ]
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> TreeHash.hashEntries

/// The key a plugin's outcome is filed and looked up under when it declares its inputs: the
/// plugin, the binary that grades it, and the digest of each declared input set.
let key
    (repoRoot: string)
    (producer: string)
    (name: string)
    (first: InputSpec, rest: InputSpec list)
    : ActionKey.ActionKey =
    ActionKey.create
        "plugin-inputs"
        [ "plugin", name
          "producer", producer
          yield!
              first :: rest
              |> List.mapi (fun i spec ->
                  "inputs." + i.ToString(Globalization.CultureInfo.InvariantCulture), digest repoRoot spec) ]
