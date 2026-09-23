module FsHotWatch.Bench.Tests.ConfigOverrideTests

open Xunit
open Swensen.Unquote
open FsHotWatch.Bench

let private ok (r: Result<'a, string>) =
    match r with
    | Ok v -> v
    | Error e -> failwith e

[<Fact>]
let ``--set parses a dotted path and a JSON value`` () =
    let s = ok (ConfigOverride.parseSet "checker.cacheSizeFactor=10")
    test <@ s.Path = [ "checker"; "cacheSizeFactor" ] @>
    test <@ s.Json = "10" @>
    test <@ (ok (ConfigOverride.parseSet "format=false")).Json = "false" @>
    test <@ (ok (ConfigOverride.parseSet "build.args=\"build -c Release\"")).Json = "\"build -c Release\"" @>
    // Only the FIRST = splits: a value may contain one.
    test <@ (ok (ConfigOverride.parseSet "a.b=\"x=y\"")).Json = "\"x=y\"" @>

[<Fact>]
let ``a --set that is not path=json is refused, never guessed`` () =
    for bad in
        [ "checker.cacheSizeFactor"
          "=10"
          "checker..x=1"
          "checker.x=ten"
          "checker.x=null"
          "checker.x=" ] do
        test <@ Result.isError (ConfigOverride.parseSet bad) @>

let private config =
    """{"build":{"command":"dotnet","args":"build"},"format":true,"tests":{"projects":[]}}"""

[<Fact>]
let ``set creates intermediate objects and strip runs first`` () =
    let sets =
        [ ok (ConfigOverride.parseSet "checker.cacheSizeFactor=10")
          ok (ConfigOverride.parseSet "build.args=\"build -c Release\"") ]

    let text = ok (ConfigOverride.apply [ "tests" ] sets config)
    let doc = System.Text.Json.Nodes.JsonNode.Parse(text)
    test <@ doc.["checker"].["cacheSizeFactor"].GetValue<int>() = 10 @>
    test <@ doc.["build"].["args"].GetValue<string>() = "build -c Release" @>
    test <@ doc.["build"].["command"].GetValue<string>() = "dotnet" @>
    test <@ isNull doc.["tests"] @>

[<Fact>]
let ``a later set of the same path wins, and a stripped key can be set again`` () =
    let sets =
        [ ok (ConfigOverride.parseSet "checker.cacheSizeFactor=10")
          ok (ConfigOverride.parseSet "checker.cacheSizeFactor=20")
          ok (ConfigOverride.parseSet "format=false") ]

    let doc =
        System.Text.Json.Nodes.JsonNode.Parse(ok (ConfigOverride.apply [ "format" ] sets config))

    test <@ doc.["checker"].["cacheSizeFactor"].GetValue<int>() = 20 @>
    test <@ doc.["format"].GetValue<bool>() = false @>

[<Fact>]
let ``setting through a value that is not an object is refused rather than overwritten`` () =
    let sets = [ ok (ConfigOverride.parseSet "format.x=1") ]
    test <@ Result.isError (ConfigOverride.apply [] sets config) @>

[<Fact>]
let ``the daemon's config echo is read from key=value lines only, in either timestamp shape`` () =
    let window =
        [ "  [config] 2026-09-23T03:51:43.670Z verdictInputs: 5 declared, 6 file(s) folded into the tree hash, 0 absent"
          "  [config] 2026-09-23T03:51:43.700Z checker: cacheSizeFactor=10"
          "  [config] 19:36:07.907 other: a=1 b=two"
          "  [config] 2026-09-23T03:51:44.505Z format: dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)"
          "  [test-prune] 2026-09-23T03:52:00.000Z   |   [config] 03:52:00.000 checker: cacheSizeFactor=99" ]

    test <@ ConfigOverride.echo window = [ "checker.cacheSizeFactor", "10"; "other.a", "1"; "other.b", "two" ] @>

[<Fact>]
let ``an echoed section must echo exactly the value that was set`` () =
    let set = [ ok (ConfigOverride.parseSet "checker.cacheSizeFactor=10") ]
    test <@ List.isEmpty (ConfigOverride.echoProblems set [ "checker.cacheSizeFactor", "10" ]) @>

    test
        <@
            ConfigOverride.echoProblems set [ "checker.cacheSizeFactor", "100" ] = [ "--set checker.cacheSizeFactor=10 but the daemon echoed 100" ]
        @>

    test
        <@
            ConfigOverride.echoProblems set [] = [ "--set checker.cacheSizeFactor=10 but the daemon echoed no checker.cacheSizeFactor" ]
        @>

[<Fact>]
let ``string values compare unquoted, and sections that never echo are recorded but not judged`` () =
    let sets =
        [ ok (ConfigOverride.parseSet "checker.mode=\"fast\"")
          ok (ConfigOverride.parseSet "build.args=\"build\"") ]

    test <@ List.isEmpty (ConfigOverride.echoProblems sets [ "checker.mode", "fast" ]) @>
