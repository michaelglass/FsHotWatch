module FsHotWatch.Bench.Tests.HeapGraphTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

// A hand-built heap with a known dominator tree:
//
//   root -> 0;  0 -> 1, 2;  1 -> 3;  2 -> 3, 6;  3 -> 4;  4 -> 5;  5 -> 4 (cycle)
//
// Node 3 has two predecessors (1 and 2), so neither dominates it: idom(3) = 0.
// The 4 <-> 5 cycle is entered only through 3.
//
//   idom: 0->root, 1->0, 2->0, 3->0, 4->3, 5->4, 6->2
//
// Sizes are powers of two so every retained sum is unambiguous.
let private sizes = [| 1L; 2L; 4L; 8L; 16L; 32L; 64L |]
let private edges = [| [ 1; 2 ]; [ 3 ]; [ 3; 6 ]; [ 4 ]; [ 5 ]; [ 4 ]; [] |]

// Types: 0,6 = A; 1,2 = B; 3,4,5 = C.
let private typeOfNode = [| 0; 1; 1; 2; 2; 2; 0 |]

/// The raw walk, in the runtime's shape: addresses deliberately NOT in node order.
let private raw () : HeapGraph.RawWalk =
    let address k = 1000L - int64 k * 10L

    { Addresses = Array.init 7 address
      Sizes = sizes
      TypeIds = typeOfNode |> Array.map uint64
      EdgeCounts = edges |> Array.map List.length
      EdgeTargets = edges |> Array.collect (fun es -> es |> List.map address |> List.toArray)
      RootAddresses = [| address 0 |]
      WeakTableEdges = [||] }

let private typeOf (id: uint64) =
    [| "A"; "B"; "C" |].[int id], "FSharp.Compiler.Service.dll"

let private graph () = fst (HeapGraph.build (raw ()) typeOf)

/// Map a node's index in the built graph back to its index in the picture above.
let private nodeOf (g: HeapGraph.Graph) (size: int64) = Array.findIndex ((=) size) g.Size

[<Fact>]
let ``build resolves every edge and root and reports no mismatch`` () =
    let g, report = HeapGraph.build (raw ()) typeOf
    test <@ report.Nodes = 7 && report.Edges = 8 @>

    test
        <@
            report.UnresolvedEdges = 0
            && report.UnresolvedRoots = 0
            && report.EdgeCountMismatch = 0L
        @>

    test <@ g.Roots = [| nodeOf g 1L |] @>
    test <@ g.TypeNames = [| "A"; "B"; "C" |] @>

[<Fact>]
let ``immediate dominators match the known tree, cycle and diamond included`` () =
    let g = graph ()
    let d = HeapGraph.dominators g
    let virtualRoot = HeapGraph.nodeCount g
    let idomOf (size: int64) = d.Idom.[nodeOf g size]
    let node (size: int64) = nodeOf g size

    test <@ idomOf 1L = virtualRoot @>
    test <@ idomOf 2L = node 1L && idomOf 4L = node 1L @>
    test <@ idomOf 8L = node 1L @> // the diamond join: neither branch dominates it
    test <@ idomOf 16L = node 8L && idomOf 32L = node 16L @> // cycle entered through 3
    test <@ idomOf 64L = node 4L @>

[<Fact>]
let ``children-first order lists every reachable node before its dominator`` () =
    let g = graph ()
    let d = HeapGraph.dominators g
    let n = HeapGraph.nodeCount g
    let position = Array.create (n + 1) n
    d.ChildrenFirst |> Array.iteri (fun i v -> position.[v] <- i)
    test <@ d.ChildrenFirst.Length = n @>

    for v in 0 .. n - 1 do
        test <@ position.[v] < position.[d.Idom.[v]] @>

[<Fact>]
let ``retained sizes sum each dominator subtree`` () =
    let g = graph ()
    let r = HeapGraph.retainedSizes g (HeapGraph.dominators g)
    let retainedOf (size: int64) = r.[nodeOf g size]

    test <@ retainedOf 32L = 32L && retainedOf 16L = 48L && retainedOf 8L = 56L @>
    test <@ retainedOf 64L = 64L && retainedOf 4L = 68L && retainedOf 2L = 2L @>
    test <@ retainedOf 1L = 127L @>
    test <@ r.[HeapGraph.nodeCount g] = 127L @>

[<Fact>]
let ``retained-by-type counts only top-most instances, so nesting is not double counted`` () =
    let g = graph ()
    let d = HeapGraph.dominators g
    let byType = HeapGraph.retainedByType g d (HeapGraph.retainedSizes g d)

    let find name =
        byType |> List.find (fun (t, _, _) -> g.TypeNames.[t] = name)

    // A: node 0 retains everything; node 6 sits under it and is not added again.
    test <@ find "A" |> fun (_, r, n) -> r = 127L && n = 2 @>
    // B: nodes 1 and 2 are dominator-tree siblings, both top-most.
    test <@ find "B" |> fun (_, r, n) -> r = 70L && n = 2 @>
    // C: node 3 is top-most; 4 and 5 are nested under it.
    test <@ find "C" |> fun (_, r, n) -> r = 56L && n = 3 @>

let private classes (g: HeapGraph.Graph) (reach: HeapGraph.Reach[]) (want: HeapGraph.Reach) =
    [ for v in 0 .. HeapGraph.nodeCount g - 1 do
          if reach.[v] = want then
              yield g.Size.[v] ]
    |> List.sort

[<Fact>]
let ``with node 1 as the import root, 3-4-5 are shared and 0-2-6 are session-only`` () =
    let g = graph ()

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 2L) (fun _ -> false) (fun _ -> false)

    test <@ classes g reach HeapGraph.Reach.ImportOnly = [ 2L ] @>
    test <@ classes g reach HeapGraph.Reach.Overlap = [ 8L; 16L; 32L ] @>
    test <@ classes g reach HeapGraph.Reach.SessionOnly = [ 1L; 4L; 64L ] @>
    test <@ List.isEmpty (classes g reach HeapGraph.Reach.Unreached) @>

[<Fact>]
let ``with node 2 as the import root, what only it reaches is import-only`` () =
    let g = graph ()

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 4L) (fun _ -> false) (fun _ -> false)

    test <@ classes g reach HeapGraph.Reach.ImportOnly = [ 4L; 64L ] @>
    test <@ classes g reach HeapGraph.Reach.Overlap = [ 8L; 16L; 32L ] @>
    test <@ classes g reach HeapGraph.Reach.SessionOnly = [ 1L; 2L ] @>

[<Fact>]
let ``a barrier stops the import flood at a hub, so what lies beyond it stays session-side`` () =
    // Import root 2 would reach 3-4-5 through the diamond join; with 3 a barrier (a hub
    // the imports point at, not import contents) only 2 and 6 are import-side.
    let g = graph ()

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 4L) (fun v -> g.Size.[v] = 8L) (fun _ -> false)

    test <@ classes g reach HeapGraph.Reach.ImportOnly = [ 4L; 64L ] @>
    test <@ List.isEmpty (classes g reach HeapGraph.Reach.Overlap) @>
    test <@ classes g reach HeapGraph.Reach.SessionOnly = [ 1L; 2L; 8L; 16L; 32L ] @>

[<Fact>]
let ``an import root that is also a GC root is not counted session-side`` () =
    let g =
        { graph () with
            Roots = Array.append (graph ()).Roots [| nodeOf (graph ()) 64L |] }

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 64L) (fun _ -> false) (fun _ -> false)

    test <@ classes g reach HeapGraph.Reach.ImportOnly = [ 64L ] @>

[<Fact>]
let ``the session flood does not pass through an owner of import contents`` () =
    // Import root 2 and owner 1: 1 reaches 3-4-5 as well. Without the owner block, 3-4-5
    // would be session-reachable through 1 (overlap); with it, only 0 is session-side
    // and 1 itself is unreached from both floods.
    let g = graph ()

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 4L) (fun _ -> false) (fun v -> g.Size.[v] = 2L)

    test <@ classes g reach HeapGraph.Reach.ImportOnly = [ 4L; 8L; 16L; 32L; 64L ] @>
    test <@ classes g reach HeapGraph.Reach.SessionOnly = [ 1L ] @>
    test <@ classes g reach HeapGraph.Reach.Unreached = [ 2L ] @>

[<Fact>]
let ``the partition's bytes sum to the selected nodes' bytes`` () =
    let g = graph ()

    let reach =
        HeapGraph.partition g (fun v -> g.Size.[v] = 2L) (fun _ -> false) (fun _ -> false)

    let isC v = g.TypeIndex.[v] = 2
    let all = HeapGraph.reachBytes g reach (fun _ -> true)
    test <@ HeapGraph.reachTotal all = 127L @>
    test <@ HeapGraph.reachTotal (HeapGraph.reachBytes g reach isC) = 56L @>
    test <@ (HeapGraph.reachBytes g reach isC).Overlap = 56L @>

[<Fact>]
let ``unresolved edges and roots are counted and dropped, and a short edge stream is a mismatch`` () =
    let r = raw ()

    let broken =
        { r with
            EdgeTargets = Array.append [| 4242L |] r.EdgeTargets.[1..]
            RootAddresses = Array.append r.RootAddresses [| 7L |] }

    let _, report = HeapGraph.build broken typeOf

    test
        <@
            report.UnresolvedEdges = 1
            && report.UnresolvedRoots = 1
            && report.EdgeCountMismatch = 0L
        @>

    let short =
        { r with
            EdgeTargets = r.EdgeTargets.[1..] }

    let _, shortReport = HeapGraph.build short typeOf
    test <@ shortReport.EdgeCountMismatch = 1L @>

[<Fact>]
let ``a weak-table value lives through its key`` () =
    // An extra node 7 (size 128) held only by a ConditionalWeakTable keyed on node 6.
    let r = raw ()

    let withWeak =
        { r with
            Addresses = Array.append r.Addresses [| 5L |]
            Sizes = Array.append r.Sizes [| 128L |]
            TypeIds = Array.append r.TypeIds [| 0UL |]
            EdgeCounts = Array.append r.EdgeCounts [| 0 |]
            WeakTableEdges = [| r.Addresses.[6], 5L |] }

    let g, _ = HeapGraph.build withWeak typeOf
    let d = HeapGraph.dominators g
    test <@ d.Idom.[nodeOf g 128L] = nodeOf g 64L @>

/// Brute-force reference: `d` dominates `v` iff `v` is unreachable from the virtual root
/// once `d` is removed. The immediate dominator is the strict dominator every other strict
/// dominator dominates, i.e. the one with the most strict dominators of its own.
let private bruteForceIdom (g: HeapGraph.Graph) : int[] =
    let n = HeapGraph.nodeCount g

    let reachableWithout (removed: int) =
        let seen = Array.zeroCreate<bool> n
        let stack = System.Collections.Generic.Stack<int>()

        for r in g.Roots do
            if r <> removed && not seen.[r] then
                seen.[r] <- true
                stack.Push r

        while stack.Count > 0 do
            let v = stack.Pop()

            for e in g.EdgeStart.[v] .. g.EdgeStart.[v + 1] - 1 do
                let t = g.EdgeTarget.[e]

                if t <> removed && not seen.[t] then
                    seen.[t] <- true
                    stack.Push t

        seen

    let reachable = reachableWithout -1
    let without = Array.init n reachableWithout
    // strict dominators of v (real nodes only; the virtual root dominates everything)
    let strict v =
        [ for d in 0 .. n - 1 do
              if d <> v && not without.[d].[v] then
                  yield d ]

    Array.init n (fun v ->
        if not reachable.[v] then
            -1
        else
            match strict v with
            | [] -> n
            | ds -> ds |> List.maxBy (fun d -> List.length (strict d)))

[<Fact>]
let ``Lengauer-Tarjan agrees with brute force on random graphs`` () =
    let rng = System.Random(677)

    for _ in 1..200 do
        let n = rng.Next(1, 14)

        let edgeLists =
            Array.init n (fun _ -> List.init (rng.Next(0, 4)) (fun _ -> rng.Next n))

        let roots = Array.init (rng.Next(1, 3)) (fun _ -> rng.Next n) |> Array.distinct

        let g: HeapGraph.Graph =
            { Size = Array.create n 1L
              TypeIndex = Array.zeroCreate n
              TypeNames = [| "T" |]
              TypeModules = [| "" |]
              EdgeStart = Array.scan (+) 0 (edgeLists |> Array.map List.length)
              EdgeTarget = edgeLists |> Array.collect List.toArray
              Roots = roots }

        let expected = bruteForceIdom g
        let actual = (HeapGraph.dominators g).Idom.[0 .. n - 1]
        test <@ actual = expected @>
