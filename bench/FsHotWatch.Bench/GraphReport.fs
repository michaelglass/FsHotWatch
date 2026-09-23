/// Offline analysis of a kept heap-walk trace: the evidence for choosing import roots, and
/// the partition those roots produce.
module FsHotWatch.Bench.GraphReport

open System

let private mb (b: int64) = float b / 1048576.0

/// Render the report for `g`.
let render (g: HeapGraph.Graph) (report: HeapGraph.BuildReport) (rootTypes: string list) (top: int) : string =
    let d = HeapGraph.dominators g
    let retained = HeapGraph.retainedSizes g d
    let n = HeapGraph.nodeCount g
    let live = g.Size |> Array.sum

    let byType =
        HeapGraph.retainedByType g d retained
        |> List.sortByDescending (fun (_, r, _) -> r)

    let isTypedTree =
        let tt =
            Array.init g.TypeNames.Length (fun t ->
                HeapHistogram.classify g.TypeNames.[t] g.TypeModules.[t] = HeapHistogram.Share.TypedTree)

        fun v -> tt.[g.TypeIndex.[v]]

    let lines = ResizeArray<string>()
    let add (s: string) = lines.Add s

    add
        $"graph: %d{report.Nodes} nodes, %d{report.Edges} edges, %d{g.Roots.Length} roots; unresolved edges %d{report.UnresolvedEdges}, unresolved roots %d{report.UnresolvedRoots}, edge-count mismatch %d{report.EdgeCountMismatch}"

    add $"live %.1f{mb live} MB; reachable from roots %.1f{mb retained.[n]} MB"
    add ""
    add $"top %d{top} types by bytes retained by their top-most instances (dominator tree):"
    add "  retained MB | instances | type [module]"

    for t, r, count in byType |> List.truncate top do
        add $"  %10.1f{mb r} | %9d{count} | %s{g.TypeNames.[t]} [%s{g.TypeModules.[t]}]"

    let set =
        if List.isEmpty rootTypes then
            Retention.importedAssemblies
        else
            { Retention.importedAssemblies with
                Name = "custom"
                Types = rootTypes }

    let show (label: string) (r: HeapGraph.ReachBytes) =
        add
            $"    %-12s{label} import-only %8.1f{mb r.ImportOnly} | overlap %8.1f{mb r.Overlap} | session-only %8.1f{mb r.SessionOnly} | unreached %6.1f{mb r.Unreached} | total %8.1f{mb (HeapGraph.reachTotal r)} MB"

    let r = Retention.analyze g set
    add ""
    let typesText = String.Join(",", set.Types)
    let barriersText = String.Join(",", set.Barriers)
    let ownersText = String.Join(",", set.Owners)

    add
        $"root set %s{set.Name}: [%s{typesText}] barriers [%s{barriersText}] owners [%s{ownersText}] — %d{r.Roots} root instance(s)"

    show "all live" r.All

    for KeyValue(share, bytes) in r.ByShare do
        show (HeapHistogram.shareKey share) bytes

    let nr = Retention.narrow live r

    add ""
    add "  per-session-typed bytes on the import side, by type (the leak control's detail):"

    for name, bytes in Retention.perSessionOnImportSide g set |> List.truncate 10 do
        add $"    %8.2f{mb bytes} MB  %s{name}"

    add ""

    add
        $"narrowed shareable range: %.1f{nr.Low * 100.0}%% .. %.1f{nr.High * 100.0}%% of live; typed tree %.1f{nr.TypedTreeLow * 100.0}%% .. %.1f{nr.TypedTreeHigh * 100.0}%%"

    add
        $"controls: per-session types on the import side %.1f{mb nr.PerSessionOnImportSide} MB; accounting error %.3f{nr.AccountingError * 100.0}%%"

    String.Join("\n", lines)

let private anyTypeNamed (g: HeapGraph.Graph) (names: string list) : int -> bool =
    let m =
        Array.init g.TypeNames.Length (fun t ->
            let name = g.TypeNames.[t]
            let cut = name.LastIndexOfAny([| '+'; '.' |])
            let leaf = if cut < 0 then name else name.Substring(cut + 1)
            names |> List.exists (fun r -> r = name || r = leaf))

    fun v -> m.[g.TypeIndex.[v]]

/// The shortest reference chain from an instance of `from` to an instance of `target`,
/// as type names — the evidence for which fields connect import state to session state.
let path (g: HeapGraph.Graph) (from: string) (target: string) (block: string list) : string =
    let isFrom = anyTypeNamed g (from.Split(',') |> Array.toList)
    let isTo = anyTypeNamed g (target.Split(',') |> Array.toList)

    let blocked =
        if List.isEmpty block then
            (fun _ -> false)
        else
            anyTypeNamed g block

    match HeapGraph.shortestPath g isFrom isTo blocked with
    | None -> $"no path from %s{from} to %s{target}"
    | Some nodes ->
        let lines =
            nodes
            |> List.mapi (fun i v -> $"  %2d{i} %s{g.TypeNames.[g.TypeIndex.[v]]} (%d{g.Size.[v]} B)")

        String.Join("\n", $"path %s{from} -> %s{target} (%d{List.length nodes} objects):" :: lines)
