module FsHotWatch.Tests.SessionFramesTests

open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private optionsNamed (root: string) (name: string) (source: string) =
    let dir = Path.Combine(root, "src", name)
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, $"%s{name}.fs")
    File.WriteAllText(file, source)

    { ProjectFileName = Path.Combine(dir, $"%s{name}.fsproj")
      ProjectId = None
      SourceFiles = [| file |]
      OtherOptions = [||]
      ReferencedProjects = [||]
      IsIncompleteTypeCheckEnvironment = false
      UseScriptResolutionRules = false
      LoadTime = System.DateTime.UtcNow
      UnresolvedReferences = None
      OriginalLoadReferences = []
      Stamp = None }

let private optionsAt (root: string) (source: string) = optionsNamed root "Lib" source

[<Fact>]
let ``sessions with the same project share the virtual root; a differing one does not`` () =
    withTempDir "session-frames" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")

        let choose root =
            SessionFrames.choice registry (SessionFrames.Report()) root virtualRoot ignore

        let optionsA = optionsAt a "module Lib"
        let optionsB = optionsAt b "module Lib"

        test <@ (choose a optionsA "h1" []) |> Option.map (fun f -> f.Virtual) = Some virtualRoot @>
        test <@ (choose b optionsB "h1" []) |> Option.map (fun f -> f.Real) = Some b @>
        test <@ (choose b optionsB "h2" []).IsNone @>)

[<Fact>]
let ``a project that reads its location is checked where it is, and the log says why, once`` () =
    withTempDir "session-frames-excluded" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let logged = ConcurrentQueue<string>()
        let root = Path.Combine(dir, "a")

        let choose =
            SessionFrames.choice registry (SessionFrames.Report()) root (Path.Combine(dir, "virtual")) logged.Enqueue

        let options = optionsAt root "module Lib\nlet here = __SOURCE_DIRECTORY__"

        test <@ (choose options "h1" []).IsNone @>
        test <@ (choose options "h1" []).IsNone @>
        test <@ logged.Count = 1 @>
        let line = Seq.head logged

        test
            <@
                line.StartsWith "src/Lib/Lib.fsproj is checked at its own paths"
                && line.Contains "__SOURCE_DIRECTORY__"
            @>

        // It never took the canonical entry: another session may still share it.
        test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = None @>)

[<Fact>]
let ``the report names the construct a project reads its location with, and the file, relative to the worktree`` () =
    withTempDir "session-frames-report-reads" (fun dir ->
        let report = SessionFrames.Report()
        let root = Path.Combine(dir, "a")

        let choose =
            SessionFrames.choice (CanonicalProjects.Registry()) report root (Path.Combine(dir, "virtual")) ignore

        choose (optionsAt root "module Lib\nlet here = __SOURCE_DIRECTORY__") "h1" []
        |> ignore

        test
            <@
                report.Current = [ "src/Lib/Lib.fsproj",
                                   SessionFrames.ReadsItsLocation("__SOURCE_DIRECTORY__", "src/Lib/Lib.fs") ]
            @>)

[<Fact>]
let ``a project referencing one at its own paths is reported, logged and never claimed, whatever its content`` () =
    // Before this, such a project was checked at its own paths silently: nothing said
    // which upstream kept it there, so a measurement could not tell it from the rest.
    withTempDir "session-frames-upstream" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let report = SessionFrames.Report()
        let logged = ConcurrentQueue<string>()
        let root = Path.Combine(dir, "a")

        let choose =
            SessionFrames.choice registry report root (Path.Combine(dir, "virtual")) logged.Enqueue

        let upstream = optionsNamed root "Up" "module Up\nlet here = __SOURCE_DIRECTORY__"
        let app = optionsNamed root "App" "module App"

        test <@ (choose upstream "u1" []).IsNone @>
        test <@ (choose app "a1" [ upstream.ProjectFileName ]).IsNone @>

        test
            <@
                report.Current = [ "src/App/App.fsproj", SessionFrames.ReferencesOwnPaths [ "src/Up/Up.fsproj" ]
                                   "src/Up/Up.fsproj",
                                   SessionFrames.ReadsItsLocation("__SOURCE_DIRECTORY__", "src/Up/Up.fs") ]
            @>

        test <@ registry.CanonicalHash "src/App/App.fsproj" = None @>

        test
            <@
                logged
                |> Seq.exists (fun line ->
                    line.StartsWith "src/App/App.fsproj is checked at its own paths"
                    && line.Contains "src/Up/Up.fsproj")
            @>)

[<Fact>]
let ``a project whose content differs from another session's is reported naming the session that holds it`` () =
    withTempDir "session-frames-not-canonical" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")
        let reportA = SessionFrames.Report()
        let reportB = SessionFrames.Report()
        let logged = ConcurrentQueue<string>()

        let chooseA = SessionFrames.choice registry reportA a virtualRoot ignore

        let chooseB = SessionFrames.choice registry reportB b virtualRoot logged.Enqueue

        let optionsA = optionsAt a "module Lib"
        let optionsB = optionsAt b "module Lib"

        test <@ (chooseA optionsA "h1" []).IsSome @>
        test <@ (chooseB optionsB "h2" []).IsNone @>

        test <@ List.isEmpty reportA.Current @>
        test <@ reportB.Current = [ "src/Lib/Lib.fsproj", SessionFrames.NotCanonical [ a ] ] @>
        test <@ logged |> Seq.exists (fun line -> line.Contains a) @>

        // Its content becomes the canonical one: shared, and no longer reported.
        test <@ (chooseB optionsB "h1" []).IsSome @>
        test <@ List.isEmpty reportB.Current @>)

[<Fact>]
let ``a host's reports are one per worktree, in JSON, and an ended session's goes`` () =
    withTempDir "session-frames-reports" (fun dir ->
        let reports = SessionFrames.Reports()
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")
        let registry = CanonicalProjects.Registry()
        let virtualRoot = Path.Combine(dir, "virtual")

        let up = optionsNamed a "Up" "module Up\nlet here = __SOURCE_DIRECTORY__"
        let app = optionsNamed a "App" "module App"

        let choose root =
            SessionFrames.choice registry (reports.For root) root virtualRoot ignore

        choose a up "u" [] |> ignore
        choose a app "x" [ up.ProjectFileName ] |> ignore
        choose a (optionsAt a "module Lib") "h1" [] |> ignore
        choose b (optionsAt b "module Lib") "h2" [] |> ignore

        let json = reports.ToJson().ToJsonString()

        let expected =
            $$"""{"{{a}}":[{"project":"src/App/App.fsproj","reason":"references-own-paths","upstreams":["src/Up/Up.fsproj"]},{"project":"src/Up/Up.fsproj","reason":"reads-its-location","construct":"__SOURCE_DIRECTORY__","where":"src/Up/Up.fs"}],"{{b}}":[{"project":"src/Lib/Lib.fsproj","reason":"not-canonical","heldBy":["{{a}}"]}]}"""

        test <@ json = expected @>

        // An ended session's report goes; a session started again at the same root
        // keeps its new report even when the old one's end is told late.
        let first = reports.For a
        let second = reports.Start a
        let restarted = reports.ToJson().[a].AsArray()
        test <@ not (obj.ReferenceEquals(first, second)) && restarted.Count = 0 @>
        reports.End(a, first)
        test <@ reports.ToJson().ContainsKey a @>
        reports.End(a, second)
        test <@ not (reports.ToJson().ContainsKey a) @>
        test <@ reports.ToJson().ContainsKey b @>)

// AUTOMATION-681 slice 4 (ADR-046): a frame choice made through an ended incarnation's
// lease checks at its own paths and leaves no holder behind.
[<Fact>]
let ``a choice made after its session's lease was released claims nothing`` () =
    withTempDir "session-frames-retired" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let root = Path.Combine(dir, "a")
        let lease = registry.Open root

        let choose =
            SessionFrames.choiceFor registry lease (SessionFrames.Report()) root (Path.Combine(dir, "virtual")) ignore

        let options = optionsAt root "module Lib"
        test <@ (choose options "h1" []).IsSome @>
        registry.Release lease
        test <@ (choose options "h2" []).IsNone @>
        test <@ List.isEmpty (registry.HoldersOf "src/Lib/Lib.fsproj") @>)
