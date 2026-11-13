// Run the package-floor step in the fast landing gate when, and only when, its inputs moved.
//
// `mise run land-check` (compile + lint-project + `fshw check`) did not pack anything; only the
// post-land `mise run ci` ran `check-package-floors.fsx -- pack`. On 2026-11-13 a change was green
// on land-check and broke that pack (NETSDK1085: an Analyzers pack target that built its project
// references), and only the spot check after the land caught it (AUTOMATION-1654). Packing every
// released project costs time a source-only change has no use for, so the step is keyed on its
// inputs: the files changed between main@origin and the change being gated.
//
// usage:
//   dotnet fsi scripts/packaging-inputs-changed.fsx -- gate [--dry-run] [--base <revset>]
//       read `jj diff --from <base> --to @ --name-only` (base: main@origin), then run
//       `dotnet fsi scripts/check-package-floors.fsx -- pack` (exit with its code) or print why not.
//       An unresolvable base RUNS the step: failing safe. --dry-run prints the decision only.
//   dotnet fsi scripts/packaging-inputs-changed.fsx -- classify [--base-unresolved] <path>...
//       the decision alone, for the given repo-relative paths (PackagingInputsGateTests).

open System
open System.Diagnostics
open System.IO

type Decision =
    | Run of reason: string
    | Skip of reason: string

/// The changed files, or why they could not be read.
type ChangedFiles =
    | Changed of paths: string list
    | BaseUnresolved of why: string

/// What `check-package-floors.fsx -- pack` reads: every project file and MSBuild import (a pack
/// target lives in any of them), the release list, the release workflow, the floor check itself,
/// and mise.toml, which defines the pack tasks.
let isPackagingInput (path: string) =
    let path = path.Replace('\\', '/').Trim()
    let name = Path.GetFileName path

    [ ".fsproj"; ".csproj"; ".props"; ".targets" ]
    |> List.exists (fun extension -> name.EndsWith(extension, StringComparison.Ordinal))
    || List.contains name [ "semantic-tagger.json"; "mise.toml" ]
    || List.contains path [ ".github/workflows/release.yml"; "scripts/check-package-floors.fsx" ]

let decide (baseRev: string) changed =
    match changed with
    | BaseUnresolved why -> Run $"%s{baseRev} could not be resolved (%s{why}), so running to fail safe"
    | Changed paths ->
        match
            paths
            |> List.filter (fun path -> path.Trim() <> "")
            |> List.filter isPackagingInput
        with
        | [] -> Skip $"no packaging input changed since %s{baseRev}"
        | inputs -> Run $"""packaging inputs changed since %s{baseRev}: %s{String.Join(", ", inputs)}"""

let describe decision =
    match decision with
    | Run reason -> $"package floors: running, %s{reason}"
    | Skip reason -> $"package floors: skipped, %s{reason}"

let changedSince (baseRev: string) =
    try
        let start = ProcessStartInfo("jj")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true

        [ "diff"; "--from"; baseRev; "--to"; "@"; "--name-only" ]
        |> List.iter start.ArgumentList.Add

        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()
        child.WaitForExit()

        if child.ExitCode = 0 then
            Changed(stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
        else
            let firstLine =
                (stdout.Result + stderr.Result).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryHead
                |> Option.defaultValue "no output"

            BaseUnresolved $"jj exited %d{child.ExitCode}: %s{firstLine.Trim()}"
    with error ->
        BaseUnresolved $"jj could not run: %s{error.Message}"

let packFloors () =
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false

    [ "fsi"; Path.Combine("scripts", "check-package-floors.fsx"); "--"; "pack" ]
    |> List.iter start.ArgumentList.Add

    use child = Process.Start start
    child.WaitForExit()
    child.ExitCode

type Mode =
    | Pack
    | DryRun

/// `gate`'s options: `--dry-run` and `--base <revset>`, in any order.
let rec gateOptions (mode, baseRev) args =
    match args with
    | [] -> Some(mode, baseRev)
    | "--dry-run" :: rest -> gateOptions (DryRun, baseRev) rest
    | "--base" :: revset :: rest -> gateOptions (mode, revset) rest
    | _ -> None

let defaultBase = "main@origin"

let usage () =
    eprintfn
        "usage: dotnet fsi scripts/packaging-inputs-changed.fsx -- gate [--dry-run] [--base <revset>] | classify [--base-unresolved] <path>..."

    2

let exitCode =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.toList with
    | "classify" :: "--base-unresolved" :: _ ->
        printfn "%s" (describe (decide defaultBase (BaseUnresolved "--base-unresolved")))
        0
    | "classify" :: paths ->
        printfn "%s" (describe (decide defaultBase (Changed paths)))
        0
    | "gate" :: options ->
        match gateOptions (Pack, defaultBase) options with
        | None -> usage ()
        | Some(mode, baseRev) ->
            let decision = decide baseRev (changedSince baseRev)
            printfn "%s" (describe decision)

            match decision, mode with
            | Skip _, _
            | Run _, DryRun -> 0
            | Run _, Pack -> packFloors ()
    | _ -> usage ()

exit exitCode
