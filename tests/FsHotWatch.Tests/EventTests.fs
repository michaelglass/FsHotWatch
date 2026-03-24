module FsHotWatch.Tests.EventTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Events

[<Fact>]
let ``FileChangeKind constructors work`` () =
    let source = SourceChanged [ "src/Lib.fs" ]
    let proj = ProjectChanged [ "src/Lib.fsproj" ]
    let sln = SolutionChanged
    test <@ match source with SourceChanged files -> files.Length = 1 | _ -> false @>
    test <@ match proj with ProjectChanged _ -> true | _ -> false @>
    test <@ match sln with SolutionChanged -> true | _ -> false @>

[<Fact>]
let ``PluginStatus constructors work`` () =
    let idle = Idle
    let running = Running(since = System.DateTime.UtcNow)
    test <@ match idle with Idle -> true | _ -> false @>
    test <@ match running with Running _ -> true | _ -> false @>
