/// A post-GC type histogram of one daemon's managed heap, and the "shareable
/// fraction" estimate the repository-host redesign hinges on.
///
/// The histogram is built from the runtime's own heap-walk events (GCBulkNode sizes
/// summed per type), NOT from `dotnet-gcdump report`, whose per-type byte column is not
/// a per-type total. The walk's grand total is reconciled against the GC's
/// `GCHeapStats` for the same induced collection; a record whose walk and heap stats
/// disagree by more than fragmentation can explain is flagged, not trusted.
///
/// The shareable estimate is a CLASSIFICATION of types, so it is a range, not a point:
///
/// * `Metadata` — objects that exist because a referenced assembly was read: FCS's
///   AbstractIL model (ILTypeDef, ILMethodDef, …), its binary readers, and
///   System.Reflection.Metadata. One repository host could hold these once for every
///   worktree that references the same assembly bytes.
/// * `PerSession` — objects that exist because a worktree's own source was parsed and
///   checked: syntax trees, name-resolution captures, check results, and the daemon's
///   own state. A host holds one copy per session regardless.
/// * `TypedTree` — FCS typed-tree nodes (Entity, Val, TType, …). Imported assemblies and
///   checked source files BOTH produce these, and the type alone cannot say which, so
///   they are reported separately rather than guessed into either side.
/// * `Unattributed` — BCL containers, strings, arrays: owned by whichever of the above
///   points at them.
///
/// `shareableLow = Metadata / total` is what is certainly shareable by type;
/// `shareableHigh = (Metadata + TypedTree + Unattributed) / total` is the ceiling if
/// every ambiguous byte turned out to be import state. Only a retention (dominator)
/// analysis narrows that range; the record keeps the buckets so it can be recomputed.
module FsHotWatch.Bench.HeapHistogram

open System

/// Classification of one heap type for the shareable-fraction estimate.
[<RequireQualifiedAccess>]
type Share =
    | Metadata
    | PerSession
    | TypedTree
    | Unattributed

/// Stable JSON key for a share bucket.
let shareKey (share: Share) : string =
    match share with
    | Share.Metadata -> "metadata"
    | Share.PerSession -> "perSession"
    | Share.TypedTree -> "typedTree"
    | Share.Unattributed -> "unattributed"

/// One type's live bytes and object count.
type TypeStat =
    {
        TypeName: string
        /// Assembly file name the type came from ("" when the runtime did not say).
        Module: string
        Bytes: int64
        Count: int64
    }

let private fcsModule = "FSharp.Compiler.Service.dll"

/// Leaf name of a possibly nested / namespaced type name: `A.B+C+D` → `D`.
let private leaf (name: string) : string =
    let cut = name.LastIndexOfAny([| '+'; '.' |])
    if cut < 0 then name else name.Substring(cut + 1)

let private isUpperAt (i: int) (s: string) = s.Length > i && Char.IsUpper s.[i]

/// Classify an FCS type by its (bare or qualified) name. Nested F# types reach the heap
/// walk BARE (`TType_app`, `EntityRef`), so the rules are on the leaf name, with the
/// namespace used when a qualified name is available (generic arguments carry one).
///
/// Every `+`/`.` segment is tested, not only the leaf: an F# union case is a nested class
/// (`SynExpr+App`), and when the walk does report the qualified name the owning union is
/// the informative part. When it reports only the bare case name (`App`) nothing says
/// which union it came from, so it falls to `TypedTree` — the ambiguous bucket the
/// shareable RANGE already spans — rather than being guessed.
let private classifyFcs (name: string) : Share =
    let l = leaf name
    let segments = name.Split([| '+'; '.' |])
    let anySegment (p: string -> bool) = segments |> Array.exists p

    // Syntax-namespace types the TYPED tree also carries for every member, imported ones
    // included (`ValMemberInfo` holds `SynMemberFlags`; `ValReprInfo` holds `Ident`s;
    // entity lookup tables are keyed by `PrettyNaming.NameArityPair`). The heap graph of a
    // real daemon found 15 MB of them hanging off imported assemblies, so they are not
    // evidence of a session's own source: ambiguous, like the rest of the typed tree.
    let typedTreeSyntax =
        anySegment (fun seg ->
            seg = "SynMemberFlags"
            || seg = "SynMemberKind"
            || seg = "Ident"
            || seg.StartsWith("Ident`", StringComparison.Ordinal)
            || seg = "PrettyNaming")

    if typedTreeSyntax && not (name.Contains("AbstractIL", StringComparison.Ordinal)) then
        Share.TypedTree
    elif
        name.Contains("AbstractIL", StringComparison.Ordinal)
        || anySegment (fun seg -> seg.StartsWith("IL", StringComparison.Ordinal) && isUpperAt 2 seg)
        || l.StartsWith("ByteMemory", StringComparison.Ordinal)
        || l.StartsWith("ByteFile", StringComparison.Ordinal)
        || l.Contains("BinaryReader", StringComparison.Ordinal)
    then
        Share.Metadata
    elif
        name.Contains(".Syntax", StringComparison.Ordinal)
        || name.Contains("NameResolution", StringComparison.Ordinal)
        || name.Contains("CodeAnalysis", StringComparison.Ordinal)
        || anySegment (fun seg -> seg.StartsWith("Syn", StringComparison.Ordinal) && isUpperAt 3 seg)
        || anySegment (fun seg -> seg.StartsWith("Parsed", StringComparison.Ordinal))
        || l.StartsWith("CapturedNameResolution", StringComparison.Ordinal)
        || l.StartsWith("TcResolutions", StringComparison.Ordinal)
        || l.StartsWith("TcSymbolUse", StringComparison.Ordinal)
        || l.StartsWith("Item_", StringComparison.Ordinal)
        || l.StartsWith("FSharpCheck", StringComparison.Ordinal)
        || l.StartsWith("FSharpParse", StringComparison.Ordinal)
    then
        Share.PerSession
    else
        Share.TypedTree

/// The FCS type named inside a generic instantiation, if any:
/// ``FSharpList`1[FSharp.Compiler.TypedTree+TType]`` → `FSharp.Compiler.TypedTree+TType`.
let private fcsTypeArgument (name: string) : string option =
    let i = name.IndexOf("FSharp.Compiler.", StringComparison.Ordinal)

    if i < 0 then
        None
    else
        let rest = name.Substring(i)
        let stop = rest.IndexOfAny([| ','; ']'; '[' |])
        Some(if stop < 0 then rest else rest.Substring(0, stop))

/// Classify one type from its name and module.
let classify (typeName: string) (moduleName: string) : Share =
    if
        moduleName = fcsModule
        || typeName.StartsWith("FSharp.Compiler.", StringComparison.Ordinal)
    then
        classifyFcs typeName
    elif
        moduleName.StartsWith("System.Reflection.Metadata", StringComparison.Ordinal)
        || typeName.StartsWith("System.Reflection.Metadata", StringComparison.Ordinal)
    then
        Share.Metadata
    elif
        moduleName.StartsWith("FsHotWatch", StringComparison.Ordinal)
        || typeName.StartsWith("FsHotWatch", StringComparison.Ordinal)
        || moduleName.StartsWith("Microsoft.Build", StringComparison.Ordinal)
        || moduleName.StartsWith("NuGet.", StringComparison.Ordinal)
        || moduleName.StartsWith("Ionide.ProjInfo", StringComparison.Ordinal)
    then
        // The daemon's own state and the project model it cracked: both are per
        // worktree (absolute paths, per-root configuration).
        Share.PerSession
    else
        match fcsTypeArgument typeName with
        | Some inner -> classifyFcs inner
        | None -> Share.Unattributed

/// Bytes per share bucket, and the derived range.
type ShareEstimate =
    {
        TotalBytes: int64
        Buckets: Map<Share, int64>
        /// Metadata / total: certainly shareable by type.
        ShareableLow: float
        /// (Metadata + TypedTree + Unattributed) / total: the ceiling.
        ShareableHigh: float
    }

/// Summarise a histogram into the shareable-fraction estimate.
let estimate (stats: TypeStat list) : ShareEstimate =
    let total = stats |> List.sumBy _.Bytes

    let buckets =
        stats
        |> List.groupBy (fun s -> classify s.TypeName s.Module)
        |> List.map (fun (share, rows) -> share, rows |> List.sumBy _.Bytes)
        |> Map.ofList

    let get share =
        buckets |> Map.tryFind share |> Option.defaultValue 0L

    let frac (bytes: int64) =
        if total = 0L then 0.0 else float bytes / float total

    { TotalBytes = total
      Buckets = buckets
      ShareableLow = frac (get Share.Metadata)
      ShareableHigh = frac (get Share.Metadata + get Share.TypedTree + get Share.Unattributed) }

/// The `n` largest types by bytes.
let top (n: int) (stats: TypeStat list) : TypeStat list =
    stats |> List.sortByDescending _.Bytes |> List.truncate n

/// How far the heap walk's total may disagree with what the GC says is live before the
/// walk is not trusted: 15% of the post-GC heap.
[<Literal>]
let WalkToleranceFraction = 0.15

/// Whether a heap walk's total reconciles with the GC's own view of the same collection.
///
/// `GCHeapStats.TotalHeapSize` INCLUDES free space between objects, and on a real daemon
/// that free space was 59% of the heap after the induced collection (the collection a
/// heap walk triggers need not compact). So the walk is compared with the GC's
/// live-object estimate, `heap x (1 - fragmentation)`, when the GC reported its
/// fragmentation, and with the raw heap size only when it did not. `Error` explains any
/// disagreement; a walk LARGER than the whole heap is impossible and always an error.
let reconcile (walkBytes: int64) (heapSizeAfterGc: int64) (fragmentationPercent: float option) : Result<unit, string> =
    if heapSizeAfterGc <= 0L then
        Error "no GCHeapStats for the induced collection — the walk has nothing to reconcile against"
    elif walkBytes > heapSizeAfterGc then
        Error $"heap walk %d{walkBytes} B exceeds the GC's post-GC heap %d{heapSizeAfterGc} B"
    else
        let expected, what =
            match fragmentationPercent with
            | Some f ->
                float heapSizeAfterGc * (1.0 - f / 100.0), $"the GC's live estimate (heap less %.1f{f}%% fragmentation)"
            | None -> float heapSizeAfterGc, "the post-GC heap (no fragmentation reading)"

        let gap = abs (expected - float walkBytes) / float heapSizeAfterGc

        if gap > WalkToleranceFraction then
            Error
                $"heap walk %d{walkBytes} B is %.1f{gap * 100.0}%% of the heap away from %s{what} (tolerance %.0f{WalkToleranceFraction * 100.0}%%)"
        else
            Ok()
