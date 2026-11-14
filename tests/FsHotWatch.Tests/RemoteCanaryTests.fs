/// scripts/remote-canary.sh runs fssemantictagger's consumer canary on another machine.
/// The tagger packs the release candidate, makes the consumer workspace, rewrites the pin
/// and seeds NuGet's cache on the releaser's machine, then runs the configured gate there.
/// With this script as the gate, the consumer's REAL gate runs on a build server instead:
/// the script ships the candidates and the pin change, and scripts/remote-canary-server.sh
/// makes a fresh workspace at the same base revision, seeds the candidates, runs the gate
/// and tears down there.
///
/// The tagger's contract is that exit 0 is the only pass and anything else refuses the
/// release. Every refusal below is a positive control: a lost connection, a server half
/// that never reported a gate exit, a candidate that cannot be shipped and a gate that is
/// red must each end non-zero, or a green from the remote canary would mean nothing.
/// Every remote command is faked, so these tests pin the scripts' contract, not a server.
module FsHotWatch.Tests.RemoteCanaryTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks
open FsHotWatch.Tests.TestHelpers

let private script (name: string) =
    Path.Combine(repoRoot (), "scripts", name)

let private write (path: string) (text: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, text)

let private executable (path: string) (text: string) =
    write path text
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute)

/// Start `bash <script>` in `cwd` with HOME=`home`, TMPDIR=`home`/tmp and `bin` first on PATH.
/// Standard input stays OPEN until `closeStdinAfter` elapses (never, when None): the server
/// half reads EOF on it as "the releaser is gone".
let private run
    (home: string)
    (cwd: string)
    (scriptPath: string)
    (args: string list)
    (env: (string * string) list)
    (closeStdinAfter: TimeSpan option)
    =
    let start = ProcessStartInfo("bash")
    start.WorkingDirectory <- cwd
    start.UseShellExecute <- false
    start.RedirectStandardInput <- true
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add scriptPath
    args |> List.iter start.ArgumentList.Add
    start.Environment["HOME"] <- home
    start.Environment["TMPDIR"] <- Path.Combine(home, "tmp")
    start.Environment["NUGET_PACKAGES"] <- Path.Combine(home, "nuget")

    start.Environment["PATH"] <- Path.Combine(home, "bin") + ":" + Environment.GetEnvironmentVariable "PATH"

    for k, v in env do
        start.Environment[k] <- v

    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    match closeStdinAfter with
    | Some delay ->
        Threading.Thread.Sleep delay
        child.StandardInput.Close()
    | None -> ()

    let exited = child.WaitForExit(TimeSpan.FromSeconds 60.0)

    if not exited then
        child.Kill true

    test <@ exited @>
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

// ---------------------------------------------------------------- the releaser's half

/// The tagger's state when it runs the gate: a feed with the two candidates of the plan and
/// one unrelated package, the seed project naming the two, a jj consumer workspace whose
/// pin names the candidate, and the machine-local config with the consumer's remote.
let private releaserMachine (home: string) =
    let feed = Path.Combine(home, "feed")
    write (Path.Combine(feed, "Foo.Cli.1.2.0-alpha.3.nupkg")) "cli"
    write (Path.Combine(feed, "Foo.0.9.0-alpha.7.nupkg")) "lib"
    write (Path.Combine(feed, "Foo.Cli.1.2.0-alpha.2.nupkg")) "an older candidate"

    write
        (Path.Combine(home, "tmp", "fssemantictagger-canary-seed", "foo.cli-1.2.0-alpha.3", "seed.csproj"))
        """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageDownload Include="Foo.Cli" Version="[1.2.0-alpha.3]" />
    <PackageDownload Include="Foo" Version="[0.9.0-alpha.7]" />
  </ItemGroup>
</Project>
"""

    let repo = Path.Combine(home, "src", "consumer")
    Directory.CreateDirectory(Path.Combine(repo, ".jj", "repo")) |> ignore
    let ws = Path.Combine(repo, ".workspaces", "canary-foo-cli")
    // What `jj workspace add` writes: the store, relative to the workspace's .jj directory.
    write (Path.Combine(ws, ".jj", "repo")) "../../../.jj/repo"

    write
        (Path.Combine(ws, ".config", "dotnet-tools.json"))
        """{ "version": 1, "tools": { "foo.cli": { "version": "1.2.0-alpha.3", "commands": [ "foo" ] } } }"""

    write
        (Path.Combine(home, ".fssemantictagger.json"))
        $$"""{ "localFeed": "{{feed}}",
  "consumers": [ { "package": "Foo.Cli", "repo": "{{repo}}", "pin": ".config/dotnet-tools.json",
    "gate": "remote-canary.sh Foo.Cli", "timeoutMinutes": 5,
    "remote": { "host": "build-box", "gate": "./build.sh check", "teardown": "./build.sh stop" } } ] }"""

    let bin = Path.Combine(home, "bin")
    let calls = Path.Combine(home, "calls.log")

    executable
        (Path.Combine(bin, "jj"))
        """#!/usr/bin/env bash
case "$1" in
  log) echo 0123456789abcdef0123456789abcdef01234567 ;;
  diff) echo .config/dotnet-tools.json ;;
  *) exit 9 ;;
esac
"""

    // A push copies the staged run directory to $HOME/shipped; a pull plants evidence.
    executable
        (Path.Combine(bin, "rsync"))
        $$"""#!/usr/bin/env bash
echo "rsync $*" >> "{{calls}}"
src=${@: -2:1}; dst=${@: -1}
case "$dst" in
  *:*) mkdir -p "$HOME/shipped" && cp -R "$src" "$HOME/shipped/" ;;
  *) mkdir -p "$dst" && echo '{}' > "$dst/verdict.json" ;;
esac
"""

    // FAKE_REMOTE decides what the server half does: pass, red, lost, silent, liar.
    executable
        (Path.Combine(bin, "ssh"))
        $$"""#!/usr/bin/env bash
echo "ssh $*" >> "{{calls}}"
cmd=${@: -1}
case "$cmd" in mkdir*) exit 0 ;; esac
case "$FAKE_REMOTE" in
  pass) echo "gate output"; echo "remote-canary: exit=0"; exit 0 ;;
  red) echo "remote-canary: FAILED Foo.Tests: a test"; echo "remote-canary: exit=1"; exit 1 ;;
  lost) echo "gate output, then the network went away"; exit 255 ;;
  silent) echo "gate output"; exit 0 ;;
  liar) echo "remote-canary: exit=0"; exit 3 ;;
esac
exit 99
"""

    ws, calls

let private runReleaser (home: string) (ws: string) (remote: string) =
    run home ws (script "remote-canary.sh") [ "Foo.Cli" ] [ "FAKE_REMOTE", remote ] None

let private sshCalls (calls: string) =
    if File.Exists calls then
        File.ReadAllLines calls
        |> Array.filter (fun l -> l.StartsWith "ssh ")
        |> Array.length
    else
        0

[<Fact>]
let ``a green remote gate passes, after shipping exactly the plan's candidates and the pin`` () =
    withTempDir "remote-canary-pass" (fun home ->
        let ws, _ = releaserMachine home
        let code, output = runReleaser home ws "pass"
        test <@ code = 0 @>

        let shipped =
            Directory.GetDirectories(Path.Combine(home, "shipped")) |> Array.exactlyOne

        let feed =
            Directory.GetFiles(Path.Combine(shipped, "feed"))
            |> Array.map Path.GetFileName
            |> Array.sort
            |> Array.toList

        // The older candidate in the same feed is NOT shipped: only what the seed names.
        test <@ feed = [ "Foo.0.9.0-alpha.7.nupkg"; "Foo.Cli.1.2.0-alpha.3.nupkg" ] @>
        test <@ File.Exists(Path.Combine(shipped, "pin.tar")) @>
        test <@ File.Exists(Path.Combine(shipped, "seed", "seed.csproj")) @>
        test <@ File.Exists(Path.Combine(shipped, "server.sh")) @>

        let run = File.ReadAllText(Path.Combine(shipped, "run.json"))
        test <@ run.Contains "0123456789abcdef0123456789abcdef01234567" @>
        test <@ run.Contains "src/consumer/.workspaces/canary-foo-cli" @>
        test <@ run.Contains "./build.sh check" @>
        test <@ output.Contains "build-box" @>)

[<Fact>]
let ``a red remote gate refuses with the gate's exit code and fetches the evidence`` () =
    withTempDir "remote-canary-red" (fun home ->
        let ws, _ = releaserMachine home
        let code, output = runReleaser home ws "red"
        test <@ code = 1 @>
        test <@ output.Contains "FAILED Foo.Tests" @>

        let evidence = Path.Combine(home, ".fssemantictagger", "logs", "remote-canary")
        test <@ Directory.GetFiles(evidence, "verdict.json", SearchOption.AllDirectories).Length = 1 @>)

[<Fact>]
let ``a lost connection refuses`` () =
    withTempDir "remote-canary-lost" (fun home ->
        let ws, _ = releaserMachine home
        let code, output = runReleaser home ws "lost"
        test <@ code <> 0 @>
        test <@ output.Contains "REFUSED" @>)

[<Fact>]
let ``a server half that exits 0 without reporting a gate exit refuses`` () =
    withTempDir "remote-canary-silent" (fun home ->
        let ws, _ = releaserMachine home
        let code, output = runReleaser home ws "silent"
        test <@ code <> 0 @>
        test <@ output.Contains "REFUSED" @>)

[<Fact>]
let ``a reported gate exit that disagrees with ssh's refuses`` () =
    withTempDir "remote-canary-liar" (fun home ->
        let ws, _ = releaserMachine home
        let code, output = runReleaser home ws "liar"
        test <@ code <> 0 @>
        test <@ output.Contains "REFUSED" @>)

[<Fact>]
let ``a candidate missing from the feed refuses before anything reaches the server`` () =
    withTempDir "remote-canary-nofeed" (fun home ->
        let ws, calls = releaserMachine home
        File.Delete(Path.Combine(home, "feed", "Foo.0.9.0-alpha.7.nupkg"))
        let code, output = runReleaser home ws "pass"
        test <@ code = 2 @>
        test <@ output.Contains "Foo 0.9.0-alpha.7" @>
        test <@ sshCalls calls = 0 @>)

[<Fact>]
let ``no seed project for the pinned version refuses before anything reaches the server`` () =
    withTempDir "remote-canary-noseed" (fun home ->
        let ws, calls = releaserMachine home
        Directory.Delete(Path.Combine(home, "tmp", "fssemantictagger-canary-seed"), true)
        let code, _ = runReleaser home ws "pass"
        test <@ code = 2 @>
        test <@ sshCalls calls = 0 @>)

[<Fact>]
let ``a consumer with no remote in the machine config refuses before anything reaches the server`` () =
    withTempDir "remote-canary-noremote" (fun home ->
        let ws, calls = releaserMachine home
        let config = Path.Combine(home, ".fssemantictagger.json")
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"remote\"", "\"elsewhere\""))
        let code, output = runReleaser home ws "pass"
        test <@ code = 2 @>
        test <@ output.Contains "remote" @>
        test <@ sshCalls calls = 0 @>)

// ---------------------------------------------------------------- the server's half

/// What the releaser's half leaves on the server: a run directory with the candidates, the
/// seed project, the pin change and run.json. The consumer repo is a stand-in: `jj` makes
/// the workspace directory, `fakedotnet restore` seeds the cache the way NuGet would.
let private serverMachine (home: string) (gate: string) (teardown: string) =
    let runDir =
        Path.Combine(home, ".fssemantictagger", "remote-canary", "foo.cli-1.2.0-alpha.3-x")

    Directory.CreateDirectory runDir |> ignore
    File.Copy(script "remote-canary-server.sh", Path.Combine(runDir, "server.sh"))
    write (Path.Combine(runDir, "feed", "Foo.Cli.1.2.0-alpha.3.nupkg")) "cli"

    write
        (Path.Combine(runDir, "seed", "seed.csproj"))
        """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
    <PackageDownload Include="Foo.Cli" Version="[1.2.0-alpha.3]" />
</ItemGroup></Project>
"""

    let pinSrc = Path.Combine(home, "pinsrc")
    write (Path.Combine(pinSrc, ".config", "dotnet-tools.json")) "pinned to the candidate"

    let tarStart = ProcessStartInfo("tar")

    for arg in [ "-cf"; Path.Combine(runDir, "pin.tar"); "-C"; pinSrc; ".config" ] do
        tarStart.ArgumentList.Add arg

    use tar = Process.Start tarStart
    tar.WaitForExit()

    let runJson =
        Text.Json.JsonSerializer.Serialize(
            {| package = "Foo.Cli"
               version = "1.2.0-alpha.3"
               ``base`` = "0123456789abcdef"
               repo = "src/consumer"
               workspace = "src/consumer/.workspaces/canary-foo-cli"
               remote =
                {| host = "build-box"
                   dotnet = "fakedotnet"
                   prepare = "touch prepared"
                   wrap = ""
                   gate = gate
                   teardown = teardown |} |}
        )

    write (Path.Combine(runDir, "run.json")) runJson
    Directory.CreateDirectory(Path.Combine(home, "src", "consumer")) |> ignore
    let bin = Path.Combine(home, "bin")

    executable
        (Path.Combine(bin, "jj"))
        """#!/usr/bin/env bash
case "$1 $2" in
  "git fetch") exit 0 ;;
  "workspace forget") exit 0 ;;
  "workspace add") mkdir -p "${@: -1}/.jj" ;;
  *) exit 0 ;;
esac
"""

    // `fakedotnet restore <seed.csproj> --source <feed> --packages <dir>` puts each
    // PackageDownload into <dir>/<id lower>/<version>, as NuGet does.
    executable
        (Path.Combine(bin, "fakedotnet"))
        """#!/usr/bin/env bash
[ "$1" = restore ] || exit 9
proj=$2; pkgs=${@: -1}
sed -n 's/.*Include="\([^"]*\)" Version="\[\([^]]*\)\]".*/\1 \2/p' "$proj" | while read -r id v; do
  mkdir -p "$pkgs/$(echo "$id" | tr A-Z a-z)/$v"
done
"""

    runDir, Path.Combine(home, "src", "consumer", ".workspaces", "canary-foo-cli")

let private runServer (home: string) (runDir: string) (closeStdinAfter: TimeSpan option) =
    run home home (Path.Combine(runDir, "server.sh")) [] [] closeStdinAfter

let private seeded (home: string) =
    Directory.Exists(Path.Combine(home, "nuget", "foo.cli", "1.2.0-alpha.3"))

[<Fact>]
let ``server half: a green gate reports exit 0 after seeding, and evicts the candidates after`` () =
    withTempDir "remote-canary-server-pass" (fun home ->
        let gate =
            "test -d \"$NUGET_PACKAGES/foo.cli/1.2.0-alpha.3\" && test -f prepared && grep -q candidate .config/dotnet-tools.json && touch gate-ran"

        let runDir, ws = serverMachine home gate "touch torn-down"
        let code, output = runServer home runDir None
        test <@ code = 0 @>
        test <@ File.Exists(Path.Combine(ws, "gate-ran")) @>
        test <@ File.Exists(Path.Combine(ws, "torn-down")) @>
        test <@ output.TrimEnd().EndsWith "remote-canary: exit=0" @>
        // The candidate was never published: it must not stay in the server's cache.
        test <@ not (seeded home) @>)

[<Fact>]
let ``server half: a red gate reports its exit code and the failing tests' CTRF receipt`` () =
    withTempDir "remote-canary-server-red" (fun home ->
        let gate =
            "mkdir -p .fshw/test-runs/r1 && printf '%s' '{\"results\":{\"summary\":{\"failed\":1},\"tests\":[{\"name\":\"settings page is fast\",\"status\":\"failed\",\"message\":\"Timeout 45000ms exceeded\"},{\"name\":\"ok\",\"status\":\"passed\"}]}}' > .fshw/test-runs/r1/Foo.Tests.Integration.ctrf.json && exit 1"

        let runDir, _ = serverMachine home gate ""
        let code, output = runServer home runDir None
        test <@ code = 1 @>
        test <@ output.TrimEnd().EndsWith "remote-canary: exit=1" @>
        test <@ output.Contains "settings page is fast" @>
        test <@ output.Contains "Timeout 45000ms exceeded" @>
        test <@ File.Exists(Path.Combine(runDir, "evidence", "r1-Foo.Tests.Integration.ctrf.json")) @>
        test <@ not (seeded home) @>)

[<Fact>]
let ``server half: the releaser going away stops the gate, tears down and evicts`` () =
    withTempDir "remote-canary-server-gone" (fun home ->
        let runDir, ws = serverMachine home "sleep 40; touch finished" "touch torn-down"
        let started = Stopwatch.StartNew()
        let code, _ = runServer home runDir (Some(TimeSpan.FromSeconds 3.0))
        test <@ code <> 0 @>
        test <@ started.Elapsed < TimeSpan.FromSeconds 30.0 @>
        test <@ not (File.Exists(Path.Combine(ws, "finished"))) @>
        test <@ File.Exists(Path.Combine(ws, "torn-down")) @>
        test <@ not (seeded home) @>)

[<Fact>]
let ``server half: a gate slower than the watchdog's poll runs to the end while the releaser is connected`` () =
    // The first remote run stopped a healthy gate after one second: the watchdog read the
    // status of its `if`, not of `read`, so a poll timeout looked like EOF.
    withTempDir "remote-canary-server-slow" (fun home ->
        let runDir, ws = serverMachine home "sleep 4 && touch gate-ran" ""
        let code, output = runServer home runDir None
        test <@ code = 0 @>
        test <@ File.Exists(Path.Combine(ws, "gate-ran")) @>
        test <@ not (output.Contains "connection closed") @>)
