module FsHotWatch.Tests.SnapshotMemoTests

// Reads FSharpProjectSnapshot, which FCS marks experimental.
#nowarn "57"

open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.ProjectSnapshots
open FsHotWatch.Tests.TestHelpers

/// Upstream A (one file) and B (`n` files) referencing A in memory and through `-r:`
/// A's output. Nothing is on disk: content hashes come from `hashes`, and a path with
/// no entry hashes to "h0".
type private Tree =
    { A: FSharpProjectOptions
      B: FSharpProjectOptions
      BFiles: string list
      Hashes: ConcurrentDictionary<string, string>
      Generations: ConcurrentDictionary<string, int64> }

    member tree.Hash(path: string) =
        match tree.Hashes.TryGetValue path with
        | true, hash -> hash
        | false, _ -> "h0"

    member tree.Generation(project: string) =
        match tree.Generations.TryGetValue project with
        | true, n -> Generation n
        | false, _ -> Generation 0L

    member tree.Open(path: string) : OpenFile =
        { Path = path
          Version = tree.Hash path
          Text = $"// %s{Path.GetFileName path}" }

let private tree (n: int) =
    let dir = Path.Combine(Path.GetTempPath(), "fshw-snapshot-memo")
    let aDll = Path.Combine(dir, "A", "bin", "A.dll")

    let a =
        makeProjectOptions (Path.Combine(dir, "A.fsproj")) [ Path.Combine(dir, "A.fs") ] [ "--noframework" ]

    let bFiles = [ for i in 1..n -> Path.Combine(dir, $"B%d{i}.fs") ]

    let b =
        { makeProjectOptions (Path.Combine(dir, "B.fsproj")) bFiles [ $"-r:%s{aDll}"; "--noframework" ] with
            ReferencedProjects = [| FSharpReferencedProject.FSharpReference(aDll, a) |] }

    { A = a
      B = b
      BFiles = bFiles
      Hashes = ConcurrentDictionary()
      Generations = ConcurrentDictionary() }

let private buildWith (memo: SnapshotMemo) (tree: Tree) (file: string) =
    buildFramedWith memo tree.Generation tree.Hash None PathFrame.realPaths (tree.Open file) tree.B

let private buildFresh (tree: Tree) (file: string) =
    buildFramed tree.Generation tree.Hash None PathFrame.realPaths (tree.Open file) tree.B

[<Fact>]
let ``checking every file of a project builds each project's snapshot once`` () =
    let tree = tree 40
    let memo = SnapshotMemo()

    for file in tree.BFiles do
        buildWith memo tree file |> ignore

    // B and A, once each — not once per file checked.
    test <@ memo.Builds = 2 @>

[<Fact>]
let ``a memoized snapshot asks FCS exactly what a fresh build asks`` () =
    let tree = tree 5
    tree.Hashes[List.item 2 tree.BFiles] <- "edited"
    let memo = SnapshotMemo()

    for file in tree.BFiles do
        let memoized = buildWith memo tree file
        let fresh = buildFresh tree file

        test <@ memoized.SnapshotKey = snapshotKey fresh.Snapshot @>
        test <@ memoized.SnapshotKey = snapshotKey memoized.Snapshot @>

        let stamps (framed: Framed) =
            framed.Snapshot.ReferencesOnDisk |> List.map (fun r -> r.Path, r.LastModified)

        test <@ stamps memoized = stamps fresh @>

        test <@ memoized.Snapshot.ReferencedProjects.Length = fresh.Snapshot.ReferencedProjects.Length @>

[<Fact>]
let ``the open file's snapshot serves the text that was read for it`` () =
    let tree = tree 3
    let memo = SnapshotMemo()
    let file = List.head tree.BFiles
    let opened = tree.Open file

    let built =
        buildFramedWith memo tree.Generation tree.Hash None PathFrame.realPaths opened tree.B

    let source =
        (built.Snapshot.SourceFiles |> List.find (fun f -> f.FileName = file)).GetSource().Result

    test <@ source.ToString() = opened.Text @>

[<Fact>]
let ``an edited file rebuilds its project and the projects downstream of it only`` () =
    let tree = tree 10
    let memo = SnapshotMemo()
    let file = List.head tree.BFiles

    buildWith memo tree file |> ignore
    test <@ memo.Builds = 2 @>

    // An edit in B: B again, A reused.
    tree.Hashes[List.last tree.BFiles] <- "edited"
    buildWith memo tree file |> ignore
    test <@ memo.Builds = 3 @>

    // An edit in A: A and B again.
    tree.Hashes[tree.A.SourceFiles[0]] <- "edited"
    buildWith memo tree file |> ignore
    test <@ memo.Builds = 5 @>

    // Nothing changed: nothing built.
    for f in tree.BFiles do
        buildWith memo tree f |> ignore

    test <@ memo.Builds = 5 @>

[<Fact>]
let ``a project's new generation rebuilds it and the projects downstream of it`` () =
    let tree = tree 4
    let memo = SnapshotMemo()
    let file = List.head tree.BFiles

    let before = buildWith memo tree file
    tree.Generations[tree.B.ProjectFileName] <- 1L
    let afterB = buildWith memo tree file

    test <@ memo.Builds = 3 @>
    test <@ afterB.SnapshotKey <> before.SnapshotKey @>

    tree.Generations[tree.A.ProjectFileName] <- 1L
    buildWith memo tree file |> ignore
    test <@ memo.Builds = 5 @>

[<Fact>]
let ``concurrent checks of one project's files build each snapshot once`` () =
    let tree = tree 64
    let memo = SnapshotMemo()

    Parallel.ForEach(tree.BFiles, (fun (file: string) -> buildWith memo tree file |> ignore))
    |> ignore

    test <@ memo.Builds = 2 @>

[<Fact>]
let ``an open file read while it moved is checked under its own version`` () =
    let tree = tree 3
    let memo = SnapshotMemo()
    let file = List.head tree.BFiles
    buildWith memo tree file |> ignore

    let moved =
        { tree.Open file with
            Version = "text:moved" }

    let built =
        buildFramedWith memo tree.Generation tree.Hash None PathFrame.realPaths moved tree.B

    let version =
        (built.Snapshot.SourceFiles |> List.find (fun f -> f.FileName = file)).Version

    test <@ version = "text:moved" @>
    test <@ built.SnapshotKey = snapshotKey built.Snapshot @>
    // The memoized entry still describes the disk: the next ordinary check reuses it.
    let buildsBefore = memo.Builds
    buildWith memo tree (List.last tree.BFiles) |> ignore
    test <@ memo.Builds = buildsBefore @>
