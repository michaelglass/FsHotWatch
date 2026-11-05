/// Which Microsoft.NETCore.App an apphost will load, computed the way the .NET host resolves
/// it: the runtimeconfig's `rollForward` (else `DOTNET_ROLL_FORWARD`, else `Minor`) over the
/// runtimes installed under DOTNET_ROOT. Used only to predict a fingerprint, and every
/// prediction is checked against the runtime the run then reports.
module TestPrune.Trace.RuntimeResolution

open System
open System.IO
open System.Text.Json

type private Version =
    { Major: int
      Minor: int
      Patch: int
      Pre: string
      Text: string }

let private parse (text: string) : Version option =
    let core, pre =
        match text.IndexOf '-' with
        | -1 -> text, ""
        | i -> text.Substring(0, i), text.Substring(i + 1)

    match core.Split '.' |> List.ofArray |> List.map Int32.TryParse with
    | [ (true, a); (true, b); (true, c) ] ->
        Some
            { Major = a
              Minor = b
              Patch = c
              Pre = pre
              Text = text }
    | _ -> None

let private key (v: Version) = v.Major, v.Minor, v.Patch

/// The Microsoft.NETCore.App version a runtimeconfig.json asks for, and its `rollForward`
/// when it sets one.
let requestedFramework (runtimeConfigJson: string) : (string * string option) option =
    using (JsonDocument.Parse runtimeConfigJson) (fun doc ->
        let opts = doc.RootElement.GetProperty "runtimeOptions"

        let rollForward =
            match opts.TryGetProperty "rollForward" with
            | true, v -> Some(v.GetString())
            | _ -> None

        let frameworks =
            [ match opts.TryGetProperty "framework" with
              | true, f -> f
              | _ -> ()
              match opts.TryGetProperty "frameworks" with
              | true, fs -> yield! fs.EnumerateArray()
              | _ -> () ]

        frameworks
        |> List.tryFind (fun f -> f.GetProperty("name").GetString() = "Microsoft.NETCore.App")
        |> Option.map (fun f -> f.GetProperty("version").GetString(), rollForward))

/// The Microsoft.NETCore.App versions installed under a dotnet root, sorted.
let installed (dotnetRoot: string) : string list =
    let dir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App")

    if Directory.Exists dir then
        Directory.GetDirectories dir
        |> Array.map Path.GetFileName
        |> Array.sort
        |> List.ofArray
    else
        []

/// The installed version the host picks for `requested` under a roll-forward policy, or
/// `None` when none satisfies it (or the policy is unknown). A prerelease satisfies only a
/// prerelease request.
let resolve (requested: string) (rollForward: string) (installedVersions: string list) : string option =
    match parse requested with
    | None -> None
    | Some req ->
        let candidates =
            installedVersions
            |> List.choose parse
            |> List.filter (fun v -> v.Pre = "" || req.Pre <> "")
            |> List.filter (fun v -> key v >= key req)

        let latestPatchOf (vs: Version list) (major, minor) =
            vs
            |> List.filter (fun v -> v.Major = major && v.Minor = minor)
            |> List.sortByDescending key
            |> List.tryHead

        let lowestMinorThenLatestPatch (vs: Version list) =
            vs
            |> List.sortBy key
            |> List.tryHead
            |> Option.bind (fun v -> latestPatchOf vs (v.Major, v.Minor))

        let sameMajor = candidates |> List.filter (fun v -> v.Major = req.Major)

        let chosen =
            match rollForward.ToLowerInvariant() with
            | "disable" -> candidates |> List.tryFind (fun v -> v.Text = req.Text)
            | "latestpatch" -> latestPatchOf candidates (req.Major, req.Minor)
            | "minor" -> lowestMinorThenLatestPatch sameMajor
            | "major" ->
                lowestMinorThenLatestPatch sameMajor
                |> Option.orElseWith (fun () -> lowestMinorThenLatestPatch candidates)
            | "latestminor" -> sameMajor |> List.sortByDescending key |> List.tryHead
            | "latestmajor" -> candidates |> List.sortByDescending key |> List.tryHead
            | _ -> None

        chosen |> Option.map _.Text

/// A runtime version as `RuntimeInformation.FrameworkDescription` describes it.
let describe (version: string) : string = ".NET " + version

/// The runtime description an apphost whose runtimeconfig is at `runtimeConfigPath` will
/// report, launched with `dotnetRoot` and `DOTNET_ROLL_FORWARD` = `rollForwardEnv`.
let predictRuntime (dotnetRoot: string) (runtimeConfigPath: string) (rollForwardEnv: string option) : string option =
    if not (File.Exists runtimeConfigPath) then
        None
    else
        requestedFramework (File.ReadAllText runtimeConfigPath)
        |> Option.bind (fun (version, configured) ->
            let policy =
                configured |> Option.orElse rollForwardEnv |> Option.defaultValue "Minor"

            resolve version policy (installed dotnetRoot))
        |> Option.map describe
