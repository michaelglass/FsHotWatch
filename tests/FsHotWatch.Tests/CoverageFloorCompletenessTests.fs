/// Every source file has an explicit coverage floor on every platform CI grades.
///
/// `coverageratchet check` fails when a FLOORED file has no row in the report, because
/// the set it is obliged to measure comes from the floors file, not from the report.
/// A file with no entry leaves no name to look for: the 100%/100% default still grades
/// it when it appears, but a report that loses it passes. That is how a gate once graded
/// 113 of 134 files, every FsHotWatch.Cli file absent, and still passed. So the floors
/// file has to name every file the report should contain, on each platform (an entry
/// with no `platform` applies to both). A file the coverage reader can never see, because
/// it holds no executable line, is exempt here with the reason written down instead.
module FsHotWatch.Tests.CoverageFloorCompletenessTests

open System
open System.IO
open System.Text.Json
open Xunit
open FsHotWatch.Tests.RepoTasks

/// The platforms CI measures coverage on; `platform` values in the floors file.
let private platforms = [ "macos"; "linux" ]

/// Source files with no floor, and why. A floor on a file that never appears in the
/// report makes `coverageratchet check` exit 2, so these cannot carry one.
let private exemptions =
    Map.ofList
        [ "Plugin.fs",
          "declares only a function type alias, a record and an interface: no executable line, so the cobertura report has no row for it on either platform. Measured 2026-10-17: absent from the macOS unit-suite report (mise run test-direct, 138 files) and from Linux CI run 39059935111's 138-file report" ]

/// Every `src/**/*.fs` outside build output, as a path relative to the repo root.
let private sourceFiles root =
    let src = Path.Combine(root, "src")

    Directory.EnumerateFiles(src, "*.fs", SearchOption.AllDirectories)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
    |> Seq.filter (fun path ->
        let segments = path.Split('/')
        not (Array.contains "obj" segments || Array.contains "bin" segments))
    |> Seq.sort
    |> Seq.toList

/// (file name, platform) for every floor in `overrides` and `countFloors`. A section's
/// value is one entry or a list of them; an entry without `platform` covers every one.
let private flooredOn root =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "coverage-ratchet-FsHotWatch.json")))

    let entries (value: JsonElement) =
        if value.ValueKind = JsonValueKind.Array then
            value.EnumerateArray() |> Seq.toList
        else
            [ value ]

    let platformsOf (entry: JsonElement) =
        match entry.TryGetProperty "platform" with
        | true, platform -> [ platform.GetString() ]
        | false, _ -> platforms

    [ for section in [ "overrides"; "countFloors" ] do
          match config.RootElement.TryGetProperty section with
          | true, files ->
              for file in files.EnumerateObject() do
                  for entry in entries file.Value do
                      for platform in platformsOf entry do
                          yield file.Name, platform
          | false, _ -> () ]
    |> Set.ofList

[<Fact>]
let ``every source file has a coverage floor on every platform, or a written exemption`` () =
    let root = repoRoot ()
    let floored = flooredOn root

    let gaps =
        [ for path in sourceFiles root do
              let name = Path.GetFileName path

              if not (exemptions.ContainsKey name) then
                  let missing = platforms |> List.filter (fun p -> not (floored.Contains(name, p)))

                  if not (List.isEmpty missing) then
                      yield $"""%s{path} (%s{String.Join(", ", missing)})""" ]

    Assert.True(
        List.isEmpty gaps,
        $"%d{gaps.Length} source file(s) have no floor in coverage-ratchet-FsHotWatch.json for the platform(s) named, so a report that drops them still passes. Add a floor at the measured value, or an exemption with its reason:\n"
        + String.Join("\n", gaps)
    )

[<Fact>]
let ``every exemption names a source file that has no floor`` () =
    let root = repoRoot ()
    let names = sourceFiles root |> List.map Path.GetFileName |> Set.ofList
    let floored = flooredOn root |> Set.map fst

    let stale =
        exemptions
        |> Map.keys
        |> Seq.filter (fun name -> not (names.Contains name) || floored.Contains name)
        |> Seq.toList

    Assert.True(List.isEmpty stale, "stale exemptions: " + String.Join(", ", stale))

/// Floors are keyed by file NAME, so two source files sharing one would share a floor,
/// and the report could drop either without the floors file noticing.
[<Fact>]
let ``no two source files share a name`` () =
    let shared =
        sourceFiles (repoRoot ())
        |> List.groupBy Path.GetFileName
        |> List.filter (fun (_, paths) -> paths.Length > 1)
        |> List.collect snd

    Assert.True(List.isEmpty shared, "source files sharing a name: " + String.Join(", ", shared))
