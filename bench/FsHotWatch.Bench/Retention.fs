/// The shareable fraction from the heap graph: which live bytes exist because of
/// imported assemblies (one repository host could hold them once) and which because of
/// a worktree's own checked source (one copy per session regardless).
///
/// ## The import roots, and the evidence for them
///
/// Chosen from a heap walk of this repository's own daemon (40.5M objects, 85.2M
/// references, every edge resolved), not from reading FCS:
///
/// * **Dominance alone cannot answer it.** The 16 `TcImports` DOMINATE only 1.5 MB, while
///   the import-created `MaybeLazy.Lazy[ModuleOrNamespaceType]` module types (94,594
///   instances) retain 868 MB: imported entities are also referenced from check results,
///   so no single import object dominates them. Reachability with blocking separates them
///   (`HeapGraph.partition`).
/// * **`TcImports` is a hub, not import contents.** Rooted there, 97% of the heap was
///   "import-reachable", including 192 MB of syntax trees and check results. The shortest
///   path shows why: `TcImports → TcAssemblyResolutions → TcConfig → TcConfigBuilder →
///   IProjectReference closure → TransparentCompiler → CompilerCaches → parsed files`.
/// * **`ImportedAssembly` is the unit of imported contents** (its `CcuThunk`, IL scope and
///   optimisation data). Its only way back to session state is the same hub, entered
///   through the lazy optimisation-data closure (`optdata → TcImports`). With `TcImports`
///   and `TcConfig` as barriers there is NO path from any `ImportedAssembly` to
///   `TransparentCompiler`, `ParsedImplFileInput`, `CheckedImplFile`, `TcIntermediate`,
///   `FSharpCheckFileResults` or `CapturedNameResolution`.
/// * **Every import is a DLL read from disk here:** 1,003 `ImportedAssembly`, 1,003
///   `RawFSharpAssemblyDataBackedByFileOnDisk`, no in-memory language-service references.
///   So project-to-project references are imported from each worktree's own `bin/` DLLs:
///   identical bytes at the same revision, different paths. They count as shareable only
///   for a host that keys imports by CONTENT; a path-keyed host would hold one per
///   worktree. The walk carries no string contents, so they cannot be told apart here.
/// * **`TcImports` also OWNS import contents directly** (its CCU and DLL tables point at
///   the same `CcuThunk`s the `ImportedAssembly`s do). With only the roots blocked on the
///   session side, the flood from the GC roots entered every import through `TcImports`
///   and 993 MB read as "overlap". So the session flood does not pass through `TcImports`
///   either; what remains in overlap is import contents that session state genuinely
///   references.
/// * **`TcGlobals` is import state too.** With `ImportedAssembly` and `TcImports` both
///   blocked, the session flood still entered the imports through
///   `CompilerCaches → framework-imports memo → (TcGlobals, TcImports) → TcGlobals →
///   EntityRef → Entity`: the global type table is built from the framework references.
///   It is a root and an owner, and with the barriers in place it has no path to
///   `TransparentCompiler`.
/// * **What is left in overlap is real reference, not ownership.** The next shortest route
///   from the checker into import contents is `TcInfo → TcState → OpenDeclaration →
///   EntityRef → Entity`: a session's checked state naming an imported entity. A host
///   holds that entity once however many sessions point at it, so overlap counts as
///   shareable (and is reported separately).
/// * `FrameworkImportsCache` reaches no `TcImports` under the TransparentCompiler, so a
///   "framework imports only" root set is empty and is not offered.
///
/// A leak control rides along: types that can only come from a session's own source
/// (`HeapHistogram.Share.PerSession`: syntax trees, name resolution, check results) must
/// land on the session side. Bytes of them on the import side mean the roots reach
/// checked source, and the record says how many.
module FsHotWatch.Bench.Retention

/// A named set of import roots.
type RootSet =
    {
        Name: string
        /// FCS type names (full, or the leaf after `+`/`.`) whose instances are roots.
        Types: string list
        /// FCS types the import flood must not enter (hubs, not contents).
        Barriers: string list
        /// FCS types that OWN import contents directly: the flood from the GC roots must
        /// not pass through them, or it reaches imports without passing a root.
        Owners: string list
    }

/// The root set the evidence above supports.
let importedAssemblies =
    { Name = "importedAssemblies"
      Types = [ "ImportedAssembly"; "TcGlobals" ]
      Barriers = [ "TcImports"; "TcConfig" ]
      Owners = [ "TcImports"; "TcGlobals" ] }

let private leafOf (name: string) =
    let cut = name.LastIndexOfAny([| '+'; '.' |])
    if cut < 0 then name else name.Substring(cut + 1)

/// Whether each type index names one of `names` in FSharp.Compiler.Service.dll.
let typeMatches (g: HeapGraph.Graph) (names: string list) : bool[] =
    Array.init g.TypeNames.Length (fun t ->
        g.TypeModules.[t] = "FSharp.Compiler.Service.dll"
        && names
           |> List.exists (fun r -> r = g.TypeNames.[t] || r = leafOf g.TypeNames.[t]))

/// One root set's partition of the live heap.
type Result =
    {
        RootSet: string
        Roots: int
        All: HeapGraph.ReachBytes
        /// The same partition restricted to each type-histogram share bucket.
        ByShare: Map<HeapHistogram.Share, HeapGraph.ReachBytes>
    }

let private shares =
    [ HeapHistogram.Share.Metadata
      HeapHistogram.Share.PerSession
      HeapHistogram.Share.TypedTree
      HeapHistogram.Share.Unattributed ]

/// Partition `g` for one root set.
let analyze (g: HeapGraph.Graph) (set: RootSet) : Result =
    let isRootType = typeMatches g set.Types
    let isBarrierType = typeMatches g set.Barriers
    let isOwnerType = typeMatches g set.Owners
    let isRoot v = isRootType.[g.TypeIndex.[v]]

    let reach =
        HeapGraph.partition g isRoot (fun v -> isBarrierType.[g.TypeIndex.[v]]) (fun v -> isOwnerType.[g.TypeIndex.[v]])

    let shareOfType =
        Array.init g.TypeNames.Length (fun t -> HeapHistogram.classify g.TypeNames.[t] g.TypeModules.[t])

    { RootSet = set.Name
      Roots = seq { 0 .. HeapGraph.nodeCount g - 1 } |> Seq.filter isRoot |> Seq.length
      All = HeapGraph.reachBytes g reach (fun _ -> true)
      ByShare =
        shares
        |> List.map (fun s -> s, HeapGraph.reachBytes g reach (fun v -> shareOfType.[g.TypeIndex.[v]] = s))
        |> Map.ofList }

/// The certainly-per-session types found on the import side (import-only or overlap),
/// with their bytes, largest first: what the leak control is made of.
let perSessionOnImportSide (g: HeapGraph.Graph) (set: RootSet) : (string * int64) list =
    let isRootType = typeMatches g set.Types
    let isBarrierType = typeMatches g set.Barriers
    let isOwnerType = typeMatches g set.Owners

    let reach =
        HeapGraph.partition
            g
            (fun v -> isRootType.[g.TypeIndex.[v]])
            (fun v -> isBarrierType.[g.TypeIndex.[v]])
            (fun v -> isOwnerType.[g.TypeIndex.[v]])

    let bytes = Array.zeroCreate<int64> g.TypeNames.Length

    for v in 0 .. HeapGraph.nodeCount g - 1 do
        let t = g.TypeIndex.[v]

        if
            (reach.[v] = HeapGraph.Reach.ImportOnly || reach.[v] = HeapGraph.Reach.Overlap)
            && HeapHistogram.classify g.TypeNames.[t] g.TypeModules.[t] = HeapHistogram.Share.PerSession
        then
            bytes.[t] <- bytes.[t] + g.Size.[v]

    [ for t in 0 .. bytes.Length - 1 do
          if bytes.[t] > 0L then
              yield g.TypeNames.[t], bytes.[t] ]
    |> List.sortByDescending snd

/// The narrowed shareable range over the live total.
type Narrowed =
    {
        /// Import-only / live: what exists only because of imports.
        Low: float
        /// (Import-only + overlap) / live: adds structure the imports reach that session
        /// state also reaches.
        High: float
        /// Same two ends for the FCS typed-tree bytes alone, over the typed-tree total.
        TypedTreeLow: float
        TypedTreeHigh: float
        /// Bytes of certainly-per-session types on the import side (import-only +
        /// overlap): the leak control. Should be ~0.
        PerSessionOnImportSide: int64
        /// |partition total − live| / live: the partition must account for every byte.
        AccountingError: float
    }

let narrow (live: int64) (r: Result) : Narrowed =
    let frac (b: int64) (over: int64) =
        if over = 0L then 0.0 else float b / float over

    let tt =
        r.ByShare
        |> Map.tryFind HeapHistogram.Share.TypedTree
        |> Option.defaultValue
            { ImportOnly = 0L
              Overlap = 0L
              SessionOnly = 0L
              Unreached = 0L }

    let perSession =
        r.ByShare
        |> Map.tryFind HeapHistogram.Share.PerSession
        |> Option.map (fun p -> p.ImportOnly + p.Overlap)
        |> Option.defaultValue 0L

    { Low = frac r.All.ImportOnly live
      High = frac (r.All.ImportOnly + r.All.Overlap) live
      TypedTreeLow = frac tt.ImportOnly (HeapGraph.reachTotal tt)
      TypedTreeHigh = frac (tt.ImportOnly + tt.Overlap) (HeapGraph.reachTotal tt)
      PerSessionOnImportSide = perSession
      AccountingError = frac (abs (HeapGraph.reachTotal r.All - live)) live }

/// Everything a record keeps about one retention analysis.
type Reading =
    {
        Set: RootSet
        Graph: HeapGraph.BuildReport
        Result: Result
        Narrowed: Narrowed
        /// Why this reading must not be trusted; empty when it can be.
        Problems: string list
    }

/// Leak-control ceiling: certainly-per-session bytes on the import side, as a fraction
/// of live, above which the roots are judged to reach checked source.
[<Literal>]
let LeakToleranceFraction = 0.01

/// Accounting ceiling: the partition must cover the live total to within 1%.
[<Literal>]
let AccountingToleranceFraction = 0.01

/// Analyse one graph and judge the result.
let evaluate (g: HeapGraph.Graph) (report: HeapGraph.BuildReport) (live: int64) (set: RootSet) : Reading =
    let result = analyze g set
    let narrowed = narrow live result

    let rootTypes = String.concat "/" set.Types

    let problems =
        [ if report.EdgeCountMismatch <> 0L then
              $"node/edge pairing off by %d{report.EdgeCountMismatch}: the graph is not the heap"
          if report.Edges > 0 && float report.UnresolvedEdges / float report.Edges > 0.001 then
              $"%d{report.UnresolvedEdges} of %d{report.Edges} edges point at no walked object"
          if result.Roots = 0 then
              $"no %s{rootTypes} instance in the heap: nothing to partition from"
          if narrowed.AccountingError > AccountingToleranceFraction then
              $"partition covers the live heap only to within %.2f{narrowed.AccountingError * 100.0}%%"
          if
              live > 0L
              && float narrowed.PerSessionOnImportSide / float live > LeakToleranceFraction
          then
              $"%d{narrowed.PerSessionOnImportSide} B of per-session types on the import side: the roots reach checked source" ]

    { Set = set
      Graph = report
      Result = result
      Narrowed = narrowed
      Problems = problems }
