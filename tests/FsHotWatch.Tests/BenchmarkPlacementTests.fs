/// A benchmark is a NUMBER; a test is a VERDICT. Neither may pose as the other.
///
/// A throughput bound measured on shared hardware is a function of what else the box is
/// doing, so it can never decide pass/fail: asserted in the unit suite it fails under a
/// landing gate's parallel builds and passes alone, and every such failure costs a full
/// re-gate. The `merkleCacheKey` measurement that used to sit in `TaskCacheTests` with
/// an absolute µs/call ceiling lives in the bench harness (`fshw-bench hash-cost`),
/// which reports it.
///
/// The obvious half-measure — keep the test, tag it `Category=Benchmark`, and filter the
/// trait out of `check` and `confirm` — is refused here on purpose. TestPrune's debt
/// ledger counts a configured project as covering every symbol its tests reach, and a
/// project's run retires that debt. A test that is IN a configured project but filtered
/// out of every run is therefore a test the ledger credits and no run ever executes: the
/// exact "configured-suite green discharged a test that never ran" the ledger was
/// rebuilt to forbid. The only honest positions are "in the suite and asserting" or "out
/// of the suite entirely", so these tests pin the second for anything shaped like a
/// benchmark.
module FsHotWatch.Tests.BenchmarkPlacementTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The test projects a gate runs: every `tests.projects` entry in `.fshw.json` (what
/// `check`/`confirm`/`ci` execute) plus what `test-direct` runs for coverage.
let private gatedTestProjectDirs root =
    use config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".fshw.json")))

    let fromFshw =
        config.RootElement.GetProperty("tests").GetProperty("projects").EnumerateArray()
        |> Seq.map (fun entry ->
            let args = entry.GetProperty("args").GetString()

            match Regex.Match(args, @"--project\s+(\S+)") with
            | m when m.Success -> m.Groups[1].Value
            | _ -> failwith $"cannot find the project in tests.projects args: %s{args}")
        |> Seq.toList

    let fromTestDirect =
        Regex.Matches(commandLines (miseToml root) "test-direct", @"--project\s+(\S+)")
        |> Seq.map (fun m -> Path.GetDirectoryName(m.Groups[1].Value))
        |> Seq.toList

    fromFshw @ fromTestDirect |> List.distinct

/// Source files of a project, skipping its build outputs.
let private sourceFiles root (projectDir: string) =
    Directory.EnumerateFiles(Path.Combine(root, projectDir), "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun path ->
        let relative = Path.GetRelativePath(root, path)

        not (
            relative.Split([| '/'; '\\' |])
            |> Array.exists (fun segment -> segment = "bin" || segment = "obj")
        ))
    |> Seq.toList

/// The two spellings a benchmark posing as a test has worn here: the trait, and the
/// `BENCH` name prefix the retired test used to let a reader filter it by eye.
let private benchmarkShapes =
    [ "a Category=Benchmark trait", Regex(@"Trait\(\s*""Category""\s*,\s*""Benchmark""\s*\)")
      "a BENCH-prefixed test name", Regex(@"^let\s+``BENCH\b", RegexOptions.Multiline) ]

[<Fact>]
let ``no gated test project carries a benchmark`` () =
    let root = repoRoot ()
    let projects = gatedTestProjectDirs root

    // The scan must have something to scan: an empty project list would make the
    // assertion below vacuous.
    test <@ List.contains "tests/FsHotWatch.Tests" projects @>

    let offenders =
        [ for project in projects do
              for file in sourceFiles root project do
                  let source = File.ReadAllText file

                  for (shape, pattern) in benchmarkShapes do
                      if pattern.IsMatch source then
                          yield $"%s{Path.GetRelativePath(root, file)}: %s{shape}" ]

    test <@ List.isEmpty offenders @>

[<Fact>]
let ``the retired hashing benchmark is still runnable, from the bench harness`` () =
    let root = repoRoot ()
    let mise = miseToml root

    // Moved, not deleted: the number is still available, on demand, from a task that no
    // gate depends on.
    test <@ (commandLines mise "bench-hash-cost").Contains "fshw-bench.dll hash-cost" @>

    for gate in [ "check"; "ci" ] do
        test <@ not (Set.contains "bench-hash-cost" (dependencyClosure mise gate)) @>

    test <@ File.Exists(Path.Combine(root, "bench", "FsHotWatch.Bench", "HashCost.fs")) @>
