module FsHotWatch.Tests.TestRootTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestRoots

// AUTOMATION-1775: every test that needs disk gets a root it alone owns. These pin the
// three properties the rule FSHW-ROOT-001 relies on: a root is fresh and private, it is
// gone after its owner disposes it, and a path cannot climb out of it.

let private isUnder (parent: string) (path: string) =
    path.StartsWith(
        Path.TrimEndingDirectorySeparator parent + string Path.DirectorySeparatorChar,
        StringComparison.Ordinal
    )

[<Fact(Timeout = 15000)>]
let ``two roots are distinct fresh directories under this process's root`` () =
    use a = TestRoot.create "tr"
    use b = TestRoot.create "tr"

    test <@ a.Path <> b.Path @>
    test <@ Directory.Exists a.Path && Directory.Exists b.Path @>
    test <@ Directory.GetFileSystemEntries a.Path |> Array.isEmpty @>
    test <@ isUnder processRoot.Value a.Path && isUnder processRoot.Value b.Path @>
    test <@ Path.GetFileName(a.Path).StartsWith("tr-", StringComparison.Ordinal) @>

[<Fact(Timeout = 15000)>]
let ``a root is removed when its owner disposes it, files and all`` () =
    let root = TestRoot.create "tr-dispose"
    Directory.CreateDirectory(root.Under "nested") |> ignore
    File.WriteAllText(root.Under("nested", "file.txt"), "x")

    (root :> IDisposable).Dispose()

    test <@ not (Directory.Exists root.Path) @>

[<Fact(Timeout = 15000)>]
let ``withRoot removes the root even when the body throws`` () =
    let mutable seen = ""

    let thrown =
        try
            TestRoot.withRoot "tr-throw" (fun root ->
                seen <- root.Path
                failwith "boom")
        with ex ->
            ex.Message

    test <@ thrown = "boom" @>
    test <@ seen <> "" && not (Directory.Exists seen) @>

[<Fact(Timeout = 15000)>]
let ``a path under a root is relative and never climbs out`` () =
    TestRoot.withRoot "tr-under" (fun root ->
        test <@ root.Under("a/b", "c.fs") = Path.Combine(root.Path, "a/b", "c.fs") @>

        let refused (parts: string array) =
            try
                root.Under parts |> ignore
                false
            with :? ArgumentException ->
                true

        test <@ refused [| "/etc/passwd" |] @>
        test <@ refused [| ".." |] @>
        test <@ refused [| "a/../../b" |] @>
        test <@ refused [| "a"; "" |] @>
        // A name that merely contains dots is not a climb.
        test <@ not (refused [| "..a"; "b.." |]) @>)

[<Fact(Timeout = 15000)>]
let ``the physical path names the same directory with no symlinked component`` () =
    TestRoot.withRoot "tr-physical" (fun root ->
        File.WriteAllText(root.Under "marker", "here")
        let physical = root.Physical

        test <@ File.ReadAllText(Path.Combine(physical, "marker")) = "here" @>

        // Every ancestor of the physical path is a real directory, not a link: the form
        // `getcwd` reports, which a consumer keying files by exact string compares to.
        let rec ancestors (dir: DirectoryInfo) =
            if isNull dir then [] else dir :: ancestors dir.Parent

        let links =
            ancestors (DirectoryInfo physical)
            |> List.filter (fun d -> not (isNull d.LinkTarget))
            |> List.map _.FullName

        test <@ List.isEmpty links @>)

[<Fact(Timeout = 15000)>]
let ``this process's root carries the repository's global.json`` () =
    let copied = Path.Combine(processRoot.Value, "global.json")
    test <@ File.ReadAllText copied = File.ReadAllText(RepositoryTree.path "global.json") @>

[<Fact(Timeout = 15000)>]
let ``repository and test-binary paths resolve named files and refuse a climb`` () =
    test <@ File.Exists(RepositoryTree.path "FsHotWatch.slnx") @>
    test <@ File.Exists(RepositoryTree.path "tests/FsHotWatch.Tests/TestRoot.fs") @>
    test <@ File.Exists(TestBinary.path "FsHotWatch.Tests.dll") @>

    let climbs (f: string -> string) =
        try
            f "../outside" |> ignore
            false
        with :? ArgumentException ->
            true

    let repositoryRefuses = climbs RepositoryTree.path
    let binaryRefuses = climbs TestBinary.path
    test <@ repositoryRefuses && binaryRefuses @>

[<Fact(Timeout = 15000)>]
let ``the drop-ins name fresh places under this process's root`` () =
    let path = TestRoot.freshPath "tr-path"
    let other = TestRoot.freshPath "tr-path"
    let dir = TestRoot.freshDirectory "tr-dir"
    let file = TestRoot.freshFile "tr-file"

    try
        test <@ path <> other && not (Directory.Exists path) && not (File.Exists path) @>
        test <@ dir.Exists && Directory.GetFileSystemEntries dir.FullName |> Array.isEmpty @>
        test <@ File.Exists file && FileInfo(file).Length = 0L @>

        test
            <@
                [ path; dir.FullName; file ]
                |> List.forall (fun p -> Path.GetDirectoryName p = processRoot.Value)
            @>
    finally
        dir.Delete true
        File.Delete file
