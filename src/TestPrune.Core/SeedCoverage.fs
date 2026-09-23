/// Covering test projects for many seeds at once, from ONE loaded region of the
/// dependency graph.
///
/// `QueryAffectedTests [seed]` walks the graph from one seed. Asking it once per seed
/// repeats the same traversal for every seed that shares a dependent, which is most of
/// them in a wide change: each walk re-reads the hubs every other walk already read. This
/// module answers the per-seed question from one region instead. It computes, once per
/// strongly connected component, the test projects reachable from it, then reads each
/// seed's answer from its start symbols.
///
/// The answer must equal `QueryAffectedTests [seed]` projected to test projects, including
/// under composition-root barriers. The barriered reachability here stops at EVERY marked
/// symbol, while the single-seed walk stops only at marked symbols outside that seed's own
/// start set. The two agree because a start symbol is expanded from its own start term:
/// a marked start reached again from another start is already covered. The per-project
/// fail-safe is `Domain.CompositionRoot.restoreEmptiedProjects`, called here exactly as
/// `QueryAffectedTests` calls it.
module internal TestPrune.SeedCoverage

open System.Collections.Generic

/// The region of the graph a grouped walk loaded.
type Region =
    {
        /// Each seed's start symbols: its own id, lifted to a containing type, and
        /// expanded to that type's members, exactly as `QueryAffectedTests` seeds its walk.
        /// A seed the index does not know has no entry.
        Starts: IReadOnlyDictionary<string, int64 list>
        /// Who depends on each symbol, over the reverse closure of every start.
        Dependents: IReadOnlyDictionary<int64, int64 list>
        /// The test projects of the test methods sitting on each symbol.
        Projects: IReadOnlyDictionary<int64, Set<string>>
        /// Composition-root markers in the region.
        Barriers: IReadOnlySet<int64>
    }

let private listAt (map: IReadOnlyDictionary<int64, int64 list>) (id: int64) =
    match map.TryGetValue id with
    | true, values -> values
    | _ -> []

/// For every symbol reachable from `roots` through `successors`, the union of `own` over
/// everything it reaches, itself included. One pass: an iterative Tarjan emits each
/// strongly connected component after every component it reaches, so each component's
/// set is built once from finished successors. Iterative because an index's dependency
/// chains can be deeper than the stack.
let private reachability
    (roots: int64 seq)
    (successors: int64 -> int64 list)
    (own: int64 -> Set<string>)
    : int64 -> Set<string> =
    let index = Dictionary<int64, int>()
    let low = Dictionary<int64, int>()
    let onStack = HashSet<int64>()
    let stack = Stack<int64>()
    let componentOf = Dictionary<int64, int>()
    let sets = ResizeArray<Set<string>>()
    let mutable counter = 0

    for root in roots do
        if not (index.ContainsKey root) then
            let work = Stack<int64 * IEnumerator<int64>>()

            let visit (node: int64) =
                index[node] <- counter
                low[node] <- counter
                counter <- counter + 1
                stack.Push node
                onStack.Add node |> ignore
                work.Push((node, (successors node :> seq<int64>).GetEnumerator()))

            visit root

            while work.Count > 0 do
                let node, pending = work.Peek()

                if pending.MoveNext() then
                    let next = pending.Current

                    if not (index.ContainsKey next) then
                        visit next
                    elif onStack.Contains next then
                        low[node] <- min low[node] index[next]
                else
                    work.Pop() |> ignore

                    if work.Count > 0 then
                        let parent, _ = work.Peek()
                        low[parent] <- min low[parent] low[node]

                    if low[node] = index[node] then
                        let members = ResizeArray<int64>()
                        let mutable popping = true

                        while popping do
                            let popped = stack.Pop()
                            onStack.Remove popped |> ignore
                            members.Add popped
                            popping <- popped <> node

                        let componentId = sets.Count

                        for m in members do
                            componentOf[m] <- componentId

                        let mutable reached = Set.empty

                        for m in members do
                            reached <- Set.union reached (own m)

                            for next in successors m do
                                match componentOf.TryGetValue next with
                                | true, other when other <> componentId -> reached <- Set.union reached sets[other]
                                | _ -> ()

                        sets.Add reached

    fun id ->
        match componentOf.TryGetValue id with
        | true, c -> sets[c]
        | _ -> own id

/// For each seed, the test projects `QueryAffectedTests [seed]` selects tests from.
/// Every seed is present in the result; one the index does not know maps to the empty set.
let coveringProjects (region: Region) (seeds: string list) : Map<string, Set<string>> =
    let dependents = listAt region.Dependents

    let own (id: int64) =
        match region.Projects.TryGetValue id with
        | true, projects -> projects
        | _ -> Set.empty

    let startsOf (seed: string) =
        match region.Starts.TryGetValue seed with
        | true, starts -> starts
        | _ -> []

    let allStarts = region.Starts.Values |> Seq.concat |> Seq.distinct |> List.ofSeq

    let unbarriered = reachability allStarts dependents own

    // Only a region holding a marker can narrow anything; without one the barriered walk
    // is the unbarriered one, and `QueryAffectedTests` skips it the same way.
    let barriered =
        if region.Barriers.Count = 0 then
            None
        else
            let stopsAt (id: int64) = region.Barriers.Contains id

            let successors (id: int64) =
                if stopsAt id then [] else dependents id

            Some(reachability (allStarts |> List.collect dependents) successors own)

    seeds
    |> List.map (fun seed ->
        let starts = startsOf seed
        let full = starts |> List.map unbarriered |> Set.unionMany

        let projects =
            match barriered with
            | None -> full
            | Some reach ->
                // A start is always expanded, marker or not: the single-seed walk exempts
                // its own start set from the barriers.
                let narrowed =
                    starts
                    |> List.map (fun start ->
                        Set.union (own start) (dependents start |> List.map reach |> Set.unionMany))
                    |> Set.unionMany

                Domain.CompositionRoot.restoreEmptiedProjects id (Set.toList narrowed) (Set.toList full)
                |> Set.ofList

        seed, projects)
    |> Map.ofList
