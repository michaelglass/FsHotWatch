/// The heap walk as a graph, and the two analyses that narrow the shareable range:
/// immediate dominators (who RETAINS what), and a partition of every live object by
/// whether it is reachable through a chosen set of import roots.
///
/// Why a partition and not only a type histogram: the FCS typed tree (Entity, Val, TType,
/// …) is built both for imported assemblies and for checked source, and the type alone
/// cannot say which. Where an object is reachable FROM can. Given the set R of objects
/// that hold imported assembly contents:
///
/// * `ImportOnly` — reachable from R, and not reachable from any GC root without passing
///   through R. Freeing R frees these: they exist because of the imports.
/// * `Overlap` — reachable from R AND from a GC root by a path that avoids R. Shared
///   structure both sides point at (counted shareable, reported separately).
/// * `SessionOnly` — reachable from a GC root only by paths that avoid R.
///
/// Every live object falls in exactly one class (the walk enumerates only reachable
/// objects), which is what lets the partition be checked against the histogram totals.
module FsHotWatch.Bench.HeapGraph

open System
open System.Collections.Generic

/// A heap graph in compressed-sparse-row form. Node indices are dense `0 .. n-1`.
type Graph =
    {
        /// Object size, bytes, per node.
        Size: int64[]
        /// Index into `TypeNames` / `TypeModules`, per node.
        TypeIndex: int[]
        TypeNames: string[]
        TypeModules: string[]
        /// Outgoing edges of node `i` are `EdgeTarget[EdgeStart[i] .. EdgeStart[i+1]-1]`.
        EdgeStart: int[]
        EdgeTarget: int[]
        /// Nodes a GC root points at directly (handles, stacks, statics, finalizer queue).
        Roots: int[]
    }

/// Number of nodes.
let nodeCount (g: Graph) = g.Size.Length

/// How faithfully the raw walk resolved into a graph.
type BuildReport =
    {
        Nodes: int
        Edges: int
        /// Edges whose target address is not a walked object (freed between batches, or a
        /// walk that lost events). Dropped from the graph.
        UnresolvedEdges: int
        /// Root references to addresses that are not walked objects.
        UnresolvedRoots: int
        /// Sum of the nodes' declared edge counts minus the edges received. Non-zero
        /// means the node/edge pairing is off and the graph must not be trusted.
        EdgeCountMismatch: int64
    }

/// Raw walk data in arrival order: each node declares how many of the following edges
/// (in the concatenated edge stream) are its own.
type RawWalk =
    {
        Addresses: int64[]
        Sizes: int64[]
        TypeIds: uint64[]
        EdgeCounts: int[]
        EdgeTargets: int64[]
        RootAddresses: int64[]
        /// ConditionalWeakTable (key, value) pairs: the value lives as long as the key.
        WeakTableEdges: (int64 * int64)[]
    }

/// Build the graph. `typeOf` resolves a runtime type id to (name, module).
let build (raw: RawWalk) (typeOf: uint64 -> string * string) : Graph * BuildReport =
    let n = raw.Addresses.Length
    let order = Array.init n id
    let sortedAddr = Array.copy raw.Addresses
    Array.Sort(sortedAddr, order)

    let indexOf (address: int64) =
        let i = Array.BinarySearch(sortedAddr, address)
        if i >= 0 then order.[i] else -1

    // Dense type indices.
    let typeIndexById = Dictionary<uint64, int>()
    let names = ResizeArray<string>()
    let modules = ResizeArray<string>()

    let typeIndex =
        raw.TypeIds
        |> Array.map (fun id ->
            match typeIndexById.TryGetValue id with
            | true, i -> i
            | _ ->
                let name, moduleName = typeOf id
                let i = names.Count
                names.Add name
                modules.Add moduleName
                typeIndexById.[id] <- i
                i)

    // Edge ownership: node k owns the next EdgeCounts[k] edges of the stream.
    let declared = raw.EdgeCounts |> Array.sumBy int64
    let received = int64 raw.EdgeTargets.Length

    let extra = Dictionary<int, ResizeArray<int>>()

    for key, value in raw.WeakTableEdges do
        let k = indexOf key
        let v = indexOf value

        if k >= 0 && v >= 0 then
            match extra.TryGetValue k with
            | true, l -> l.Add v
            | _ -> extra.[k] <- ResizeArray [ v ]

    let edgeStart = Array.zeroCreate (n + 1)
    let targets = ResizeArray<int>(raw.EdgeTargets.Length)
    let mutable cursor = 0
    let mutable unresolved = 0

    for k in 0 .. n - 1 do
        edgeStart.[k] <- targets.Count

        for _ in 1 .. raw.EdgeCounts.[k] do
            if cursor < raw.EdgeTargets.Length then
                let t = indexOf raw.EdgeTargets.[cursor]

                if t >= 0 then
                    targets.Add t
                else
                    unresolved <- unresolved + 1

                cursor <- cursor + 1

        match extra.TryGetValue k with
        | true, l -> targets.AddRange l
        | _ -> ()

    edgeStart.[n] <- targets.Count

    let rootIdx = raw.RootAddresses |> Array.map indexOf
    let roots = rootIdx |> Array.filter (fun i -> i >= 0) |> Array.distinct

    { Size = raw.Sizes
      TypeIndex = typeIndex
      TypeNames = names.ToArray()
      TypeModules = modules.ToArray()
      EdgeStart = edgeStart
      EdgeTarget = targets.ToArray()
      Roots = roots },
    { Nodes = n
      Edges = targets.Count
      UnresolvedEdges = unresolved
      UnresolvedRoots = rootIdx |> Array.filter (fun i -> i < 0) |> Array.length
      EdgeCountMismatch = declared - received }

/// Immediate dominators over the graph plus a virtual root (index `n`) that points at
/// every GC root. `Idom.[v]` is `v`'s immediate dominator (`n` for nodes only the
/// virtual root dominates), `-1` when `v` is unreachable. `ChildrenFirst` lists the
/// reachable real nodes so that every node comes before its dominator (reverse DFS
/// preorder: a dominator is always a DFS-tree ancestor).
///
/// Lengauer & Tarjan's algorithm with path compression ("A Fast Algorithm for Finding
/// Dominators in a Flowgraph", 1979), fully iterative. The simpler Cooper-Harvey-Kennedy
/// iteration did not finish in ten minutes on a 38M-object daemon heap: its intersect
/// walks are proportional to dominator-tree depth, and F# lists make that depth millions.
type Dominators = { Idom: int[]; ChildrenFirst: int[] }

let dominators (g: Graph) : Dominators =
    let n = nodeCount g
    let root = n
    // Everything below is indexed by DFS preorder number; 0 is the virtual root.
    let numOf = Array.create (n + 1) -1
    let vertex = ResizeArray<int>(n + 1)
    let parentNum = ResizeArray<int>(n + 1)

    let childAt (v: int) (i: int) =
        if v = root then
            if i < g.Roots.Length then g.Roots.[i] else -1
        else
            let e = g.EdgeStart.[v] + i
            if e < g.EdgeStart.[v + 1] then g.EdgeTarget.[e] else -1

    let visit (v: int) (parent: int) =
        numOf.[v] <- vertex.Count
        vertex.Add v
        parentNum.Add parent

    visit root -1
    let stack = Stack<struct (int * int)>()
    stack.Push(struct (root, 0))

    while stack.Count > 0 do
        let struct (v, i) = stack.Pop()
        let c = childAt v i

        if c >= 0 then
            stack.Push(struct (v, i + 1))

            if numOf.[c] < 0 then
                visit c numOf.[v]
                stack.Push(struct (c, 0))

    let count = vertex.Count

    // Predecessors in preorder-number space (CSR).
    let predStart = Array.zeroCreate (count + 1)

    let forEachSucc (w: int) (f: int -> unit) =
        let v = vertex.[w]

        if v = root then
            for r in g.Roots do
                f numOf.[r]
        else
            for e in g.EdgeStart.[v] .. g.EdgeStart.[v + 1] - 1 do
                f numOf.[g.EdgeTarget.[e]]

    for w in 0 .. count - 1 do
        forEachSucc w (fun s -> predStart.[s + 1] <- predStart.[s + 1] + 1)

    for i in 1..count do
        predStart.[i] <- predStart.[i] + predStart.[i - 1]

    let fill = Array.copy predStart
    let preds = Array.zeroCreate predStart.[count]

    for w in 0 .. count - 1 do
        forEachSucc w (fun s ->
            preds.[fill.[s]] <- w
            fill.[s] <- fill.[s] + 1)

    let semi = Array.init count id
    let label = Array.init count id
    let ancestor = Array.create count -1
    let idom = Array.create count -1
    let bucketHead = Array.create count -1
    let bucketNext = Array.create count -1
    let path = Stack<int>()

    // eval with iterative path compression.
    let eval (v: int) =
        if ancestor.[v] < 0 then
            v
        else
            let mutable x = v

            while ancestor.[ancestor.[x]] >= 0 do
                path.Push x
                x <- ancestor.[x]

            while path.Count > 0 do
                let y = path.Pop()
                let a = ancestor.[y]

                if semi.[label.[a]] < semi.[label.[y]] then
                    label.[y] <- label.[a]

                ancestor.[y] <- ancestor.[a]

            label.[v]

    for w in count - 1 .. -1 .. 1 do
        for p in predStart.[w] .. predStart.[w + 1] - 1 do
            let u = eval preds.[p]

            if semi.[u] < semi.[w] then
                semi.[w] <- semi.[u]

        let s = semi.[w]
        bucketNext.[w] <- bucketHead.[s]
        bucketHead.[s] <- w
        let par = parentNum.[w]
        ancestor.[w] <- par

        let mutable b = bucketHead.[par]

        while b >= 0 do
            let u = eval b
            idom.[b] <- if semi.[u] < semi.[b] then u else par
            b <- bucketNext.[b]

        bucketHead.[par] <- -1

    for w in 1 .. count - 1 do
        if idom.[w] <> semi.[w] then
            idom.[w] <- idom.[idom.[w]]

    let result = Array.create (n + 1) -1
    result.[root] <- root

    for w in 1 .. count - 1 do
        result.[vertex.[w]] <- vertex.[idom.[w]]

    { Idom = result
      ChildrenFirst = [| for w in count - 1 .. -1 .. 1 -> vertex.[w] |] }

/// Retained size per node: its own size plus everything it dominates. Index `n` is the
/// virtual root (the whole reachable heap).
let retainedSizes (g: Graph) (d: Dominators) : int64[] =
    let n = nodeCount g
    let retained = Array.zeroCreate<int64> (n + 1)

    for v in d.ChildrenFirst do
        retained.[v] <- retained.[v] + g.Size.[v]
        retained.[d.Idom.[v]] <- retained.[d.Idom.[v]] + retained.[v]

    retained

/// Per type: bytes retained by its TOP-MOST instances (an instance dominated by another
/// instance of the same type is not counted again) and its instance count. This is the
/// evidence for choosing import roots: which types' instances hold the heap.
let retainedByType (g: Graph) (d: Dominators) (retained: int64[]) : (int * int64 * int) list =
    let n = nodeCount g
    let types = g.TypeNames.Length

    // Dominator-tree children, CSR.
    let count = Array.zeroCreate (n + 2)

    for v in d.ChildrenFirst do
        count.[d.Idom.[v] + 1] <- count.[d.Idom.[v] + 1] + 1

    for i in 1 .. n + 1 do
        count.[i] <- count.[i] + count.[i - 1]

    let start = Array.copy count
    let fill = Array.copy count
    let kids = Array.zeroCreate count.[n + 1]

    for v in d.ChildrenFirst do
        let p = d.Idom.[v]
        kids.[fill.[p]] <- v
        fill.[p] <- fill.[p] + 1

    let active = Array.zeroCreate<int> types
    let top = Array.zeroCreate<int64> types
    let instances = Array.zeroCreate<int> types
    // (node, entering?) — exit events undo the active count.
    let stack = Stack<struct (int * bool)>()
    stack.Push(struct (n, true))

    while stack.Count > 0 do
        let struct (v, entering) = stack.Pop()

        if entering then
            if v < n then
                let t = g.TypeIndex.[v]
                instances.[t] <- instances.[t] + 1

                if active.[t] = 0 then
                    top.[t] <- top.[t] + retained.[v]

                active.[t] <- active.[t] + 1
                stack.Push(struct (v, false))

            for k in start.[v] .. start.[v + 1] - 1 do
                stack.Push(struct (kids.[k], true))
        else
            let t = g.TypeIndex.[v]
            active.[t] <- active.[t] - 1

    [ for t in 0 .. types - 1 -> t, top.[t], instances.[t] ]

/// Which side of the import roots a live object is on.
[<RequireQualifiedAccess>]
type Reach =
    | Unreached
    | ImportOnly
    | Overlap
    | SessionOnly

/// Partition every node by reachability through `isImportRoot` nodes (see module doc).
/// The flood FROM the import roots does not enter `isBarrier` nodes: hub objects the
/// import state points at that are not assembly contents (configuration, and through it
/// a closure back to the whole checker), which would otherwise make "reachable from an
/// import" mean "reachable from anything". The flood from the GC roots does not enter
/// the import roots themselves, nor `isOwner` nodes: objects that OWN import contents
/// directly (so passing through them would reach imports without going through a root).
let partition (g: Graph) (isImportRoot: int -> bool) (isBarrier: int -> bool) (isOwner: int -> bool) : Reach[] =
    let n = nodeCount g
    let fromImports = Array.zeroCreate<bool> n
    let fromSession = Array.zeroCreate<bool> n

    let flood (seeds: int seq) (mark: bool[]) (blocked: int -> bool) =
        let stack = Stack<int>()

        for s in seeds do
            if not mark.[s] then
                mark.[s] <- true
                stack.Push s

        while stack.Count > 0 do
            let v = stack.Pop()

            for e in g.EdgeStart.[v] .. g.EdgeStart.[v + 1] - 1 do
                let t = g.EdgeTarget.[e]

                if not mark.[t] && not (blocked t) then
                    mark.[t] <- true
                    stack.Push t

    flood (seq { 0 .. n - 1 } |> Seq.filter isImportRoot) fromImports isBarrier
    let sessionBlocked v = isImportRoot v || isOwner v
    flood (g.Roots |> Seq.filter (sessionBlocked >> not)) fromSession sessionBlocked

    Array.init n (fun v ->
        match fromImports.[v], fromSession.[v] with
        | true, false -> Reach.ImportOnly
        | true, true -> Reach.Overlap
        | false, true -> Reach.SessionOnly
        | false, false -> Reach.Unreached)

/// Bytes per reach class among the nodes `include` selects.
type ReachBytes =
    { ImportOnly: int64
      Overlap: int64
      SessionOnly: int64
      Unreached: int64 }

let reachBytes (g: Graph) (reach: Reach[]) (includeNode: int -> bool) : ReachBytes =
    let mutable io = 0L
    let mutable ov = 0L
    let mutable so = 0L
    let mutable un = 0L

    for v in 0 .. nodeCount g - 1 do
        if includeNode v then
            match reach.[v] with
            | Reach.ImportOnly -> io <- io + g.Size.[v]
            | Reach.Overlap -> ov <- ov + g.Size.[v]
            | Reach.SessionOnly -> so <- so + g.Size.[v]
            | Reach.Unreached -> un <- un + g.Size.[v]

    { ImportOnly = io
      Overlap = ov
      SessionOnly = so
      Unreached = un }

/// Total of a `ReachBytes`.
let reachTotal (r: ReachBytes) =
    r.ImportOnly + r.Overlap + r.SessionOnly + r.Unreached

/// Shortest reference path from any node satisfying `isFrom` to any node satisfying
/// `isTo` (breadth-first), as the node list from start to end. `blocked` nodes are not
/// entered. The evidence tool for asking "how does X reach Y?".
let shortestPath (g: Graph) (isFrom: int -> bool) (isTo: int -> bool) (blocked: int -> bool) : int list option =
    let n = nodeCount g
    let parent = Array.create n -2 // -2 unseen, -1 start
    let queue = Queue<int>()

    for v in 0 .. n - 1 do
        if isFrom v then
            parent.[v] <- -1
            queue.Enqueue v

    let mutable found = -1

    while found < 0 && queue.Count > 0 do
        let v = queue.Dequeue()

        if isTo v && parent.[v] <> -1 then
            found <- v
        else
            for e in g.EdgeStart.[v] .. g.EdgeStart.[v + 1] - 1 do
                let t = g.EdgeTarget.[e]

                if parent.[t] = -2 && not (blocked t) then
                    parent.[t] <- v
                    queue.Enqueue t

    if found < 0 then
        None
    else
        let rec walk (v: int) (acc: int list) =
            if parent.[v] = -1 then
                v :: acc
            else
                walk parent.[v] (v :: acc)

        Some(walk found [])
