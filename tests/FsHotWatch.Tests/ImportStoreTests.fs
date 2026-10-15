module FsHotWatch.Tests.ImportStoreTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private hasher () = CheckCache.FileContentHasher()

let private writeBytes (path: string) (bytes: byte array) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllBytes(path, bytes)
    path

[<Fact>]
let ``identical bytes at two paths are placed at one store path`` () =
    withTempDir "import-store-same" (fun dir ->
        let store = ImportStore.Store(Path.Combine(dir, "store"), (hasher ()).Hash)
        let a = writeBytes (Path.Combine(dir, "a", "lib", "X.dll")) [| 1uy; 2uy; 3uy |]
        let b = writeBytes (Path.Combine(dir, "b", "lib", "X.dll")) [| 1uy; 2uy; 3uy |]

        let placedA = store.Place a
        let placedB = store.Place b

        test <@ placedA = Some(placedB.Value) @>
        test <@ Path.GetFileName placedA.Value = "X.dll" @>
        test <@ File.ReadAllBytes placedA.Value = [| 1uy; 2uy; 3uy |] @>)

[<Fact>]
let ``different bytes under one name are placed apart`` () =
    withTempDir "import-store-different" (fun dir ->
        let store = ImportStore.Store(Path.Combine(dir, "store"), (hasher ()).Hash)
        let a = writeBytes (Path.Combine(dir, "a", "X.dll")) [| 1uy |]
        let b = writeBytes (Path.Combine(dir, "b", "X.dll")) [| 2uy |]

        test <@ store.Place a <> store.Place b @>
        test <@ File.ReadAllBytes (store.Place b).Value = [| 2uy |] @>)

[<Fact>]
let ``one content under two names is placed under each name`` () =
    // FCS resolves an assembly by its file's simple name, so the name is kept.
    withTempDir "import-store-names" (fun dir ->
        let store = ImportStore.Store(Path.Combine(dir, "store"), (hasher ()).Hash)
        let x = writeBytes (Path.Combine(dir, "X.dll")) [| 7uy |]
        let y = writeBytes (Path.Combine(dir, "Y.dll")) [| 7uy |]

        test <@ Path.GetFileName (store.Place x).Value = "X.dll" @>
        test <@ Path.GetFileName (store.Place y).Value = "Y.dll" @>)

[<Fact>]
let ``a placed file's write time is derived from its content`` () =
    withTempDir "import-store-stamp" (fun dir ->
        let hash = (hasher ()).Hash
        let store = ImportStore.Store(Path.Combine(dir, "store"), hash)
        let x = writeBytes (Path.Combine(dir, "X.dll")) [| 9uy |]

        let placed = (store.Place x).Value

        test <@ File.GetLastWriteTimeUtc placed = ImportStore.stampOf (hash x) @>)

[<Fact>]
let ``a store file whose bytes no longer match is replaced, never served`` () =
    withTempDir "import-store-corrupt" (fun dir ->
        let root = Path.Combine(dir, "store")
        let x = writeBytes (Path.Combine(dir, "X.dll")) [| 4uy; 5uy |]
        let placed = (ImportStore.Store(root, (hasher ()).Hash).Place x).Value
        File.WriteAllBytes(placed, [| 0uy |])

        // A new host process: nothing verified yet.
        let again = (ImportStore.Store(root, (hasher ()).Hash).Place x).Value

        test <@ again = placed @>
        test <@ File.ReadAllBytes again = [| 4uy; 5uy |] @>)

[<Fact>]
let ``a missing file is not placed`` () =
    withTempDir "import-store-missing" (fun dir ->
        let store = ImportStore.Store(Path.Combine(dir, "store"), (hasher ()).Hash)
        test <@ store.Place(Path.Combine(dir, "Gone.dll")) = None @>)

[<Fact>]
let ``concurrent placements of one content agree and leave no temporary file`` () =
    withTempDir "import-store-concurrent" (fun dir ->
        let root = Path.Combine(dir, "store")
        let store = ImportStore.Store(root, (hasher ()).Hash)
        let bytes = Array.init 100000 byte

        let sources =
            [ for i in 1..16 -> writeBytes (Path.Combine(dir, $"w%d{i}", "X.dll")) bytes ]

        let placed =
            sources
            |> List.map (fun s -> Task.Run(fun () -> store.Place s))
            |> Task.WhenAll
            |> _.Result
            |> Array.distinct

        test <@ placed.Length = 1 @>
        test <@ File.ReadAllBytes placed[0].Value = bytes @>
        test <@ Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length = 1 @>)
