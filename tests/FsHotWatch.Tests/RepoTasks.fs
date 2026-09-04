/// Reads this repo's own `mise.toml` and `.fshw.json` so tests can pin what the
/// task graph promises against what the daemon config demands. Deliberately not a
/// TOML parser: the task file is hand-written, one `[tasks.NAME]` table per task,
/// with `run` (string, triple-quoted block or array) and a single-line `depends`.
module FsHotWatch.Tests.RepoTasks

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// Walk up from the test binary to the directory that holds `mise.toml`.
let repoRoot () =
    let rec up (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "repo root not found: mise.toml is absent from every ancestor"
        elif File.Exists(Path.Combine(directory.FullName, "mise.toml")) then
            directory.FullName
        else
            up directory.Parent

    up (DirectoryInfo(AppContext.BaseDirectory))

let miseToml root =
    File.ReadAllText(Path.Combine(root, "mise.toml"))

/// The `[tasks.NAME]` table, from its header up to (not including) the next table.
let taskBlock (mise: string) taskName =
    let marker = $"[tasks.%s{taskName}]"

    mise.Split('\n')
    |> Array.skipWhile ((<>) marker)
    |> Array.takeWhile (fun line -> line = marker || not (line.StartsWith("[tasks.", StringComparison.Ordinal)))
    |> String.concat "\n"

/// Every task name declared in the file, in file order.
let taskNames (mise: string) =
    Regex.Matches(mise, @"^\[tasks\.([^\]]+)\]", RegexOptions.Multiline)
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.toList

/// The direct `depends = [...]` of a task; empty when the task declares none.
let dependsOf (mise: string) taskName =
    let block = taskBlock mise taskName

    if String.IsNullOrEmpty block then
        failwith $"mise.toml declares no task named %s{taskName}"

    match Regex.Match(block, @"^depends\s*=\s*\[([^\]]*)\]", RegexOptions.Multiline) with
    | m when m.Success ->
        m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> s.Trim().Trim('"'))
        |> Array.toList
    | _ -> []

/// The task plus everything it transitively depends on — the set mise runs before
/// (and including) the named task.
let dependencyClosure (mise: string) taskName =
    let rec walk visited name =
        if Set.contains name visited then
            visited
        else
            dependsOf mise name |> List.fold walk (Set.add name visited)

    walk Set.empty taskName

/// The project paths a task's `run` builds via `dotnet build <path>`, in order.
/// Counts occurrences, so a project built twice appears twice.
let dotnetBuildTargets (mise: string) taskName =
    Regex.Matches(taskBlock mise taskName, @"dotnet build (\S+)")
    |> Seq.map (fun m -> m.Groups[1].Value.TrimEnd('"', '\'', ','))
    |> Seq.toList

/// `analyzers.paths` from the repo's `.fshw.json`, as written (repo-relative).
let configuredAnalyzerPaths root =
    use config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".fshw.json")))

    config.RootElement.GetProperty("analyzers").GetProperty("paths").EnumerateArray()
    |> Seq.map _.GetString()
    |> Seq.toList

/// The one project file whose `bin/<Configuration>/<tfm>/` output an analyzer path
/// names. Fails loudly when the path is not shaped `<projectDir>/bin/...` or the
/// project directory holds anything other than exactly one project file.
let analyzerProjectFor root (analyzerPath: string) =
    let segments =
        analyzerPath.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let projectDir =
        match List.tryFindIndex ((=) "bin") segments with
        | Some binAt when binAt > 0 -> segments |> List.take binAt |> String.concat "/"
        | _ -> failwith $"analyzer path is not a project's bin output: %s{analyzerPath}"

    match Directory.GetFiles(Path.Combine(root, projectDir), "*proj") with
    | [| project |] -> $"%s{projectDir}/%s{Path.GetFileName project}"
    | projects -> failwith $"expected exactly one project under %s{projectDir}, found %d{projects.Length}"
