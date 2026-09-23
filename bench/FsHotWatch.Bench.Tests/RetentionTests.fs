module FsHotWatch.Bench.Tests.RetentionTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

// A miniature of the shape the real daemon's heap graph showed:
//
//   GC root -> 0 TransparentCompiler -> 1 TcImports -> 2 ImportedAssembly -> 3 Entity
//              0 -> 4 SynBinding -> 3            (checked source REFERENCES an import)
//              2 -> 1                            (import points back at its hub)
//              1 -> 5 TcConfig -> 0              (the hub leads back to the checker)
//
// Sizes 1, 2, 4, 8, 16, 32.
let private names =
    [| "TransparentCompiler"
       "TcImports"
       "ImportedAssembly"
       "Entity"
       "FSharp.Compiler.Syntax.SynBinding"
       "TcConfig" |]

let private heap (extraEdges: (int * int) list) : HeapGraph.Graph * HeapGraph.BuildReport =
    let edges = [ 0, 1; 1, 2; 2, 3; 0, 4; 4, 3; 2, 1; 1, 5; 5, 0 ] @ extraEdges

    let address k = 100L + int64 k * 8L

    let bySource =
        [| for k in 0..5 -> edges |> List.filter (fst >> (=) k) |> List.map snd |]

    HeapGraph.build
        { Addresses = Array.init 6 address
          Sizes = [| 1L; 2L; 4L; 8L; 16L; 32L |]
          TypeIds = Array.init 6 uint64
          EdgeCounts = bySource |> Array.map List.length
          EdgeTargets = bySource |> Array.collect (List.map address >> List.toArray)
          RootAddresses = [| address 0 |]
          WeakTableEdges = [||] }
        (fun id -> names.[int id], "FSharp.Compiler.Service.dll")

[<Fact>]
let ``barriers keep the checker off the import side and owners keep imports off the session side`` () =
    let g, report = heap []
    let r = Retention.evaluate g report 63L Retention.importedAssemblies

    test <@ r.Result.Roots = 1 @>
    test <@ r.Result.All.ImportOnly = 4L @> // the ImportedAssembly itself
    test <@ r.Result.All.Overlap = 8L @> // the Entity the syntax tree references
    test <@ r.Result.All.SessionOnly = 17L @> // checker + syntax
    test <@ r.Result.All.Unreached = 34L @> // hub + config: behind an owner
    test <@ r.Narrowed.AccountingError = 0.0 @>
    test <@ r.Narrowed.PerSessionOnImportSide = 0L @>
    // Bare FCS names default to the typed-tree bucket, so its total here is every node but
    // the syntax tree (1+2+4+8+32 = 47): import-only 4, overlap 8.
    test <@ r.Narrowed.TypedTreeLow = 4.0 / 47.0 && r.Narrowed.TypedTreeHigh = 12.0 / 47.0 @>
    test <@ List.isEmpty r.Problems @>

[<Fact>]
let ``an import that reaches checked source trips the leak control`` () =
    // Entity -> SynBinding: now the syntax tree is import-reachable.
    let g, report = heap [ 3, 4 ]
    let r = Retention.evaluate g report 63L Retention.importedAssemblies
    test <@ r.Narrowed.PerSessionOnImportSide = 16L @>

    test
        <@
            r.Problems
            |> List.exists (fun p -> p.Contains "per-session types on the import side")
        @>

[<Fact>]
let ``a heap with no import roots, a broken graph or a partition that misses bytes is untrusted`` () =
    let g, report = heap []

    let noRoots =
        Retention.evaluate
            g
            report
            63L
            { Retention.importedAssemblies with
                Types = [ "Nope" ] }

    test <@ noRoots.Problems |> List.exists (fun p -> p.Contains "nothing to partition") @>

    let mismatched =
        Retention.evaluate g { report with EdgeCountMismatch = 3L } 63L Retention.importedAssemblies

    test <@ mismatched.Problems |> List.exists (fun p -> p.Contains "pairing") @>

    let missing = Retention.evaluate g report 100L Retention.importedAssemblies
    test <@ missing.Problems |> List.exists (fun p -> p.Contains "covers the live heap") @>

[<Fact>]
let ``a record carries the retention reading and the summary scores only trusted ones`` () =
    let g, report = heap []
    let trusted = Retention.evaluate g report 63L Retention.importedAssemblies

    let leaky =
        Retention.evaluate (fst (heap [ 3, 4 ])) report 63L Retention.importedAssemblies

    let row (reading: Retention.Reading) =
        let json =
            Record.toJsonLine
                { RunId = "r"
                  RecordedAt = System.DateTime.UtcNow
                  Label = "l"
                  Mode = "legacy"
                  Position =
                    { Sessions = 1
                      Rep = 1
                      Session = 1
                      Phase = "post-gc" }
                  Worktree = ""
                  Pid = 1
                  Alive = true
                  SleepGapMs = 0.0
                  Load =
                    { Load1 = 0.0
                      Load5 = 0.0
                      Load15 = 0.0
                      Cpus = 1
                      MemBytes = 0L
                      MemFreePercent = None
                      SwapUsedBytes = None
                      ForeignDaemons = [] }
                  Contended = []
                  Footprint = None
                  FootprintBeforeWalk = None
                  Gc = None
                  Heap = None
                  Retention = Some reading
                  Config = Record.noConfig
                  Scan = None
                  Tests = None
                  PhaseMs = None
                  Invalid = [] }

        (Record.tryParseLine json).Value

    test <@ (row trusted).RetentionHigh = Some(12.0 / 63.0) @>
    test <@ (row trusted).RetentionTypedTreeHigh = Some(12.0 / 47.0) @>
    test <@ (row leaky).RetentionHigh = None @>
