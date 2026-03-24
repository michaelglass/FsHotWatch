#!/usr/bin/env dotnet fsi

/// Release script with automatic semantic versioning based on API changes.
/// Detects which packages have API changes and releases them independently.
///
/// Usage:
///   dotnet fsi scripts/release.fsx              # auto-detect changed packages
///   dotnet fsi scripts/release.fsx alpha         # start first alpha for changed packages
///   dotnet fsi scripts/release.fsx --publish     # publish locally instead of pushing

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions

// ============================================================================
// Configuration
// ============================================================================

type PackageConfig =
    { Name: string
      Fsproj: string
      DllPath: string
      TagPrefix: string
      /// Additional fsproj files that should get the same version (e.g. CLI follows Core).
      ExtraFsprojs: string list }

let allPackages =
    [ { Name = "FsHotWatch"
        Fsproj = "src/FsHotWatch/FsHotWatch.fsproj"
        DllPath = "src/FsHotWatch/bin/Release/net10.0/FsHotWatch.dll"
        TagPrefix = "core-v"
        ExtraFsprojs = [ "src/FsHotWatch.Cli/FsHotWatch.Cli.fsproj" ] }
      { Name = "FsHotWatch.TestPrune"
        Fsproj = "src/FsHotWatch.TestPrune/FsHotWatch.TestPrune.fsproj"
        DllPath = "src/FsHotWatch.TestPrune/bin/Release/net10.0/FsHotWatch.TestPrune.dll"
        TagPrefix = "testprune-v"
        ExtraFsprojs = [] }
      { Name = "FsHotWatch.Analyzers"
        Fsproj = "src/FsHotWatch.Analyzers/FsHotWatch.Analyzers.fsproj"
        DllPath = "src/FsHotWatch.Analyzers/bin/Release/net10.0/FsHotWatch.Analyzers.dll"
        TagPrefix = "analyzers-v"
        ExtraFsprojs = [] }
      { Name = "FsHotWatch.Lint"
        Fsproj = "src/FsHotWatch.Lint/FsHotWatch.Lint.fsproj"
        DllPath = "src/FsHotWatch.Lint/bin/Release/net10.0/FsHotWatch.Lint.dll"
        TagPrefix = "lint-v"
        ExtraFsprojs = [] }
      { Name = "FsHotWatch.Fantomas"
        Fsproj = "src/FsHotWatch.Fantomas/FsHotWatch.Fantomas.fsproj"
        DllPath = "src/FsHotWatch.Fantomas/bin/Release/net10.0/FsHotWatch.Fantomas.dll"
        TagPrefix = "fantomas-v"
        ExtraFsprojs = [] } ]

let repoUrl = "https://github.com/michaelglass/FsHotWatch"

/// Versions that were accidentally published to NuGet and cannot be reused.
/// The script will skip these and bump to the next patch version.
let reservedVersions = set []

// ============================================================================
// Domain Types
// ============================================================================

type PreRelease =
    | Alpha of int
    | Beta of int
    | RC of int

type VersionStage =
    | PreRelease of PreRelease
    | Stable

type Version =
    { Major: int
      Minor: int
      Patch: int
      Stage: VersionStage }

type ApiSignature = ApiSignature of string

type ApiChange =
    | Breaking of removed: ApiSignature list
    | Addition of added: ApiSignature list
    | NoChange

type ReleaseCommand =
    | Auto
    | StartAlpha
    | PromoteToBeta
    | PromoteToRC
    | PromoteToStable
    | ShowHelp

type PublishMode =
    | GitHubActions
    | LocalPublish

type ReleaseState =
    | FirstRelease
    | HasPreviousRelease of tag: string * currentVersion: Version

type CommandResult<'a> =
    | Success of 'a
    | Failure of string

// ============================================================================
// Shell Commands
// ============================================================================

module Shell =
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
            Success(output.Trim())
        else
            Failure error

    let runOrFail cmd args =
        match run cmd args with
        | Success output -> output
        | Failure error -> failwithf "Command failed: %s %s\n%s" cmd args error

    let runSilent cmd args =
        match run cmd args with
        | Success output -> Some output
        | Failure _ -> None

// ============================================================================
// Version Parsing and Formatting
// ============================================================================

module Version =
    let parse (version: string) : Version =
        let v = version.TrimStart('v')
        let parts = v.Split('-')
        let baseParts = parts.[0].Split('.')
        let major = if baseParts.Length > 0 then int baseParts.[0] else 0
        let minor = if baseParts.Length > 1 then int baseParts.[1] else 0
        let patch = if baseParts.Length > 2 then int baseParts.[2] else 0

        let stage =
            if parts.Length > 1 then
                let pre = parts.[1]
                let numMatch = Regex.Match(pre, @"(\d+)$")

                let num =
                    if numMatch.Success then
                        int numMatch.Groups.[1].Value
                    else
                        1

                if pre.StartsWith("alpha") then PreRelease(Alpha num)
                elif pre.StartsWith("beta") then PreRelease(Beta num)
                elif pre.StartsWith("rc") then PreRelease(RC num)
                else Stable
            else
                Stable

        { Major = major
          Minor = minor
          Patch = patch
          Stage = stage }

    let format (v: Version) : string =
        let base' = sprintf "%d.%d.%d" v.Major v.Minor v.Patch

        match v.Stage with
        | PreRelease(Alpha n) -> sprintf "%s-alpha.%d" base' n
        | PreRelease(Beta n) -> sprintf "%s-beta.%d" base' n
        | PreRelease(RC n) -> sprintf "%s-rc.%d" base' n
        | Stable -> base'

    let toTag (pkg: PackageConfig) (v: Version) : string = sprintf "%s%s" pkg.TagPrefix (format v)

    let firstAlpha =
        { Major = 0
          Minor = 1
          Patch = 0
          Stage = PreRelease(Alpha 1) }

    let bumpPreRelease =
        function
        | Alpha n -> Alpha(n + 1)
        | Beta n -> Beta(n + 1)
        | RC n -> RC(n + 1)

    let nextAlphaCycle v =
        { v with
            Minor = v.Minor + 1
            Patch = 0
            Stage = PreRelease(Alpha 1) }

    let toBeta v = { v with Stage = PreRelease(Beta 1) }
    let toRC v = { v with Stage = PreRelease(RC 1) }
    let toStable v = { v with Stage = Stable }

    let bumpPatch v =
        { v with
            Patch = v.Patch + 1
            Stage = Stable }

    let bumpMinor v =
        { v with
            Minor = v.Minor + 1
            Patch = 0
            Stage = Stable }

    let bumpMajor v =
        { Major = v.Major + 1
          Minor = 0
          Patch = 0
          Stage = Stable }

    let sortKey (v: Version) =
        let stageOrder, stageNum =
            match v.Stage with
            | PreRelease(Alpha n) -> 0, n
            | PreRelease(Beta n) -> 1, n
            | PreRelease(RC n) -> 2, n
            | Stable -> 3, 0

        (v.Major, v.Minor, v.Patch, stageOrder, stageNum)

// ============================================================================
// API Extraction and Comparison
// ============================================================================

module Api =
    let private extractApiScript = "scripts/extract-api.fsx"

    let private extractFromDll (dllPath: string) : ApiSignature list =
        match Shell.run "dotnet" (sprintf "fsi %s %s" extractApiScript dllPath) with
        | Success output ->
            output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map ApiSignature
            |> Array.toList
        | Failure error ->
            printfn "Warning: Failed to extract API from %s: %s" dllPath error
            []

    let extractCurrent (pkg: PackageConfig) : ApiSignature list =
        Shell.runOrFail "dotnet" "build -c Release --verbosity quiet" |> ignore
        extractFromDll (Path.GetFullPath pkg.DllPath)

    let extractFromTag (pkg: PackageConfig) (tag: string) : ApiSignature list =
        let tempDir =
            Path.Combine(Path.GetTempPath(), sprintf "api-check-%s" (Guid.NewGuid().ToString("N").[..7]))

        try
            Shell.runOrFail "jj" (sprintf "workspace add %s -r %s" tempDir tag) |> ignore

            Shell.runOrFail "dotnet" (sprintf "build %s/%s -c Release --verbosity quiet" tempDir pkg.Fsproj)
            |> ignore

            let tempDllPath = Path.Combine(tempDir, pkg.DllPath)
            let api = extractFromDll tempDllPath

            Shell.runSilent "jj" (sprintf "workspace forget %s" tempDir) |> ignore

            if Directory.Exists(tempDir) then
                Directory.Delete(tempDir, true)

            api
        with ex ->
            Shell.runSilent "jj" (sprintf "workspace forget %s" tempDir) |> ignore

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with _ ->
                    ()

            printfn "Warning: Failed to extract API from tag %s: %s" tag ex.Message
            []

    let compare (baseline: ApiSignature list) (current: ApiSignature list) : ApiChange =
        let baselineSet = Set.ofList baseline
        let currentSet = Set.ofList current
        let removed = Set.difference baselineSet currentSet |> Set.toList
        let added = Set.difference currentSet baselineSet |> Set.toList

        match removed, added with
        | _ :: _, _ -> Breaking removed
        | [], _ :: _ -> Addition added
        | [], [] -> NoChange

// ============================================================================
// Version Bump Logic
// ============================================================================

module Bump =
    type BumpResult = { NewVersion: Version; Reason: string }

    let private isApiChanged =
        function
        | Breaking _
        | Addition _ -> true
        | NoChange -> false

    let fromApiChange (current: Version) (change: ApiChange) : BumpResult =
        match current.Stage with
        | PreRelease(RC _) when isApiChanged change ->
            { NewVersion = Version.toBeta current
              Reason = "back to beta (API changed in RC)" }
        | PreRelease pre ->
            { NewVersion =
                { current with
                    Stage = PreRelease(Version.bumpPreRelease pre) }
              Reason =
                match pre with
                | Alpha _ -> "alpha"
                | Beta _ -> "beta"
                | RC _ -> "rc" }
        | Stable when current.Major >= 1 ->
            match change with
            | Breaking _ ->
                { NewVersion = Version.bumpMajor current
                  Reason = "MAJOR (breaking change)" }
            | Addition _ ->
                { NewVersion = Version.bumpMinor current
                  Reason = "MINOR (new API)" }
            | NoChange ->
                { NewVersion = Version.bumpPatch current
                  Reason = "PATCH (no API changes)" }
        | Stable ->
            match change with
            | Breaking _ ->
                { NewVersion = Version.bumpMinor current
                  Reason = "MINOR (breaking change, pre-1.0)" }
            | Addition _ ->
                { NewVersion = Version.bumpPatch current
                  Reason = "PATCH (new API, pre-1.0)" }
            | NoChange ->
                { NewVersion = Version.bumpPatch current
                  Reason = "PATCH (no API changes)" }

    let private bumpPreRelease (v: Version) (pre: PreRelease) : BumpResult =
        { NewVersion =
            { v with
                Stage = PreRelease(Version.bumpPreRelease pre) }
          Reason =
            match pre with
            | Alpha n -> sprintf "alpha.%d (API changes ignored in alpha)" (n + 1)
            | Beta n -> sprintf "beta.%d (API changes ignored in beta)" (n + 1)
            | RC n -> sprintf "rc.%d" (n + 1) }

    let forCommand (state: ReleaseState) (cmd: ReleaseCommand) : BumpResult option =
        match cmd, state with
        | ShowHelp, _ -> None
        | StartAlpha, FirstRelease ->
            Some
                { NewVersion = Version.firstAlpha
                  Reason = "first alpha release" }
        | StartAlpha, HasPreviousRelease(_, v) ->
            Some
                { NewVersion = Version.nextAlphaCycle v
                  Reason = "starting new alpha cycle" }
        | PromoteToBeta, HasPreviousRelease(_, v) ->
            Some
                { NewVersion = Version.toBeta v
                  Reason = "promoting to beta" }
        | PromoteToRC, HasPreviousRelease(_, v) ->
            Some
                { NewVersion = Version.toRC v
                  Reason = "promoting to release candidate" }
        | PromoteToStable, HasPreviousRelease(_, v) ->
            Some
                { NewVersion = Version.toStable v
                  Reason = "promoting to stable" }
        | Auto, FirstRelease -> None
        | Auto, HasPreviousRelease(_, v) ->
            match v.Stage with
            | PreRelease(Alpha _ as pre)
            | PreRelease(Beta _ as pre) -> Some(bumpPreRelease v pre)
            | PreRelease(RC _)
            | Stable -> None
        | _, FirstRelease -> None

// ============================================================================
// Version Control Operations (jj only)
// ============================================================================

module VCS =
    let hasUncommittedChanges () =
        match Shell.run "jj" "status" with
        | Success output -> not (output.Contains("The working copy has no changes"))
        | Failure _ -> true

    let tagExists tag =
        match Shell.run "jj" (sprintf "tag list %s" tag) with
        | Success output -> output.Contains(tag)
        | Failure _ -> false

    let getLatestTag (pkg: PackageConfig) =
        match Shell.run "jj" (sprintf "tag list %s*" pkg.TagPrefix) with
        | Success output when output <> "" ->
            output.Split('\n')
            |> Array.map (fun line -> line.Split(':').[0].Trim())
            |> Array.filter (fun t -> t.StartsWith(pkg.TagPrefix))
            |> Array.sortByDescending (fun t -> Version.sortKey (Version.parse (t.Substring(pkg.TagPrefix.Length))))
            |> Array.tryHead
        | _ -> None

    let getReleaseState (pkg: PackageConfig) : ReleaseState =
        match getLatestTag pkg with
        | Some tag -> HasPreviousRelease(tag, Version.parse (tag.Substring(pkg.TagPrefix.Length)))
        | None -> FirstRelease

    let commitAndTag (pkg: PackageConfig) (version: Version) =
        let versionStr = Version.format version
        let tag = Version.toTag pkg version

        Shell.runOrFail "jj" (sprintf "commit -m \"Release %s %s\"" pkg.Name versionStr)
        |> ignore

        Shell.runOrFail "jj" "bookmark set main -r @-" |> ignore
        Shell.runOrFail "jj" (sprintf "tag set %s -r @-" tag) |> ignore
        tag

    let private gitDir () =
        let root = Shell.runOrFail "jj" "root"
        Path.Combine(root, ".jj/repo/store/git")

    let pushTags (tags: string list) =
        Shell.runOrFail "jj" "git push --all" |> ignore
        Shell.runOrFail "jj" "git export" |> ignore
        let gd = gitDir ()

        for tag in tags do
            Shell.runOrFail "git" (sprintf "--git-dir=%s push origin %s" gd tag) |> ignore

// ============================================================================
// NuGet Operations
// ============================================================================

module NuGet =
    let artifactsDir = "artifacts"

    let pack (pkg: PackageConfig) =
        if Directory.Exists(artifactsDir) then
            Directory.Delete(artifactsDir, true)

        Directory.CreateDirectory(artifactsDir) |> ignore

        Shell.runOrFail "dotnet" (sprintf "pack %s -c Release -o %s" pkg.Fsproj artifactsDir)
        |> ignore

        Directory.GetFiles(artifactsDir, "*.nupkg")
        |> Array.tryHead
        |> Option.defaultWith (fun () -> failwith "No .nupkg file found after pack")

    let publish (nupkgPath: string) =
        let apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY") |> Option.ofObj

        let pushArgs =
            match apiKey with
            | Some key ->
                sprintf
                    "nuget push %s --api-key %s --source https://api.nuget.org/v3/index.json --skip-duplicate"
                    nupkgPath
                    key
            | None ->
                printfn "No NUGET_API_KEY found, trying with stored credentials..."
                sprintf "nuget push %s --source https://api.nuget.org/v3/index.json --skip-duplicate" nupkgPath

        Shell.runOrFail "dotnet" pushArgs |> ignore

module Project =
    let private updateFsprojVersion (fsproj: string) (version: Version) =
        let content = File.ReadAllText(fsproj)

        let newContent =
            Regex.Replace(content, @"<Version>.*</Version>", sprintf "<Version>%s</Version>" (Version.format version))

        File.WriteAllText(fsproj, newContent)

    let updateVersion (pkg: PackageConfig) (version: Version) =
        updateFsprojVersion pkg.Fsproj version

        for extra in pkg.ExtraFsprojs do
            updateFsprojVersion extra version

// ============================================================================
// User Interaction
// ============================================================================

module UI =
    let promptYesNo message =
        printf "%s [y/N] " message

        match Console.ReadLine() with
        | null -> false
        | s -> s.ToLower() = "y"

    let printApiChanges =
        function
        | Breaking removed ->
            printfn "  BREAKING API changes:"

            removed
            |> List.truncate 10
            |> List.iter (fun (ApiSignature s) -> printfn "    - %s" s)

            if removed.Length > 10 then
                printfn "    ... and %d more" (removed.Length - 10)
        | Addition added ->
            printfn "  New APIs:"

            added
            |> List.truncate 10
            |> List.iter (fun (ApiSignature s) -> printfn "    + %s" s)

            if added.Length > 10 then
                printfn "    ... and %d more" (added.Length - 10)
        | NoChange -> printfn "  No API changes."

    let showHelp () =
        printfn "Usage: scripts/release.fsx [command] [--publish]"
        printfn ""
        printfn "Automatically detects which packages have API changes and releases them."
        printfn "Each package gets its own version and tag:"
        printfn "  core-v1.0.0      FsHotWatch + FsHotWatch.Cli (same version)"
        printfn "  testprune-v1.0.0 FsHotWatch.TestPrune"
        printfn "  analyzers-v1.0.0 FsHotWatch.Analyzers"
        printfn "  lint-v1.0.0      FsHotWatch.Lint"
        printfn "  fantomas-v1.0.0  FsHotWatch.Fantomas"
        printfn ""
        printfn "Commands:"
        printfn "  (none)  - auto-detect changes, bump based on API diff"
        printfn "  alpha   - start first alpha for all packages (or new cycle)"
        printfn "  beta    - promote all packages to beta"
        printfn "  rc      - promote to release candidate"
        printfn "  stable  - promote to stable release"
        printfn ""
        printfn "Options:"
        printfn "  --publish  - publish to NuGet locally instead of pushing to GitHub"

// ============================================================================
// Command Parsing
// ============================================================================

let parseCommand =
    function
    | "--help"
    | "-h" -> ShowHelp
    | "alpha" -> StartAlpha
    | "beta" -> PromoteToBeta
    | "rc" -> PromoteToRC
    | "stable" -> PromoteToStable
    | "" -> Auto
    | other -> failwithf "Unknown command: %s" other

let parseArgs (argv: string array) : ReleaseCommand * PublishMode =
    let args = argv |> Array.toList
    let hasPublish = args |> List.contains "--publish"

    let cmdArgs =
        args
        |> List.filter (fun a -> a <> "--publish")
        |> List.tryHead
        |> Option.defaultValue ""

    let cmd = parseCommand cmdArgs
    let mode = if hasPublish then LocalPublish else GitHubActions
    (cmd, mode)

// ============================================================================
// Per-Package Release Logic
// ============================================================================

type PackageRelease =
    { Package: PackageConfig
      Bump: Bump.BumpResult
      Tag: string }

/// Determine bump for a single package. Returns None if no release needed.
let determineBump (pkg: PackageConfig) (cmd: ReleaseCommand) : Bump.BumpResult option =
    let state = VCS.getReleaseState pkg

    printfn
        "\n%s: %s"
        pkg.Name
        (match state with
         | FirstRelease -> "(no previous release)"
         | HasPreviousRelease(_, v) -> Version.format v)

    match Bump.forCommand state cmd with
    | Some b -> Some b
    | None ->
        match cmd, state with
        | Auto, HasPreviousRelease(tag, v) ->
            printfn "  Comparing API against %s..." tag
            let baseline = Api.extractFromTag pkg tag
            let current = Api.extractCurrent pkg
            let change = Api.compare baseline current
            UI.printApiChanges change

            if change = NoChange then
                printfn "  Skipping (no changes)."
                None
            else
                Some(Bump.fromApiChange v change)
        | Auto, FirstRelease ->
            printfn "  Skipping (no previous release — use 'alpha' for first release)."
            None
        | _ -> None

// ============================================================================
// Main
// ============================================================================

let release (cmd: ReleaseCommand) (mode: PublishMode) : int =
    match cmd with
    | ShowHelp ->
        UI.showHelp ()
        0
    | _ ->
        if VCS.hasUncommittedChanges () then
            failwith "You have uncommitted changes. Please commit or stash them first."

        Shell.runOrFail "dotnet" "build -c Release --verbosity quiet" |> ignore

        // Determine which packages need releasing
        let skipReserved (pkg: PackageConfig) (bump: Bump.BumpResult) =
            let ver = Version.format bump.NewVersion

            if reservedVersions |> Set.contains (pkg.Name, ver) then
                let patched = Version.bumpPatch bump.NewVersion
                printfn "  Skipping reserved version %s -> %s" ver (Version.format patched)

                { bump with
                    NewVersion = patched
                    Reason = sprintf "%s (skipped reserved %s)" bump.Reason ver }
            else
                bump

        let bumps =
            allPackages
            |> List.choose (fun pkg ->
                determineBump pkg cmd
                |> Option.map (fun bump -> pkg, skipReserved pkg bump))

        if bumps.IsEmpty then
            printfn "\nNo packages to release."
            0
        else
            printfn "\nPackages to release:"

            for (pkg, bump) in bumps do
                let tag = Version.toTag pkg bump.NewVersion
                printfn "  %s: %s -> %s (%s)" pkg.Name (tag) (Version.format bump.NewVersion) bump.Reason

            match mode with
            | LocalPublish -> printfn "\nMode: local publish to NuGet"
            | GitHubActions -> printfn "Mode: push to GitHub Actions"

            if not (UI.promptYesNo "\nContinue?") then
                printfn "Aborted."
                0
            else
                let mutable tags = []

                for (pkg, bump) in bumps do
                    Project.updateVersion pkg bump.NewVersion
                    let tag = VCS.commitAndTag pkg bump.NewVersion
                    tags <- tag :: tags
                    printfn "Created tag %s" tag

                match mode with
                | LocalPublish ->
                    for (pkg, _bump) in bumps do
                        printfn "\nPacking %s..." pkg.Name
                        let nupkgPath = NuGet.pack pkg
                        printfn "Created %s" nupkgPath

                        if UI.promptYesNo (sprintf "Publish %s to NuGet.org?" pkg.Name) then
                            printfn "Publishing..."
                            NuGet.publish nupkgPath
                            printfn "Published!"

                    if UI.promptYesNo "\nPush tags to GitHub?" then
                        VCS.pushTags (tags |> List.rev)
                        printfn "Pushed!"

                | GitHubActions ->
                    if UI.promptYesNo "\nPush to trigger releases?" then
                        VCS.pushTags (tags |> List.rev)
                        printfn "\nPushed! %s/actions" repoUrl
                    else
                        printfn "\nTo push: jj git push --all"

                0

let main (argv: string array) =
    try
        let (cmd, mode) = parseArgs argv
        release cmd mode
    with ex ->
        eprintfn "Error: %s" ex.Message
        1

main (fsi.CommandLineArgs |> Array.skip 1)
