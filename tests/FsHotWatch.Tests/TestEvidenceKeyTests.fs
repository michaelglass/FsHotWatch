/// A test project's evidence key must be equal in two checkouts holding the same bytes,
/// and must move when ANY input that can change the outcome moves. A key that is too
/// wide costs a re-run; a key that is too narrow replays a green into a tree that never
/// earned it, so every miss below is pinned as hard as the hit.
module FsHotWatch.Tests.TestEvidenceKeyTests

open System
open System.IO
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.TestEvidenceKey
open FsHotWatch.Tests.TestHelpers

let private write (root: string) (rel: string) (text: string) =
    let path = Path.Combine(root, rel)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private fsproj (refs: string list) =
    let items =
        refs
        |> List.map (fun r -> $"""<ProjectReference Include="%s{r}" />""")
        |> String.concat ""

    $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>%s{items}</ItemGroup></Project>"""

let private assets (version: string) =
    $$"""{ "version": 3, "libraries": { "Newtonsoft.Json/{{version}}": { "sha512": "abc{{version}}", "type": "package" } } }"""

/// A repository with two libraries (A, B), a test project over each (TA, TB), a shared
/// root file, and a file under no project directory at all.
let private populate (root: string) =
    write root "global.json" """{ "sdk": { "version": "10.0.100" } }"""
    write root ".fshw.json" """{ "tests": { "projects": [] } }"""
    write root "src/A/A.fsproj" (fsproj [])
    write root "src/A/Lib.fs" "module A\nlet x = 1\n"
    write root "src/A/data.sql" "select 1;\n"
    write root "src/B/B.fsproj" (fsproj [])
    write root "src/B/Lib.fs" "module B\nlet y = 2\n"
    write root "tests/TA/TA.fsproj" (fsproj [ "../../src/A/A.fsproj" ])
    write root "tests/TA/Tests.fs" "module TA\n"
    write root "tests/TA/obj/project.assets.json" (assets "13.0.1")
    write root "tests/TB/TB.fsproj" (fsproj [ "../../src/B/B.fsproj" ])
    write root "tests/TB/Tests.fs" "module TB\n"
    write root "tests/shared/fixture.txt" "shared fixture\n"
    write root ".env.test" "SECRET=hunter2-sentinel\n"

let private requestFor (root: string) (config: Config) : Request =
    { Project = Path.Combine(root, "tests/TA/TA.fsproj")
      Closure = [ Path.Combine(root, "src/A/A.fsproj") ]
      Config = config
      TestConfig = """{"project":"TA","command":"dotnet"}"""
      Toolchain = "sdk 10.0.100 / fcs 1 / osx-arm64"
      OptionsHash = fun p -> Some("opts:" + Path.GetRelativePath(root, p).Replace('\\', '/')) }

let private keyOf (root: string) (config: Config) (adjust: Request -> Request) =
    let snap = snapshot root []

    match inputsFor snap (adjust (requestFor root config)) with
    | Ok(inputs, _) -> compute inputs
    | Error e -> failwith e

let private closureConfig =
    { defaultConfig with
        Reads = Reads.Closure }

let private withTwin (body: string -> string -> unit) =
    withTempDir "evkey-a" (fun a ->
        withTempDir "evkey-b" (fun b ->
            populate a
            populate b
            body a b))

[<Fact>]
let ``identical trees at two roots give the same key`` () =
    // The positive control: nothing about WHICH checkout the bytes came from may reach
    // the key, or no checkout could ever reuse another's green.
    withTwin (fun a b ->
        let ka = keyOf a defaultConfig id
        let kb = keyOf b defaultConfig id
        test <@ ka.Value = kb.Value @>
        test <@ ka.Labels = kb.Labels @>)

[<Fact>]
let ``one byte in a closure file is a miss that names closure`` () =
    withTwin (fun a b ->
        write b "src/A/Lib.fs" "module A\nlet x = 2\n"
        let ka = keyOf a defaultConfig id
        let kb = keyOf b defaultConfig id
        test <@ ka.Value <> kb.Value @>
        test <@ moved ka.Labels kb.Labels = [ "closure" ] @>)

[<Fact>]
let ``a non-compiled file in a closure directory is in the key`` () =
    // Hashing whole project directories is what covers content items, SQL and embedded
    // resources a Compile list would miss.
    withTwin (fun a b ->
        write b "src/A/data.sql" "select 2;\n"
        test <@ moved (keyOf a defaultConfig id).Labels (keyOf b defaultConfig id).Labels = [ "closure" ] @>)

[<Fact>]
let ``a change in a non-closure project keeps the key under reads closure and misses by default`` () =
    withTwin (fun a b ->
        write b "src/B/Lib.fs" "module B\nlet y = 3\n"
        test <@ (keyOf a closureConfig id).Value = (keyOf b closureConfig id).Value @>
        let ka = keyOf a defaultConfig id
        let kb = keyOf b defaultConfig id
        test <@ ka.Value <> kb.Value @>
        test <@ moved ka.Labels kb.Labels = [ "otherProjects" ] @>)

[<Fact>]
let ``reads mode is itself part of the key`` () =
    // With no project outside the closure, both modes hash the same files; only the
    // stated assumption tells the two claims apart.
    withTwin (fun a _ ->
        Directory.Delete(Path.Combine(a, "src/B"), true)
        Directory.Delete(Path.Combine(a, "tests/TB"), true)
        let closure = keyOf a closureConfig id
        let tree = keyOf a defaultConfig id
        test <@ closure.Value <> tree.Value @>
        test <@ moved closure.Labels tree.Labels = [ "scope" ] @>)

[<Theory>]
[<InlineData("global.json")>]
[<InlineData(".fshw.json")>]
[<InlineData(".config/dotnet-tools.json")>]
[<InlineData("tests/shared/fixture.txt")>]
let ``a change to the shared domain is a miss for every project`` (rel: string) =
    withTwin (fun a b ->
        // dotnet-tools is only in the domain once it exists; give both sides one.
        write a ".config/dotnet-tools.json" """{ "tools": {} }"""
        write b ".config/dotnet-tools.json" """{ "tools": {} }"""
        let before = keyOf b closureConfig id
        write b rel ((File.ReadAllText(Path.Combine(b, rel))) + " ")

        for config in [ closureConfig; defaultConfig ] do
            let ka = keyOf a config id
            let kb = keyOf b config id
            test <@ ka.Value <> kb.Value @>
            test <@ List.contains "shared" (moved ka.Labels kb.Labels) @>

        test <@ before.Value <> (keyOf b closureConfig id).Value @>)

[<Fact>]
let ``a toolchain change is a miss`` () =
    withTwin (fun a b ->
        let ka = keyOf a defaultConfig id

        let kb =
            keyOf b defaultConfig (fun r ->
                { r with
                    Toolchain = r.Toolchain + " / fcs 2" })

        test <@ moved ka.Labels kb.Labels = [ "toolchain" ] @>)

[<Fact>]
let ``a test-config change is a miss`` () =
    withTwin (fun a b ->
        let ka = keyOf a defaultConfig id

        let kb =
            keyOf b defaultConfig (fun r ->
                { r with
                    TestConfig = r.TestConfig + " " })

        test <@ moved ka.Labels kb.Labels = [ "testConfig" ] @>)

[<Fact>]
let ``an options change in a closure project is a miss`` () =
    withTwin (fun a b ->
        let ka = keyOf a defaultConfig id

        let kb =
            keyOf b defaultConfig (fun r ->
                { r with
                    OptionsHash = fun p -> r.OptionsHash p |> Option.map (fun h -> h + "--define:X") })

        test <@ moved ka.Labels kb.Labels = [ "options" ] @>)

[<Fact>]
let ``a project whose options are unknown has no key`` () =
    withTempDir "evkey-unknown" (fun root ->
        populate root

        let request =
            { requestFor root defaultConfig with
                OptionsHash = fun _ -> None }

        test <@ Result.isError (inputsFor (snapshot root []) request) @>)

[<Fact>]
let ``a resolved package version change with the same fsproj is a miss`` () =
    withTwin (fun a b ->
        // obj/ is build output — the walk never sees it — so only the packages input
        // can notice a floating version resolving differently.
        write b "tests/TA/obj/project.assets.json" (assets "13.0.3")
        let ka = keyOf a defaultConfig id
        let kb = keyOf b defaultConfig id
        test <@ moved ka.Labels kb.Labels = [ "packages" ] @>)

[<Fact>]
let ``a file appearing or disappearing in a closure directory is a miss`` () =
    withTwin (fun a b ->
        let original = keyOf a defaultConfig id
        write b "src/A/New.fs" ""
        test <@ moved original.Labels (keyOf b defaultConfig id).Labels = [ "closure" ] @>
        File.Delete(Path.Combine(b, "src/A/New.fs"))
        File.Delete(Path.Combine(b, "src/A/data.sql"))
        test <@ moved original.Labels (keyOf b defaultConfig id).Labels = [ "closure" ] @>)

[<Fact>]
let ``a closure project outside src and tests is still keyed`` () =
    withTwin (fun a b ->
        write a "bench/C/C.fsproj" (fsproj [])
        write b "bench/C/C.fsproj" (fsproj [])
        write a "bench/C/Lib.fs" "1"
        write b "bench/C/Lib.fs" "2"

        let withC (root: string) (r: Request) =
            { r with
                Closure = Path.Combine(root, "bench/C/C.fsproj") :: r.Closure }

        test
            <@ moved (keyOf a closureConfig (withC a)).Labels (keyOf b closureConfig (withC b)).Labels = [ "closure" ] @>)

[<Fact>]
let ``env file content that differs is a miss and only its hash is an input`` () =
    withTwin (fun a b ->
        let config =
            { defaultConfig with
                EnvFiles = [ ".env.test" ] }

        write b ".env.test" "SECRET=other-value\n"
        let ka = keyOf a config id
        let kb = keyOf b config id
        test <@ moved ka.Labels kb.Labels = [ "envFiles" ] @>

        let snap = snapshot a []

        let inputs =
            match inputsFor snap (requestFor a config) with
            | Ok(inputs, _) -> inputs
            | Error e -> failwith e

        let rendered = sprintf "%A" inputs + sprintf "%A" ka
        test <@ not (rendered.Contains "hunter2-sentinel") @>)

[<Fact>]
let ``a declared extra read is keyed and an absent one is a sentinel`` () =
    withTwin (fun a b ->
        write a "tools/data.json" "1"
        write b "tools/data.json" "2"

        let config =
            { closureConfig with
                Extra = [ "tools/data.json"; "tools/missing.json" ] }

        let ka = keyOf a config id
        let kb = keyOf b config id
        test <@ moved ka.Labels kb.Labels = [ "declared" ] @>)

[<Fact>]
let ``an ignored file under a declared extra directory does not move the key and a tracked one does`` () =
    // A directory taken whole is the files the repository tracks there. Build output a
    // publish leaves beside them, or a log a session appends to, is not a test input,
    // and keying it would make every such write a miss.
    withTwin (fun a b ->
        for root in [ a; b ] do
            write root ".gitignore" "infra/publish/\n*.jsonl\n"
            write root "infra/template.yaml" "Resources: {}\n"

        write b "infra/publish/app.dll" "built"
        write b "infra/metrics.jsonl" "{}\n"

        let config =
            { closureConfig with
                Extra = [ "infra" ] }

        let ka = keyOf a config id
        test <@ ka.Value = (keyOf b config id).Value @>

        // Positive control: the tracked file in the same directory is still keyed.
        write b "infra/template.yaml" "Resources: { Bucket: {} }\n"
        let kb = keyOf b config id
        test <@ moved ka.Labels kb.Labels = [ "declared" ] @>)

let private parse (json: string) =
    use doc = JsonDocument.Parse json
    parseConfig (Some(doc.RootElement.Clone()))

[<Fact>]
let ``config parses reads, extra and envFiles and refuses what it cannot honour`` () =
    test <@ parseConfig None = Ok defaultConfig @>

    test
        <@
            parse """{ "reads": "closure", "extra": ["data/**"], "envFiles": [".env.test"] }""" = Ok
                { Reads = Reads.Closure
                  Extra = [ "data/**" ]
                  EnvFiles = [ ".env.test" ] }
        @>

    test <@ Result.isError (parse """{ "reads": "everything" }""") @>
    test <@ Result.isError (parse """{ "extra": "data" }""") @>
    test <@ Result.isError (parse """{ "envFiles": ["../secrets.env"] }""") @>
    test <@ Result.isError (parse "true") @>

[<Fact>]
let ``config refuses non-string items, rooted paths and a reads value that is not a string`` () =
    test <@ Result.isError (parse """{ "extra": [1] }""") @>
    test <@ Result.isError (parse """{ "extra": [""] }""") @>
    test <@ Result.isError (parse """{ "extra": ["/etc/passwd"] }""") @>
    test <@ Result.isError (parse """{ "reads": 1 }""") @>
    test <@ parse """{ "reads": "verdict-tree" }""" = Ok defaultConfig @>

[<Fact>]
let ``configsOf reads every project's block, defaults the rest, and names a bad one`` () =
    withTempDir "evkey-configs" (fun root ->
        test <@ configsOf root = Ok Map.empty @>

        write
            root
            ".fshw.json"
            """{ "tests": { "projects": [ { "project": "A", "evidence": { "reads": "closure" } }, { "project": "B" } ] } }"""

        test
            <@
                configsOf root = Ok(
                    Map.ofList
                        [ "A",
                          { defaultConfig with
                              Reads = Reads.Closure }
                          "B", defaultConfig ]
                )
            @>

        write root ".fshw.json" """{ "tests": { "projects": [ { "project": "A", "evidence": { "reads": "x" } } ] } }"""

        test
            <@
                match configsOf root with
                | Error e -> e.Contains "tests.projects[A].evidence.reads"
                | Ok _ -> false
            @>

        write root ".fshw.json" "{ not json"
        test <@ Result.isError (configsOf root) @>)

[<Fact>]
let ``configsOf answers an unreadable config with an error, never a throw`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evkey-configs-unreadable" (fun root ->
            write root ".fshw.json" """{ "tests": { "projects": [] } }"""
            let path = Path.Combine(root, ".fshw.json")
            File.SetUnixFileMode(path, UnixFileMode.None)

            try
                test <@ Result.isError (configsOf root) @>
            finally
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a restore that lists no libraries, or cannot be parsed, names no packages`` () =
    withTempDir "evkey-assets" (fun root ->
        let project = Path.Combine(root, "P/P.fsproj")
        write root "P/P.fsproj" "<Project />"
        test <@ resolvedPackages project = None @>
        write root "P/obj/project.assets.json" """{ "version": 3 }"""
        test <@ resolvedPackages project = None @>
        write root "P/obj/project.assets.json" "{ not json"
        test <@ resolvedPackages project = None @>
        write root "P/obj/project.assets.json" (assets "1.0.0")
        test <@ Option.isSome (resolvedPackages project) @>)

let private keyResultOf (root: string) (config: Config) =
    inputsFor (snapshot root []) (requestFor root config)

let private rwx =
    UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

[<Fact>]
let ``a directory the walk cannot read makes no key, in the closure or anywhere else`` () =
    // A hole would otherwise hash to one shared constant, and two checkouts holding
    // DIFFERENT unseen bytes would agree on a key.
    if not (OperatingSystem.IsWindows()) then
        for hiddenRel in [ "src/A/hidden"; "tests/shared/hidden"; "src/B/hidden" ] do
            withTempDir "evkey-hole" (fun root ->
                populate root
                let hidden = Path.Combine(root, hiddenRel)
                Directory.CreateDirectory hidden |> ignore
                File.WriteAllText(Path.Combine(hidden, "secret.txt"), "x")
                File.SetUnixFileMode(hidden, UnixFileMode.None)

                try
                    test <@ Result.isError (keyResultOf root defaultConfig) @>
                finally
                    File.SetUnixFileMode(hidden, rwx))

[<Fact>]
let ``a hole in another project's directory does not block a closure-only key`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evkey-hole-other" (fun root ->
            populate root
            let hidden = Path.Combine(root, "src/B/hidden")
            Directory.CreateDirectory hidden |> ignore
            File.SetUnixFileMode(hidden, UnixFileMode.None)

            try
                test <@ Result.isOk (keyResultOf root closureConfig) @>
            finally
                File.SetUnixFileMode(hidden, rwx))

[<Fact>]
let ``a file that cannot be read makes no key`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evkey-unreadable-file" (fun root ->
            populate root
            let path = Path.Combine(root, "src/A/data.sql")
            File.SetUnixFileMode(path, UnixFileMode.None)

            try
                test <@ Result.isError (keyResultOf root defaultConfig) @>
            finally
                File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``a source a closure project links from another project's directory is in its key`` () =
    withTwin (fun a b ->
        let linking =
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="../B/Lib.fs" Link="Lib.fs" /></ItemGroup></Project>"""

        write a "src/A/A.fsproj" linking
        write b "src/A/A.fsproj" linking

        write
            b
            "src/B/Lib.fs"
            "module B
let y = 99
"

        let ka = keyOf a closureConfig id
        let kb = keyOf b closureConfig id
        test <@ moved ka.Labels kb.Labels = [ "closure" ] @>)

[<Fact>]
let ``an item reads closure cannot resolve makes no closure key, and does not block the default`` () =
    withTempDir "evkey-unresolvable" (fun root ->
        populate root

        write
            root
            "src/A/A.fsproj"
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="$(Shared)/Util.fs" /></ItemGroup></Project>"""

        test <@ Result.isError (keyResultOf root closureConfig) @>
        test <@ Result.isOk (keyResultOf root defaultConfig) @>)

[<Fact>]
let ``a project file that is not XML, or a link outside the repository, makes no key`` () =
    withTempDir "evkey-badproj" (fun root ->
        populate root
        write root "src/A/A.fsproj" "<Project"
        test <@ Result.isError (keyResultOf root defaultConfig) @>

        write
            root
            "src/A/A.fsproj"
            """<Project><ItemGroup><None Include="../../../outside.txt" /></ItemGroup></Project>"""

        test <@ Result.isError (keyResultOf root defaultConfig) @>)

[<Fact>]
let ``a declared verdict input that matches nothing is a shared entry of its own`` () =
    withTwin (fun a b ->
        let declare (root: string) (path: string) =
            write
                root
                ".fshw.json"
                $$"""{ "verdictInputs": { "hashed": [ { "path": "{{path}}", "why": "test" } ] }, "tests": { "projects": [] } }"""

        declare a "baselines/one.json"
        declare b "baselines/two.json"
        test <@ moved (keyOf a closureConfig id).Labels (keyOf b closureConfig id).Labels = [ "shared" ] @>)

[<Fact>]
let ``a project at the repository root takes the whole tree as its closure`` () =
    withTwin (fun a b ->
        write a "Root.fsproj" "<Project />"
        write b "Root.fsproj" "<Project />"
        write b "tests/shared/fixture.txt" "changed"

        let atRoot (root: string) (r: Request) =
            { r with
                Closure = Path.Combine(root, "Root.fsproj") :: r.Closure }

        let ka = keyOf a closureConfig (atRoot a)
        let kb = keyOf b closureConfig (atRoot b)
        test <@ moved ka.Labels kb.Labels = [ "closure" ] @>)

[<Fact>]
let ``a closure project outside the repository has no key`` () =
    withTempDir "evkey-outside" (fun root ->
        populate root

        let request =
            { requestFor root defaultConfig with
                Closure = [ Path.Combine(Path.GetDirectoryName root, "elsewhere/E.fsproj") ] }

        test
            <@
                match inputsFor (snapshot root []) request with
                | Error e -> e.Contains "outside the repository"
                | Ok _ -> false
            @>)

[<Fact>]
let ``a declared env file that is absent is an entry of its own`` () =
    withTwin (fun a b ->
        File.Delete(Path.Combine(b, ".env.test"))

        let config =
            { defaultConfig with
                EnvFiles = [ ".env.test" ] }

        test <@ moved (keyOf a config id).Labels (keyOf b config id).Labels = [ "envFiles" ] @>)

[<Fact>]
let ``the toolchain names the checker build and the platform`` () =
    withTempDir "evkey-toolchain" (fun root ->
        let toolchain = toolchainOf root
        test <@ toolchain.Contains FsHotWatch.CheckCache.toolchainIdentity @>

        test
            <@
                toolchain.Contains "os="
                && toolchain.Contains "arch="
                && toolchain.Contains "runtime="
            @>)

[<Fact>]
let ``config refuses an envFiles that is not a list`` () =
    test <@ Result.isError (parse """{ "envFiles": ".env.test" }""") @>

[<Fact>]
let ``configsOf names the first bad block when several are bad`` () =
    withTempDir "evkey-configs-two" (fun root ->
        write
            root
            ".fshw.json"
            """{ "tests": { "projects": [ { "project": "A", "evidence": 1 }, { "project": "B", "evidence": 2 } ] } }"""

        test
            <@
                match configsOf root with
                | Error e -> e.Contains "tests.projects[A]"
                | Ok _ -> false
            @>)

[<Fact>]
let ``a restore whose libraries are not an object names no packages`` () =
    withTempDir "evkey-assets-shape" (fun root ->
        write root "P/P.fsproj" "<Project />"
        write root "P/obj/project.assets.json" """{ "libraries": [] }"""
        test <@ resolvedPackages (Path.Combine(root, "P/P.fsproj")) = None @>)

[<Fact>]
let ``a closure project in the directory above the repository has no key`` () =
    withTempDir "evkey-parent" (fun root ->
        populate root

        let request =
            { requestFor root defaultConfig with
                Closure = [ Path.Combine(Path.GetDirectoryName root, "E.fsproj") ] }

        test <@ Result.isError (inputsFor (snapshot root []) request) @>)

[<Fact>]
let ``a root-level closure project that links a file outside the repository has no key`` () =
    withTempDir "evkey-root-link" (fun root ->
        populate root
        write root "Root.fsproj" """<Project><ItemGroup><None Include="../outside.txt" /></ItemGroup></Project>"""

        let request =
            { requestFor root defaultConfig with
                Closure = [ Path.Combine(root, "Root.fsproj") ] }

        test
            <@
                match inputsFor (snapshot root []) request with
                | Error e -> e.Contains "outside the repository"
                | Ok _ -> false
            @>)

[<Fact>]
let ``an absolute link to another project's file is in the key`` () =
    withTwin (fun a b ->
        let linking (root: string) =
            $"""<Project><ItemGroup><Compile Include="%s{Path.Combine(root, "src/B/Lib.fs")}" /></ItemGroup></Project>"""

        write a "src/A/A.fsproj" (linking a)
        write b "src/A/A.fsproj" (linking b)

        write
            b
            "src/B/Lib.fs"
            "module B
let y = 7
"

        test <@ moved (keyOf a closureConfig id).Labels (keyOf b closureConfig id).Labels = [ "closure" ] @>)

[<Fact>]
let ``an unreadable test project file makes no key`` () =
    if not (OperatingSystem.IsWindows()) then
        withTempDir "evkey-proj-unreadable" (fun root ->
            populate root
            let project = Path.Combine(root, "tests/TA/TA.fsproj")
            File.SetUnixFileMode(project, UnixFileMode.None)

            try
                test <@ Result.isError (keyResultOf root defaultConfig) @>
            finally
                File.SetUnixFileMode(project, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))
