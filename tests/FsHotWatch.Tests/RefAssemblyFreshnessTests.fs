/// AUTOMATION-1498: is a project's published reference assembly (`obj/<cfg>/<tfm>/ref/`)
/// the one the compiler last wrote (`refint/`)? The real-compiler reproduction is
/// `RefAssemblyBuildTests` in the integration tests; these pin the rule on files.
module FsHotWatch.Tests.RefAssemblyFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.RefAssemblyFreshness
open FsHotWatch.Tests.TestHelpers

/// `Lib/Lib.fsproj` with `obj/<subdir>/refint/Lib.dll` holding `intermediate` and, when
/// given, `obj/<subdir>/ref/Lib.dll` holding `published` at an OLDER timestamp.
let private withPair (label: string) (subdir: string) (intermediate: string) (published: string option) body =
    withTempDir label (fun tmpDir ->
        let project = Path.Combine(tmpDir, "Lib", "Lib.fsproj")
        let obj = Path.Combine(tmpDir, "Lib", "obj", subdir)
        let refint = Path.Combine(obj, "refint", "Lib.dll")
        let ref = Path.Combine(obj, "ref", "Lib.dll")
        Directory.CreateDirectory(Path.GetDirectoryName refint) |> ignore
        File.WriteAllText(project, "<Project />")
        File.WriteAllText(refint, intermediate)
        let now = DateTime.UtcNow
        File.SetLastWriteTimeUtc(refint, now)

        match published with
        | Some text ->
            Directory.CreateDirectory(Path.GetDirectoryName ref) |> ignore
            File.WriteAllText(ref, text)
            File.SetLastWriteTimeUtc(ref, now - TimeSpan.FromMinutes 5.0)
        | None -> ()

        body (project, refint, ref))

[<Fact(Timeout = 15000)>]
let ``pairsOf finds refint beside ref under obj/<cfg>/<tfm>`` () =
    withPair "ref-pairs" (Path.Combine("Debug", "net10.0")) "v2" (Some "v1") (fun (project, refint, ref) ->
        test
            <@
                pairsOf project = [ { Project = "Lib"
                                      Intermediate = refint
                                      Published = ref } ]
            @>)

[<Fact(Timeout = 15000)>]
let ``pairsOf finds a runtime-specific pair one level deeper`` () =
    withPair "ref-pairs-rid" (Path.Combine("Debug", "net10.0", "osx-arm64")) "v2" (Some "v1") (fun (project, _, ref) ->
        test <@ pairsOf project |> List.map _.Published = [ ref ] @>)

[<Fact(Timeout = 15000)>]
let ``pairsOf skips an intermediate with no published copy, and a project with no obj`` () =
    withPair "ref-pairs-none" (Path.Combine("Debug", "net10.0")) "v2" None (fun (project, _, _) ->
        test <@ List.isEmpty (pairsOf project) @>
        test <@ List.isEmpty (pairsOf (Path.Combine(Path.GetTempPath(), "nowhere", "Missing.fsproj"))) @>
        test <@ List.isEmpty (pairsOf "Bare.fsproj") @>)

[<Fact(Timeout = 15000)>]
let ``a published copy holding other bytes is stale`` () =
    // Same length: the size is not what decides it.
    withPair "ref-stale" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v1") (fun (project, _, _) ->
        test <@ stalePairs [ project ] |> List.length = 1 @>)

[<Fact(Timeout = 15000)>]
let ``a published copy with the intermediate's bytes at an older timestamp is fresh`` () =
    // What every implementation-only change leaves: `refint` rewritten, the copy skipped.
    withPair "ref-fresh-older" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v2") (fun (project, _, _) ->
        test <@ List.isEmpty (stalePairs [ project ]) @>)

[<Fact(Timeout = 15000)>]
let ``a published copy matching on size and timestamp is fresh without reading it`` () =
    withPair "ref-fresh-stat" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v1") (fun (project, refint, ref) ->
        File.SetLastWriteTimeUtc(ref, File.GetLastWriteTimeUtc refint)
        test <@ List.isEmpty (stalePairs [ project ]) @>)

[<Fact(Timeout = 15000)>]
let ``a pair whose intermediate has gone is not stale`` () =
    withPair "ref-gone" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v1") (fun (project, refint, ref) ->
        File.Delete refint

        test
            <@
                not (
                    isStale
                        { Project = "Lib"
                          Intermediate = refint
                          Published = ref }
                )
            @>

        test <@ List.isEmpty (stalePairs [ project ]) @>)

[<Fact(Timeout = 15000)>]
let ``repair copies the intermediate over the published copy, with its timestamp`` () =
    withPair "ref-repair" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v1") (fun (project, refint, ref) ->
        let stale = stalePairs [ project ]
        test <@ List.isEmpty (repair stale) @>
        test <@ File.ReadAllText ref = "Lib-v2" @>
        test <@ File.GetLastWriteTimeUtc ref = File.GetLastWriteTimeUtc refint @>
        test <@ List.isEmpty (stalePairs [ project ]) @>)

[<Fact(Timeout = 15000)>]
let ``repair reports a pair it could not copy`` () =
    withPair "ref-repair-fail" (Path.Combine("Debug", "net10.0")) "Lib-v2" (Some "Lib-v1") (fun (_, refint, ref) ->
        let pair =
            { Project = "Lib"
              Intermediate = refint
              Published = ref }

        File.Delete refint
        let failed = repair [ pair ]
        test <@ failed |> List.map fst = [ pair ] @>
        test <@ File.ReadAllText ref = "Lib-v1" @>)
