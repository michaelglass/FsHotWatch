/// Tests for FsHotWatch.DepsFreshness — the deps-freshness gate. Covers the
/// pure freshness comparator, the dep-file enumeration (ancestor walk), and the
/// detect→recover→revalidate orchestration with an injected restore runner so
/// the success / fail-fast / no-loop branches are exercised without shelling out
/// or invoking FCS.
module FsHotWatch.Tests.DepsFreshnessTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.DepsFreshness
open FsHotWatch.ErrorLedger
open FsHotWatch.ProcessHelper
open FsHotWatch.PluginHost
open FsHotWatch.Daemon
open FsHotWatch.Tests.TestHelpers

let private nullChecker =
    Unchecked.defaultof<FSharp.Compiler.CodeAnalysis.FSharpChecker>

// ---- pure comparator ----

[<Fact(Timeout = 5000)>]
let ``compareFreshness: assets newer than all dep files is Fresh`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)

    let deps =
        [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
          DateTime(2026, 6, 2, 11, 0, 0, DateTimeKind.Utc) ]

    test <@ compareFreshness (Some assets) deps = Fresh @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: assets older than one dep file is Stale`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)

    let deps =
        [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
          DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc) ]

    test <@ compareFreshness (Some assets) deps = Stale @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: missing assets is Stale`` () =
    let deps = [ DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) ]
    test <@ compareFreshness None deps = Stale @>

[<Fact(Timeout = 5000)>]
let ``compareFreshness: no dep files is Fresh`` () =
    let assets = DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc)
    test <@ compareFreshness (Some assets) [] = Fresh @>

// ---- dependency-file enumeration ----

let private touch (path: string) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, "x")

[<Fact(Timeout = 5000)>]
let ``dependencyFiles: includes own fsproj and ancestor props + tools`` () =
    withTempDir "deps-enum" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        touch (Path.Combine(root, "Directory.Packages.props"))
        touch (Path.Combine(root, ".config", "dotnet-tools.json"))

        let found = dependencyFiles fsproj root |> List.map Path.GetFileName |> Set.ofList

        test <@ found.Contains "Proj.fsproj" @>
        test <@ found.Contains "Directory.Packages.props" @>
        test <@ found.Contains "dotnet-tools.json" @>)

[<Fact(Timeout = 5000)>]
let ``dependencyFiles: nearest Directory.Build.props wins, no double-count`` () =
    withTempDir "deps-nearest" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        // Two levels both have the file; only the nearest (projDir) should count.
        touch (Path.Combine(root, "Directory.Build.props"))
        touch (Path.Combine(projDir, "Directory.Build.props"))

        let buildProps =
            dependencyFiles fsproj root
            |> List.filter (fun f -> Path.GetFileName f = "Directory.Build.props")

        test <@ buildProps.Length = 1 @>

        test
            <@
                Path.GetFullPath(List.head buildProps) = Path.GetFullPath(
                    Path.Combine(projDir, "Directory.Build.props")
                )
            @>)

// ---- disk-backed probe ----

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: missing assets is Stale`` () =
    withTempDir "deps-probe-missing" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        test <@ detectProjectFreshness root fsproj = Stale @>)

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: assets newer than fsproj is Fresh`` () =
    withTempDir "deps-probe-fresh" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        let assets = assetsPath fsproj
        touch assets
        // Make assets strictly newer than the fsproj.
        File.SetLastWriteTimeUtc(fsproj, DateTime.UtcNow.AddMinutes(-5.0))
        File.SetLastWriteTimeUtc(assets, DateTime.UtcNow)
        test <@ detectProjectFreshness root fsproj = Fresh @>)

[<Fact(Timeout = 5000)>]
let ``detectProjectFreshness: assets older than fsproj is Stale`` () =
    withTempDir "deps-probe-stale" (fun root ->
        let projDir = Path.Combine(root, "src", "Proj")
        let fsproj = Path.Combine(projDir, "Proj.fsproj")
        touch fsproj
        let assets = assetsPath fsproj
        touch assets
        File.SetLastWriteTimeUtc(assets, DateTime.UtcNow.AddMinutes(-5.0))
        File.SetLastWriteTimeUtc(fsproj, DateTime.UtcNow)
        test <@ detectProjectFreshness root fsproj = Stale @>)

// ---- orchestration: evaluateProject ----

let private sigZero (_: string) = 0L
let private assetsYes (_: string) = true
let private assetsNo (_: string) = false
let private succeedingRunner: RestoreRunner = fun _ -> Succeeded "restored"

let private failingRunner: RestoreRunner =
    fun _ -> Failed(1, "NU1101: package not found")

[<Fact(Timeout = 5000)>]
let ``evaluateProject: fresh project proceeds without restore`` () =
    let mutable ran = false

    let runner: RestoreRunner =
        fun _ ->
            ran <- true
            Succeeded ""

    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Fresh) sigZero assetsYes runner tracker "P.fsproj"

    test <@ result = Proceed @>
    test <@ not ran @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: stale then successful restore (assets present) -> RecoveredOk`` () =
    // A successful restore is trusted even if the mtime probe would still read
    // stale (no-op restore doesn't bump assets mtime); only assets *presence*
    // is required post-success.
    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsYes succeedingRunner tracker "P.fsproj"

    test <@ result = RecoveredOk @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: stale and restore fails -> FailFast with one message`` () =
    let tracker = RecoveryTracker()

    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsYes failingRunner tracker "P.fsproj"

    match result with
    | FailFast(msg, detail) ->
        test <@ msg.Contains "stale" @>
        test <@ msg.Contains "P.fsproj" @>
        test <@ detail.Contains "NU1101" @>
    | other -> failwithf "expected FailFast, got %A" other

[<Fact(Timeout = 5000)>]
let ``evaluateProject: restore succeeds but assets still missing -> FailFast`` () =
    let tracker = RecoveryTracker()
    // Restore "succeeds" but assets are still absent on disk → genuine failure.
    let result =
        evaluateProject (fun _ -> Stale) sigZero assetsNo succeedingRunner tracker "P.fsproj"

    match result with
    | FailFast(msg, _) -> test <@ msg.Contains "still missing" @>
    | other -> failwithf "expected FailFast, got %A" other

[<Fact(Timeout = 5000)>]
let ``evaluateProject: still-stale second cycle does not re-run restore (no loop)`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Failed(1, "still broken")

    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    let first = evaluateProject (fun _ -> Stale) sigZero assetsYes runner tracker proj
    let second = evaluateProject (fun _ -> Stale) sigZero assetsYes runner tracker proj

    test <@ runs = 1 @>

    match first with
    | FailFast _ -> ()
    | other -> failwithf "expected FailFast first, got %A" other

    test <@ second = SkipAlreadyAttempted @>

[<Fact(Timeout = 5000)>]
let ``evaluateProject: a new dep bump re-arms recovery`` () =
    let mutable runs = 0

    let runner: RestoreRunner =
        fun _ ->
            runs <- runs + 1
            Failed(1, "broken")

    // Signature changes between the two attempts (a fresh bump).
    let sigs = System.Collections.Generic.Queue<int64>([ 1L; 2L ])
    let signatureOf (_: string) = sigs.Dequeue()

    let tracker = RecoveryTracker()
    let proj = "P.fsproj"

    evaluateProject (fun _ -> Stale) signatureOf assetsYes runner tracker proj
    |> ignore

    evaluateProject (fun _ -> Stale) signatureOf assetsYes runner tracker proj
    |> ignore

    test <@ runs = 2 @>

// ---- daemon-level gate wiring: applyDepsGate ----

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: None gate always proceeds and reports nothing`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proceed = applyDepsGate None host "/r/P.fsproj"

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: Proceed clears any prior deps diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"
    // Seed a stale diagnostic, then a fresh gate result should clear it.
    host.ReportErrors(pluginName, proj, [ ErrorEntry.error "old" ])

    let proceed = applyDepsGate (Some(fun _ -> Proceed)) host proj

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: FailFast reports one error, skips FCS, verdict non-zero`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"

    let proceed =
        applyDepsGate (Some(fun _ -> FailFast("deps stale for P.fsproj", "NU1101"))) host proj

    test <@ not proceed @>

    let byPlugin = host.GetErrorsByPlugin pluginName
    let entries = byPlugin |> Map.tryFind proj |> Option.defaultValue []
    test <@ entries.Length = 1 @>
    test <@ host.HasFailingReasons false @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: SkipAlreadyAttempted skips FCS without adding a new diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proceed = applyDepsGate (Some(fun _ -> SkipAlreadyAttempted)) host "/r/P.fsproj"

    test <@ not proceed @>
    // No new diagnostic is added on this path (a prior one, if any, is retained).
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>

[<Fact(Timeout = 10000)>]
let ``applyDepsGate: RecoveredOk proceeds and clears prior diagnostic`` () =
    let host = PluginHost.create nullChecker "/tmp/test"
    let proj = "/r/P.fsproj"
    host.ReportErrors(pluginName, proj, [ ErrorEntry.error "old" ])

    let proceed = applyDepsGate (Some(fun _ -> RecoveredOk)) host proj

    test <@ proceed @>
    test <@ host.GetErrorsByPlugin pluginName |> Map.isEmpty @>
