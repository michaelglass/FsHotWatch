/// Which SDK's MSBuild the daemon evaluates a checkout's projects with (AUTOMATION-1621).
///
/// MSBuild runs INSIDE this process, so it must be an SDK this process's runtime can host:
/// a .NET 11 SDK's MSBuild needs System.Runtime 11, and in a .NET 10 daemon every
/// evaluation fails with "Could not load file or assembly 'System.Runtime, Version=11.0.0.0'"
/// and no project loads. `dotnet` resolves the newest installed SDK wherever no `global.json`
/// selects one, so installing a .NET 11 SDK (GitHub's ubuntu-26.04 image 20261111.196 ships
/// one) broke every such checkout.
///
/// The SDK `dotnet` resolves stays the answer whenever this runtime can host it: a
/// `global.json`, with its rollForward, still decides. Only a newer major than the runtime
/// is replaced, and only when no `global.json` asked for it; then the newest installed SDK
/// this runtime can host is used. A `global.json` that selects a newer major is refused,
/// never overridden: it is the repository's own choice, and the remedy is the user's.
///
/// The choice is not a key term of its own: an options record is keyed by the MSBuild path
/// evaluated from (`OptionsRecords` `Sdk`), which names the chosen SDK's version, so a
/// different choice is a different key, and a check record follows its options hash.
module FsHotWatch.SdkChoice

open System
open System.IO
open Ionide.ProjInfo

/// An installed SDK: its version and its versioned directory (the one holding MSBuild.dll).
type Sdk =
    { Version: SemanticVersioning.Version
      Path: string }

/// What the daemon's MSBuild evaluation runs from.
type Choice =
    /// The SDK `dotnet` resolves at the checkout, which this runtime can host.
    | Resolved of Sdk
    /// `dotnet` resolved `resolved`, a newer major than this runtime can host, and no
    /// `global.json` selected it: `chosen` is the newest installed SDK this runtime can host.
    | Substituted of chosen: Sdk * resolved: SemanticVersioning.Version
    /// No SDK this runtime can host may be used; `reason` says why and what to do.
    | Refused of reason: string

/// The installed SDK a resolved version names: that version, or the first installed at or
/// above it, as `Ionide.ProjInfo.Init.init` finds it.
let private installedFor (resolved: SemanticVersioning.Version) (installed: Sdk list) =
    installed
    |> List.sortBy _.Version
    |> List.tryFind (fun sdk -> sdk.Version >= resolved)

/// The SDK to evaluate with, on a runtime of major `runtimeMajor`, given the `global.json`
/// that selected the resolution (if any), the version `dotnet --version` resolved at the
/// checkout and the installed SDKs.
let choose
    (runtimeMajor: int)
    (globalJson: string option)
    (resolved: SemanticVersioning.Version)
    (installed: Sdk list)
    : Choice =
    let listed () =
        installed |> List.map (fun sdk -> sdk.Version.ToString()) |> String.concat ", "

    if resolved.Major <= runtimeMajor then
        match installedFor resolved installed with
        | Some sdk -> Resolved sdk
        | None ->
            Refused
                $"`dotnet --version` resolved SDK %O{resolved}, but no installed SDK is at or above it (installed: %s{listed ()})."
    else
        let needs =
            $"SDK %O{resolved}'s MSBuild needs the .NET %d{resolved.Major} runtime, and fshw runs on .NET %d{runtimeMajor}"

        match globalJson with
        | Some path ->
            Refused
                $"%s{path} selects .NET SDK %O{resolved}. %s{needs}, so it cannot evaluate this checkout's projects. \
                  Pin a %d{runtimeMajor}.x SDK in that global.json (\"rollForward\": \"latestFeature\" keeps it within %d{runtimeMajor}.x)."
        | None ->
            let hostable =
                installed
                |> List.filter (fun sdk -> sdk.Version.Major <= runtimeMajor)
                |> List.sortByDescending _.Version
                |> List.tryHead

            match hostable with
            | Some sdk -> Substituted(sdk, resolved)
            | None ->
                Refused
                    $"No global.json selects an SDK here, so `dotnet` resolves the newest installed, %O{resolved}. %s{needs}, \
                      and no installed SDK is .NET %d{runtimeMajor} or older (installed: %s{listed ()}). \
                      Install a .NET %d{runtimeMajor} SDK and add a global.json pinning it."

/// `dotnet --version` at `repoRoot`: the version, or what it answered instead. Ionide's
/// `versionAt` throws when the command exits non-zero (a global.json pinning an SDK that is
/// not installed, with no rollForward to reach one), and returns an error for output that is
/// not a version; both are the same answer here.
let private versionAt (exe: FileInfo) (repoRoot: string) : Result<SemanticVersioning.Version, string> =
    try
        SdkDiscovery.versionAt (DirectoryInfo repoRoot) exe
        |> Result.mapError (fun (_, _, _, answer) -> answer)
    with ex ->
        Error ex.Message

/// `choose` for the checkout at `repoRoot`, resolving with the muxer `exe`: `dotnet --version`
/// run there (so its `global.json` and rollForward apply) and `dotnet --list-sdks`.
let forCheckout (runtimeMajor: int) (exe: FileInfo) (repoRoot: string) : Choice =
    match versionAt exe repoRoot with
    | Error answer -> Refused $"`dotnet --version` at %s{repoRoot} did not answer a version: %s{answer}"
    | Ok resolved ->
        choose
            runtimeMajor
            (DotnetToolchain.tryFindGlobalJson repoRoot)
            resolved
            (SdkDiscovery.sdks exe
             |> Array.map (fun sdk ->
                 { Version = sdk.Version
                   Path = sdk.Path.FullName })
             |> List.ofArray)
