module FsHotWatch.Tests.CachePathIdentityTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch

let private under (root: string) (relative: string) =
    Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))

[<Fact>]
let ``the same repo-relative file has the same portable key in different roots`` () =
    let roots =
        [ Path.Combine(Path.GetTempPath(), "checkout-a")
          Path.Combine(Path.GetTempPath(), "checkout-b") ]

    let keys =
        roots
        |> List.map (fun root ->
            CachePathIdentity.ofPath root (under root "src/Feature/File.fs")
            |> CachePathIdentity.toKey)

    test <@ keys = [ "repo:src/Feature/File.fs"; "repo:src/Feature/File.fs" ] @>

[<Fact>]
let ``relative-path identity is invariant across roots for varied path shapes`` () =
    let roots =
        [ Path.Combine(Path.GetTempPath(), "checkout-a")
          Path.Combine(Path.GetTempPath(), "checkout-b")
          Path.Combine(Path.GetTempPath(), "nested", "checkout-c") ]

    let relativePaths =
        [ "File.fs"
          "src/File.fs"
          "src/a b/File.fs"
          "src/Unicode-λ/File.fs"
          "src/deep/nested/File.fs" ]

    for relative in relativePaths do
        let keys =
            roots
            |> List.map (fun root -> CachePathIdentity.ofPath root (under root relative) |> CachePathIdentity.toKey)

        test <@ keys |> List.distinct = [ "repo:" + relative ] @>

[<Fact>]
let ``portable keys round-trip and rebind beneath another root`` () =
    let sourceRoot = Path.Combine(Path.GetTempPath(), "source-checkout")
    let targetRoot = Path.Combine(Path.GetTempPath(), "target-checkout")

    let key =
        CachePathIdentity.ofPath sourceRoot (under sourceRoot "src/App.fs")
        |> CachePathIdentity.toKey

    let rebound =
        key
        |> CachePathIdentity.tryParse
        |> Option.bind (CachePathIdentity.tryRebind targetRoot)

    test <@ rebound = Some(Path.GetFullPath(under targetRoot "src/App.fs")) @>

[<Fact>]
let ``outside and segment-prefix paths are not portable across roots`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")

    let cases =
        [ Path.Combine(Path.GetTempPath(), "outside", "File.fs")
          Path.Combine(Path.GetTempPath(), "repo-other", "File.fs") ]

    for path in cases do
        let identity = CachePathIdentity.ofPath root path
        test <@ CachePathIdentity.toKey identity = "external:" + Path.GetFullPath(path) @>
        test <@ CachePathIdentity.tryRebind root identity = None @>

[<Fact>]
let ``dot-dot is normalized before containment is decided`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")
    let inside = under root "src/../src/File.fs"
    let outside = under root "../outside/File.fs"

    test <@ CachePathIdentity.ofPath root inside |> CachePathIdentity.toKey = "repo:src/File.fs" @>
    test <@ CachePathIdentity.ofPath root outside |> CachePathIdentity.tryRebind root = None @>

[<Fact>]
let ``codec rejects malformed and escaping repo-relative keys`` () =
    let invalid =
        [ ""
          "src/File.fs"
          "repo:/absolute.fs"
          "repo:../escape.fs"
          "repo:a/../../escape.fs"
          "repo:a\\b.fs"
          "repo:a:b.fs"
          "repo:CON/file.fs"
          "repo:COM¹/file.fs"
          "repo:LPT².txt"
          "repo:src/trailing."
          "repo:src/trailing "
          "repo:src//File.fs"
          "repo:src/\u0000/File.fs"
          "external:relative.fs"
          "external:/tmp/../tmp/file.fs"
          "other:value" ]

    invalid
    |> List.iter (fun key -> test <@ CachePathIdentity.tryParse key = None @>)

[<Fact>]
let ``distinct relative paths remain distinct and case is preserved`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")
    let relativePaths = [ "src/A.fs"; "src/a.fs"; "src/nested/A.fs" ]

    let keys =
        relativePaths
        |> List.map (under root >> CachePathIdentity.ofPath root >> CachePathIdentity.toKey)

    test <@ Set.count (Set.ofList keys) = relativePaths.Length @>

[<Fact>]
let ``checkout root itself has a portable identity`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")
    let identity = CachePathIdentity.ofPath root root

    test <@ CachePathIdentity.toKey identity = "repo:." @>
    test <@ CachePathIdentity.tryRebind root identity = Some(Path.GetFullPath root) @>

[<Fact>]
let ``valid portable path corpus round-trips without key collisions`` () =
    let root = Path.Combine(Path.GetTempPath(), "repo")

    let segments =
        [ "a"; "A"; "a-b"; "a_b"; "a b"; "λ"; "日本語"; "file.fs"; "name.with.dots" ]

    let relativePaths =
        [ yield! segments

          for left in segments do
              for right in segments do
                  yield left + "/" + right ]

    let identities =
        relativePaths
        |> List.map (fun relative -> CachePathIdentity.ofPath root (under root relative))

    let keys = identities |> List.map CachePathIdentity.toKey
    test <@ keys.Length = (keys |> Set.ofList |> Set.count) @>

    List.zip identities keys
    |> List.iter (fun (identity, key) -> test <@ CachePathIdentity.tryParse key = Some identity @>)

[<Fact>]
let ``Unix backslash filename cannot collide with a directory separator`` () =
    if Path.DirectorySeparatorChar = '/' then
        let root = Path.Combine(Path.GetTempPath(), "repo")

        let backslashName =
            CachePathIdentity.ofPath root (Path.Combine(root, "src", "a\\b.fs"))

        let nestedName =
            CachePathIdentity.ofPath root (Path.Combine(root, "src", "a", "b.fs"))

        test <@ CachePathIdentity.toKey backslashName <> CachePathIdentity.toKey nestedName @>
        test <@ CachePathIdentity.tryRebind root backslashName = None @>
