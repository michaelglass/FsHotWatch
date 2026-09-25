/// A traced project launches its woven apphost directly, not through `dotnet run`:
/// the `dotnet run` options are consumed, the app's own arguments kept, and the
/// apphost is given the DOTNET_ROOT that `dotnet run` would have set for it.
module FsHotWatch.Tests.TracedLaunchTests

open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune

// --- appArgs: the app's own arguments from a `dotnet run` line ---

[<Fact>]
let ``a dotnet run line becomes the app's own arguments`` () =
    test
        <@
            TracedLaunch.appArgs
                "dotnet"
                "run --project tests/X --no-build"
                [ "-- --filter-class A B"; "--report-xunit-ctrf --results-directory \"/r d\"" ] = Ok
                [ "--filter-class"
                  "A"
                  "B"
                  "--report-xunit-ctrf"
                  "--results-directory"
                  "/r d" ]
        @>

[<Fact>]
let ``a trailing separator in the config line is dropped`` () =
    test
        <@
            TracedLaunch.appArgs "dotnet" "run --project tests/X --no-build --" [ "--filter-class A" ] = Ok
                [ "--filter-class"; "A" ]
        @>

[<Fact>]
let ``tokens after the separator in the config line are kept, before the extra args`` () =
    test
        <@
            TracedLaunch.appArgs "dotnet" "run --project tests/X --no-build -- --timeout 5m" [ "-- --filter-class A" ] = Ok
                [ "--timeout"; "5m"; "--filter-class"; "A" ]
        @>

[<Fact>]
let ``configuration and framework options are consumed with their values`` () =
    test
        <@
            TracedLaunch.appArgs
                "dotnet"
                "run -c Debug -f net10.0 -p tests/X --no-restore --no-build --configuration Debug --framework net10.0"
                [] = Ok []
        @>

[<Fact>]
let ``an unknown run option refuses the traced launch`` () =
    test
        <@
            TracedLaunch.appArgs "dotnet" "run --project tests/X --launch-profile p" [] = Error
                "unrecognized-run-option:--launch-profile"
        @>

[<Fact>]
let ``an msbuild property refuses the traced launch rather than being guessed at`` () =
    test
        <@
            TracedLaunch.appArgs "dotnet" "run --project tests/X -p:Foo=Bar" [] = Error
                "unrecognized-run-option:-p:Foo=Bar"
        @>

[<Fact>]
let ``an option missing its value refuses the traced launch`` () =
    test <@ TracedLaunch.appArgs "dotnet" "run --project" [] |> Result.isError @>

[<Fact>]
let ``a non-dotnet-run command refuses the traced launch`` () =
    test <@ TracedLaunch.appArgs "./run-tests.sh" "" [] = Error "not-a-dotnet-run-command" @>
    test <@ TracedLaunch.appArgs "dotnet" "test --project tests/X" [] = Error "not-a-dotnet-run-command" @>

[<Fact>]
let ``an unfinished quote refuses the traced launch`` () =
    test <@ TracedLaunch.appArgs "dotnet" "run --project \"tests/X" [] = Error "unparseable-args" @>
    test <@ TracedLaunch.appArgs "dotnet" "run --project tests/X" [ "--filter \"A" ] = Error "unparseable-args" @>

// --- apphostPath: the shadow apphost under bin/Traced/<tfm>/ ---

[<Fact>]
let ``the apphost is the assembly name under bin/Traced/<tfm>`` () =
    let root = Path.Combine("repo", "tests", "X")

    test
        <@
            TracedLaunch.apphostPath false root "net10.0" "X.Tests" = Path.Combine(
                root,
                "bin",
                "Traced",
                "net10.0",
                "X.Tests"
            )
        @>

[<Fact>]
let ``on Windows the apphost carries .exe`` () =
    let root = Path.Combine("repo", "tests", "X")

    test
        <@
            TracedLaunch.apphostPath true root "net10.0" "X.Tests" = Path.Combine(
                root,
                "bin",
                "Traced",
                "net10.0",
                "X.Tests.exe"
            )
        @>

// --- dotnetRoot: what a directly launched apphost needs ---

let private envOf (pairs: (string * string) list) =
    let m = Map.ofList pairs
    fun (key: string) -> Map.tryFind key m

let private runtimeDir =
    Path.Combine("/rt", "dotnet", "shared", "Microsoft.NETCore.App", "10.0.0")

let private rtRoot = Path.GetFullPath(Path.Combine("/rt", "dotnet"))

[<Fact>]
let ``an explicit DOTNET_ROOT wins`` () =
    let env = envOf [ "DOTNET_ROOT", "/explicit"; "DOTNET_HOST_PATH", "/muxer/dotnet" ]
    test <@ TracedLaunch.dotnetRoot env (fun _ -> true) runtimeDir = Some "/explicit" @>

[<Fact>]
let ``without DOTNET_ROOT, the resolved muxer's directory is the root`` () =
    let env = envOf [ "DOTNET_ROOT", ""; "DOTNET_HOST_PATH", "/muxer/dotnet" ]
    test <@ TracedLaunch.dotnetRoot env (fun _ -> true) runtimeDir = Some "/muxer" @>

[<Fact>]
let ``a muxer directory without a runtime falls back to this process's runtime root`` () =
    // A Nix-style wrapper directory has no shared/Microsoft.NETCore.App beside it.
    let env = envOf [ "DOTNET_HOST_PATH", "/wrapper/bin/dotnet" ]
    let hasRuntime (root: string) = root = rtRoot
    test <@ TracedLaunch.dotnetRoot env hasRuntime runtimeDir = Some rtRoot @>

[<Fact>]
let ``with no muxer, this process's runtime root is used`` () =
    test <@ TracedLaunch.dotnetRoot (envOf []) (fun root -> root = rtRoot) runtimeDir = Some rtRoot @>

[<Fact>]
let ``no candidate with a runtime is no root`` () =
    let env = envOf [ "DOTNET_HOST_PATH", "/wrapper/bin/dotnet" ]
    test <@ TracedLaunch.dotnetRoot env (fun _ -> false) runtimeDir = None @>

[<Fact>]
let ``this process resolves a root that holds a runtime`` () =
    let root = TracedLaunch.dotnetRootOfThisProcess ()
    test <@ root.IsSome @>
    test <@ Directory.Exists(Path.Combine(root.Value, "shared", "Microsoft.NETCore.App")) @>

[<Fact>]
let ``a DOTNET_HOST_PATH symlink is read as its final target`` () =
    let dir = Directory.CreateTempSubdirectory("tl-host-").FullName

    try
        let real = Path.Combine(dir, "dotnet")
        File.WriteAllText(real, "")
        let link = Path.Combine(dir, "wrapper")
        File.CreateSymbolicLink(link, real) |> ignore

        let read (key: string) =
            if key = "DOTNET_HOST_PATH" then link else null

        test <@ TracedLaunch.readEnvResolvingHost read "DOTNET_HOST_PATH" = Some(FileInfo(real).FullName) @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a DOTNET_HOST_PATH that is not a link, or is missing, is read as is`` () =
    let dir = Directory.CreateTempSubdirectory("tl-host-").FullName

    try
        let plain = Path.Combine(dir, "dotnet")
        File.WriteAllText(plain, "")
        let missing = Path.Combine(dir, "absent")
        test <@ TracedLaunch.readEnvResolvingHost (fun _ -> plain) "DOTNET_HOST_PATH" = Some plain @>
        test <@ TracedLaunch.readEnvResolvingHost (fun _ -> missing) "DOTNET_HOST_PATH" = Some missing @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``other variables are read as is, and an unset one is None`` () =
    let read (key: string) =
        if key = "DOTNET_ROOT" then "/wrapper/bin" else null

    test <@ TracedLaunch.readEnvResolvingHost read "DOTNET_ROOT" = Some "/wrapper/bin" @>
    test <@ TracedLaunch.readEnvResolvingHost read "DOTNET_HOST_PATH" = None @>
    test <@ TracedLaunch.readEnvResolvingHost (fun _ -> "") "DOTNET_HOST_PATH" = Some "" @>

// --- spec: the whole launch ---

[<Fact>]
let ``the spec launches the apphost with the app arguments and DOTNET_ROOT`` () =
    let spec =
        TracedLaunch.spec
            "/r/tests/X/bin/Traced/net10.0/X"
            "/usr/share/dotnet"
            "dotnet"
            "run --project tests/X --no-build"
            [ "-- --filter-class A" ]
            [ "APP_ENV", "test" ]

    test
        <@
            spec = Ok
                { Command = "/r/tests/X/bin/Traced/net10.0/X"
                  Args = [ "--filter-class"; "A" ]
                  Environment = [ "APP_ENV", "test"; "DOTNET_ROOT", "/usr/share/dotnet" ] }
        @>

[<Fact>]
let ``a project's own DOTNET_ROOT is kept`` () =
    let spec =
        TracedLaunch.spec "/a/X" "/derived" "dotnet" "run --project tests/X" [] [ "DOTNET_ROOT", "/mine" ]

    test <@ spec |> Result.map (fun s -> s.Environment) = Ok [ "DOTNET_ROOT", "/mine" ] @>

[<Fact>]
let ``a spec refusal carries the argument refusal`` () =
    test
        <@
            TracedLaunch.spec "/a/X" "/d" "dotnet" "run --launch-profile p" [] [] = Error
                "unrecognized-run-option:--launch-profile"
        @>

[<Fact>]
let ``the argument line quotes what needs quoting`` () =
    test <@ TracedLaunch.argsLine [ "--results-directory"; "/r d"; "A" ] = "--results-directory \"/r d\" A" @>
