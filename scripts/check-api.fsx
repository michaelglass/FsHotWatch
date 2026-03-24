#!/usr/bin/env dotnet fsi

/// Compares public API between two versions and reports changes.
/// Used by release.fsx to determine version bump type.
///
/// Usage:
///   dotnet fsi scripts/check-api.fsx                    # Compare HEAD vs latest tag
///   dotnet fsi scripts/check-api.fsx <tag>              # Compare HEAD vs specific tag
///   dotnet fsi scripts/check-api.fsx <old-dll> <new-dll> # Compare two DLLs directly

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

// ============================================================================
// Configuration
// ============================================================================

let fsproj = "src/FsHotWatch/FsHotWatch.fsproj"
let dllPath = "src/FsHotWatch/bin/Release/net10.0/FsHotWatch.dll"
let extractScript = "scripts/extract-api.fsx"

// ============================================================================
// Types
// ============================================================================

type ApiChange =
    | Breaking of removed: string list
    | NonBreaking of added: string list
    | NoChange

type VersionBump =
    | Major
    | Minor
    | Patch

// ============================================================================
// Shell Commands
// ============================================================================

let run (cmd: string) (args: string) =
    let psi = ProcessStartInfo(cmd, args)
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    use p = Process.Start(psi)
    let output = p.StandardOutput.ReadToEnd()
    let error = p.StandardError.ReadToEnd()
    p.WaitForExit()

    if p.ExitCode = 0 then
        Ok(output.Trim())
    else
        Error error

let runOrFail cmd args =
    match run cmd args with
    | Ok output -> output
    | Error e -> failwithf "Command failed: %s %s\n%s" cmd args e

// ============================================================================
// API Extraction
// ============================================================================

let extractApi (dllPath: string) : string list =
    match run "dotnet" (sprintf "fsi %s %s" extractScript dllPath) with
    | Ok output -> output.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList
    | Error e ->
        eprintfn "Warning: Failed to extract API from %s: %s" dllPath e
        []

let extractCurrentApi () : string list =
    runOrFail "dotnet" "build -c Release --verbosity quiet" |> ignore
    extractApi (Path.GetFullPath dllPath)

let extractApiFromTag (tag: string) : string list =
    let tempDir =
        Path.Combine(Path.GetTempPath(), sprintf "api-check-%s" (Guid.NewGuid().ToString("N").[..7]))

    try
        runOrFail "jj" (sprintf "workspace add %s -r %s" tempDir tag) |> ignore
        runOrFail "dotnet" (sprintf "build %s/%s -c Release --verbosity quiet" tempDir fsproj) |> ignore
        let tempDllPath = Path.Combine(tempDir, dllPath)
        let api = extractApi tempDllPath

        match run "jj" (sprintf "workspace forget %s" tempDir) with
        | Ok _ -> ()
        | Error _ -> ()

        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, true)

        api
    with ex ->
        match run "jj" (sprintf "workspace forget %s" tempDir) with
        | Ok _ -> ()
        | Error _ -> ()

        if Directory.Exists(tempDir) then
            try
                Directory.Delete(tempDir, true)
            with _ ->
                ()

        eprintfn "Warning: Failed to extract API from tag %s: %s" tag ex.Message
        []

// ============================================================================
// API Comparison
// ============================================================================

let compareApi (oldApi: string list) (newApi: string list) : ApiChange =
    let oldSet = Set.ofList oldApi
    let newSet = Set.ofList newApi
    let removed = Set.difference oldSet newSet |> Set.toList |> List.sort
    let added = Set.difference newSet oldSet |> Set.toList |> List.sort

    match removed, added with
    | _ :: _, _ -> Breaking removed
    | [], _ :: _ -> NonBreaking added
    | [], [] -> NoChange

let getMajorVersion () =
    let content = File.ReadAllText(fsproj)
    let m = Regex.Match(content, @"<Version>(\d+)\.")
    if m.Success then int m.Groups.[1].Value else 0

let getVersionBump (majorVersion: int) =
    function
    | Breaking _ -> if majorVersion >= 1 then Major else Minor
    | NonBreaking _ -> if majorVersion >= 1 then Minor else Patch
    | NoChange -> Patch

// ============================================================================
// VCS Operations (jj)
// ============================================================================

let getLatestTag () =
    match run "jj" "tag list core-v*" with
    | Ok output when output <> "" ->
        output.Split('\n')
        |> Array.map (fun line -> line.Split(':').[0].Trim())
        |> Array.filter (fun t -> t.StartsWith("core-v"))
        |> Array.sortByDescending (fun t ->
            let v = t.Substring("core-v".Length)
            let parts = v.Split('-')
            let baseParts = parts.[0].Split('.')
            let major = if baseParts.Length > 0 then int baseParts.[0] else 0
            let minor = if baseParts.Length > 1 then int baseParts.[1] else 0
            let patch = if baseParts.Length > 2 then int baseParts.[2] else 0
            (major, minor, patch))
        |> Array.tryHead
    | _ -> None

// ============================================================================
// Output
// ============================================================================

let printChange (change: ApiChange) =
    match change with
    | Breaking removed ->
        printfn "\nREMOVED (breaking):"
        removed |> List.iter (printfn "  - %s")
    | NonBreaking added ->
        printfn "\nADDED:"
        added |> List.iter (printfn "  + %s")
    | NoChange -> printfn "\nNo public API changes."

let printSummary (majorVersion: int) (change: ApiChange) =
    let bump = getVersionBump majorVersion change

    printfn "\n----------------------------------------"

    match bump with
    | Major -> printfn "MAJOR version bump required (breaking changes)"
    | Minor ->
        if majorVersion < 1 then
            printfn "MINOR version bump required (breaking changes, pre-1.0)"
        else
            printfn "MINOR version bump required (new features)"
    | Patch -> printfn "PATCH version bump (no API changes)"

// ============================================================================
// Main
// ============================================================================

let main (argv: string array) =
    try
        let oldApi, newApi, label =
            match argv with
            | [| oldDll; newDll |] when File.Exists(oldDll) && File.Exists(newDll) ->
                // Compare two DLLs directly
                printfn "Comparing %s vs %s..." oldDll newDll
                (extractApi oldDll, extractApi newDll, sprintf "%s -> %s" oldDll newDll)
            | [| tag |] ->
                // Compare specific tag vs HEAD
                printfn "Comparing %s vs HEAD..." tag
                printfn "Extracting API from %s..." tag
                let old = extractApiFromTag tag
                printfn "Building current version..."
                let current = extractCurrentApi ()
                (old, current, sprintf "%s -> HEAD" tag)
            | [||] ->
                // Compare latest tag vs HEAD
                match getLatestTag () with
                | Some tag ->
                    printfn "Comparing %s vs HEAD..." tag
                    printfn "Extracting API from %s..." tag
                    let old = extractApiFromTag tag
                    printfn "Building current version..."
                    let current = extractCurrentApi ()
                    (old, current, sprintf "%s -> HEAD" tag)
                | None ->
                    printfn "No previous tags found. Showing current API."
                    printfn "Building current version..."
                    let current = extractCurrentApi ()
                    ([], current, "initial")
            | _ ->
                eprintfn "Usage:"
                eprintfn "  dotnet fsi scripts/check-api.fsx                     # Compare HEAD vs latest tag"
                eprintfn "  dotnet fsi scripts/check-api.fsx <tag>               # Compare HEAD vs specific tag"
                eprintfn "  dotnet fsi scripts/check-api.fsx <old.dll> <new.dll> # Compare two DLLs"
                exit 1

        printfn "\nOld API: %d signatures" oldApi.Length
        printfn "New API: %d signatures" newApi.Length

        let majorVersion = getMajorVersion ()
        let change = compareApi oldApi newApi
        printChange change
        printSummary majorVersion change

        match getVersionBump majorVersion change with
        | Major -> 2 // Exit code 2 for breaking
        | Minor -> 1 // Exit code 1 for additions
        | Patch -> 0 // Exit code 0 for no changes
    with ex ->
        eprintfn "Error: %s" ex.Message
        3

exit (main (fsi.CommandLineArgs |> Array.skip 1))
