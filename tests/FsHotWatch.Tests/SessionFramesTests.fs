module FsHotWatch.Tests.SessionFramesTests

open System.Collections.Concurrent
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

let private optionsAt (root: string) (source: string) =
    let dir = Path.Combine(root, "src", "Lib")
    Directory.CreateDirectory dir |> ignore
    let file = Path.Combine(dir, "Lib.fs")
    File.WriteAllText(file, source)

    { ProjectFileName = Path.Combine(dir, "Lib.fsproj")
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

[<Fact>]
let ``sessions with the same project share the virtual root; a differing one does not`` () =
    withTempDir "session-frames" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let virtualRoot = Path.Combine(dir, "virtual")
        let a = Path.Combine(dir, "a")
        let b = Path.Combine(dir, "b")

        let choose root =
            SessionFrames.choice registry root virtualRoot ignore

        let optionsA = optionsAt a "module Lib"
        let optionsB = optionsAt b "module Lib"

        test <@ (choose a optionsA "h1") |> Option.map (fun f -> f.Virtual) = Some virtualRoot @>
        test <@ (choose b optionsB "h1") |> Option.map (fun f -> f.Real) = Some b @>
        test <@ (choose b optionsB "h2").IsNone @>)

[<Fact>]
let ``a project that reads its location is checked where it is, and the log says why, once`` () =
    withTempDir "session-frames-excluded" (fun dir ->
        let registry = CanonicalProjects.Registry()
        let logged = ConcurrentQueue<string>()
        let root = Path.Combine(dir, "a")

        let choose =
            SessionFrames.choice registry root (Path.Combine(dir, "virtual")) logged.Enqueue

        let options = optionsAt root "module Lib\nlet here = __SOURCE_DIRECTORY__"

        test <@ (choose options "h1").IsNone @>
        test <@ (choose options "h1").IsNone @>
        test <@ logged.Count = 1 @>
        let line = Seq.head logged

        test
            <@
                line.StartsWith "src/Lib/Lib.fsproj is checked at its own paths"
                && line.Contains "__SOURCE_DIRECTORY__"
            @>

        // It never took the canonical entry: another session may still share it.
        test <@ registry.CanonicalHash "src/Lib/Lib.fsproj" = None @>)
