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
    | Matching of pattern: WatchScope.FilePattern * entry: string
    /// Every file the scripts (repository-relative, as a command's arguments name them) `#load`,
    /// transitively, and the scripts themselves (`loadClosure`; AUTOMATION-1840). A command
    /// such as `fsi --typecheck-only build.fsx` reads each of them, although its pattern
    /// names none, so an edit to one must re-key it.
    | LoadClosure of scripts: string list

/// A file of a script's `#load` closure, repository-relative with `/` separators.
[<RequireQualifiedAccess>]
type Loaded =
    /// On disk: its content is an input.
    | Present of path: string
    /// Named by a `#load` (or as a script) but not on disk. The name is still an input: a file
    /// appearing there changes what the script compiles.
    | Missing of path: string

/// The scripts a command's arguments name: its `.fsx` tokens. A glob names no one script.
let scriptsIn (args: string) : string list =
    args.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter (fun token ->
        token.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase)
        && token.IndexOfAny([| '*'; '?' |]) < 0)
    |> List.ofArray

/// The string literals in `text` from `at` on, up to a line comment: regular (`\"` and `\\`
/// escape; any other backslash stays, as F# keeps it), verbatim (`@"..."`, `""` is a quote)
/// and triple-quoted. An unterminated literal ends the list.
let rec private literals (text: string) (at: int) (found: string list) : string list =
    let startsAt (i: int) (s: string) =
        String.CompareOrdinal(text, i, s, 0, s.Length) = 0

    let rec regular (i: int) (chars: char list) =
        if i >= text.Length then
            None
        elif text[i] = '"' then
            Some(String(Array.ofList (List.rev chars)), i + 1)
        elif
            text[i] = '\\'
            && i + 1 < text.Length
            && (text[i + 1] = '"' || text[i + 1] = '\\')
        then
            regular (i + 2) (text[i + 1] :: chars)
        else
            regular (i + 1) (text[i] :: chars)

    let rec verbatim (i: int) (chars: char list) =
        if i >= text.Length then
            None
        elif startsAt i "\"\"" then
            verbatim (i + 2) ('"' :: chars)
        elif text[i] = '"' then
            Some(String(Array.ofList (List.rev chars)), i + 1)
        else
            verbatim (i + 1) (text[i] :: chars)

    let continueWith (literal: (string * int) option) =
        match literal with
        | Some(value, next) -> literals text next (value :: found)
        | None -> List.rev found

    if at >= text.Length || startsAt at "//" then
        List.rev found
    elif startsAt at "\"\"\"" then
        match text.IndexOf("\"\"\"", at + 3, StringComparison.Ordinal) with
        | -1 -> List.rev found
        | close -> literals text (close + 3) (text.Substring(at + 3, close - at - 3) :: found)
    elif startsAt at "@\"" then
        continueWith (verbatim (at + 2) [])
    elif text[at] = '"' then
        continueWith (regular (at + 1) [])
    else
        literals text (at + 1) found

/// The files a `#load` directive in `text` (the content of `file`) names, absolute: each
/// relative to `file`'s own directory, with `__SOURCE_DIRECTORY__` read as that directory.
/// One directive may name several files. A directive inside a block comment or an inactive
/// `#if` branch is counted too: a superset costs a cache miss at worst, never a replay.
let internal loadTargets (file: string) (text: string) : string list =
    let dir = Path.GetDirectoryName(Path.GetFullPath file)

    text.Split('\n')
    |> Array.toList
    |> List.collect (fun line ->
        let directive = line.TrimStart()

        if
            directive.StartsWith("#load", StringComparison.Ordinal)
            && (directive.Length = 5 || not (Char.IsLetterOrDigit directive[5]))
        then
            literals directive 5 []
            |> List.map (fun path ->
                let path = path.Replace("__SOURCE_DIRECTORY__", dir)

                Path.GetFullPath(
                    if Path.IsPathRooted path then
                        path
                    else
                        Path.Combine(dir, path)
                ))
        else
            [])

/// The transitive `#load` closure of `scripts` (repository-relative, or absolute), the scripts
/// first: each file once, in the order a depth-first walk of the directives meets it, so a
/// cycle ends where it closes. Computed from the files as they are now, never listed by hand.
/// A file that exists but cannot be read is `Present`: its digest says it is unreadable.
let loadClosure (repoRoot: string) (scripts: string list) : Loaded list =
    let root = Path.GetFullPath repoRoot

    let relative (abs: string) =
        Path.GetRelativePath(root, abs).Replace('\\', '/')

    // A file that cannot be read loads nothing we can see; its own term says it is unreadable.
    let loadsOf (abs: string) =
        VerdictInputs.tryRead [] (fun () -> loadTargets abs (File.ReadAllText abs))

    let rec visit (pending: string list) (seen: Set<string>) (found: Loaded list) =
        match pending with
        | [] -> List.rev found
        | abs :: rest when seen.Contains abs -> visit rest seen found
        | abs :: rest when File.Exists abs ->
            visit (loadsOf abs @ rest) (seen.Add abs) (Loaded.Present(relative abs) :: found)
        | abs :: rest -> visit rest (seen.Add abs) (Loaded.Missing(relative abs) :: found)

    visit (scripts |> List.map (fun script -> Path.GetFullPath(Path.Combine(root, script)))) Set.empty []

/// The key terms of a load closure: each present file's content under `load:<path>`, and each
/// missing one as `missing:<path>`.
let loadTerms (repoRoot: string) (scripts: string list) : (string * string) list =
    loadClosure repoRoot scripts
    |> List.map (function
        | Loaded.Present rel -> "load:" + rel, ContentHash.ofFile (Path.Combine(Path.GetFullPath repoRoot, rel))
        | Loaded.Missing rel -> "missing:" + rel, "absent")

/// Whether a plugin's last outcome may answer for a tree it did not run on.
[<RequireQualifiedAccess; NoComparison>]
type LookupEligibility =
    /// Keyed by its own declared inputs: its newest outcome answers while they are unchanged.
    /// At least one set, by construction.
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
/// the kernel (`WatchScope.kernelExcludedDirs`) and build output (`Watcher.acceptsUnderRoot` drops
/// generated paths). Exactly the watcher's scope, so the key covers what runs the plugin.
let internal ScopeExcludedDirs =
    Set.union (set WatchScope.kernelExcludedDirs) SafeWalk.BuildOutputDirs

/// What a watcher's scope holds under one repository root, walked ONCE and shared by every
/// pattern declared over it (E5): each file outside `ScopeExcludedDirs`, as (repo-relative path,
/// absolute path) sorted ordinally; the repo-relative directories the walk could not see, each of
/// which may hold a match and so is part of every key; and the toolchain every command runs under.
[<NoComparison; NoEquality>]
type Scope =
    private
        { Root: string
          Files: (string * string) list
          Holes: string list
          Toolchain: Lazy<(string * string) list> }

/// The scope of `repoRoot` as it is now: one walk of the root.
let scopeOf (repoRoot: string) : Scope =
    let root = Path.GetFullPath repoRoot

    let relative (abs: string) =
        Path.GetRelativePath(root, abs).Replace('\\', '/')

    // Every file, matched later by the daemon's own rule (`FilePattern.matches`, case-insensitive)
    // and never by the OS glob, which is case-sensitive on Linux: a narrower glob would leave out of
    // the key a file the watcher reacts to. Build output is pruned by name (`ScopeExcludedDirs`),
    // which is exactly the `/bin/`-or-`/obj/` segment rule `PathFilter.isGeneratedUnder` applies.
    let walked = SafeWalk.walk ScopeExcludedDirs "*" root

    { Root = root
      Files =
        walked.Files
        |> List.map (fun f -> relative f.FullName, f.FullName)
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
      Holes =
        walked.Skipped
        |> List.map (fun s -> relative s.Path)
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
      // The tool-known root files, `.fshw.json` aside: each plugin's entry stands for it.
      Toolchain =
        lazy
            (let config = FsHwPaths.configFile root

             VerdictInputs.toolKnownInputs repoRoot
             |> List.filter (fun abs -> abs <> config)
             |> List.map (fun abs -> "toolchain:" + relative abs, ContentHash.ofFile abs)) }

/// The files `pattern` declares in `scope`, as (repo-relative path, absolute path), sorted
/// ordinally, and the directories the walk could not see.
let matchesIn (scope: Scope) (pattern: WatchScope.FilePattern) : (string * string) list * string list =
    scope.Files
    |> List.filter (fun (_, abs) -> WatchScope.FilePattern.matches pattern abs),
    scope.Holes

/// `matchesIn` over `repoRoot`'s scope as it is now.
let matches (repoRoot: string) (pattern: WatchScope.FilePattern) : (string * string) list * string list =
    matchesIn (scopeOf repoRoot) pattern

/// The digest of one declared input set over `scope`: each match's content, each hole as
/// unreadable (fail closed), the plugin's configuration entry, and the toolchain.
let digestIn (scope: Scope) (spec: InputSpec) : string =
    match spec with
    | InputSpec.Matching(pattern, entry) ->
        let files, holes = matchesIn scope pattern

        (files |> List.map (fun (rel, abs) -> "match:" + rel, ContentHash.ofFile abs))
        @ (holes
           |> List.map (fun rel -> "hole:" + rel + "/", ContentHash.UnhashableContent))
        @ [ "entry", entry ]
        @ scope.Toolchain.Value
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> TreeHash.hashEntries
    | InputSpec.LoadClosure scripts ->
        loadTerms scope.Root scripts
        |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
        |> TreeHash.hashEntries

/// `digestIn` over `repoRoot`'s scope as it is now.
let digest (repoRoot: string) (spec: InputSpec) : string = digestIn (scopeOf repoRoot) spec

/// The key a plugin's outcome is filed and looked up under when it declares its inputs, over
/// `scope`: the plugin, the binary that grades it, and the digest of each declared input set.
let keyIn
    (scope: Scope)
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
                  "inputs." + i.ToString(Globalization.CultureInfo.InvariantCulture), digestIn scope spec) ]

/// `keyIn` over `repoRoot`'s scope as it is now.
let key
    (repoRoot: string)
    (producer: string)
    (name: string)
    (first: InputSpec, rest: InputSpec list)
    : ActionKey.ActionKey =
    keyIn (scopeOf repoRoot) producer name (first, rest)

/// How a required plugin's outcome is keyed over one tree.
[<RequireQualifiedAccess; NoComparison>]
type Keying =
    /// By the whole tree: only a run on this tree answers for it.
    | WholeTree
    /// By its own declared inputs as they are now (`key`): its last outcome answers while they
    /// are unchanged.
    | DeclaredInputs of key: ActionKey.ActionKey

/// A required plugin resolved over one tree: what its outcome is keyed by there.
[<NoComparison>]
type Keyed = { Name: string; Keying: Keying }

/// `plugin` resolved over a scope walked at most once, however many plugins share it (E5): a
/// plugin that declares its inputs is keyed by them (`keyIn`) and forces the walk; one keyed by
/// the whole tree never does. The caller's IO, as hashing the tree is.
let keyedIn (scope: Lazy<Scope>) (producer: string) (plugin: RequiredPlugin) : Keyed =
    { Name = plugin.Name
      Keying =
        match plugin.Eligibility with
        | LookupEligibility.DeclaredInputs(first, rest) ->
            Keying.DeclaredInputs(keyIn scope.Value producer plugin.Name (first, rest))
        | LookupEligibility.NoDeclaredInputs _ -> Keying.WholeTree }

/// `keyedIn` over `repoRoot`'s scope as it is now, for one plugin.
let keyed (repoRoot: string) (producer: string) (plugin: RequiredPlugin) : Keyed =
    keyedIn (lazy (scopeOf repoRoot)) producer plugin
