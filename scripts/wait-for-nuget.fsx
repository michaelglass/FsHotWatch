open System
open System.Diagnostics
open System.IO
open System.Security
open System.Threading
open System.Xml.Linq

type ProbeConfig =
    { Attempts: int
      DelayMs: int
      ProcessTimeoutMs: int
      DotnetExecutable: string
      ProbeParent: string
      Source: string
      ToolRunArguments: string list }

/// What a PackAsTool package was missing. A library package is proven by a restore;
/// a tool package is only proven by an INSTALL that yields a runnable command, so the
/// ways it can be wrong are richer than "restore exited non-zero" and each one names
/// the artifact that was absent rather than dumping the SDK's output and hoping.
type ToolDefect =
    | ShimMissing of expectedPath: string
    | ShimNotExecutable of path: string * mode: string
    | ToolSettingsMissing of searchedRoot: string
    | ToolSettingsMisplaced of found: string
    | ToolSettingsUnreadable of path: string * detail: string
    | CommandNotDeclared of declared: string * packaged: string list
    | EntryPointMissing of command: string * expectedPath: string
    | CommandRunFailed of command: string * invocation: string * detail: string
    | CommandRunTimedOut of command: string * invocation: string * detail: string
    | PackageSettingsRejected of detail: string

/// The verdict of ONE probe attempt. The split that matters is retryable vs terminal:
/// `Unavailable`/`ProbeTimedOut` mean "not indexed yet", which is what the retry loop
/// exists for, while `ProbeShapeInvalid` and `ToolDefective` mean the package WAS found
/// and something about it is wrong. Retrying either of those would burn twenty attempts
/// restating a fact that will not change, and then report a defect as a publication delay.
type ProbeAttempt =
    | Verified
    | Unavailable of detail: string
    | ProbeTimedOut of detail: string
    | ProbeShapeInvalid of detail: string
    | ToolDefective of ToolDefect

type ProcessOutcome =
    | Exited of code: int * detail: string
    | TimedOut of detail: string
    | NotStarted of detail: string

let positiveSetting name fallback =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> Ok fallback
    | raw ->
        match Int32.TryParse raw with
        | true, value when value > 0 -> Ok value
        | _ -> Error $"%s{name} must be a positive integer, got '%s{raw}'"

let stringSetting name fallback =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> fallback
    | value -> value

let configuration () =
    match
        positiveSetting "FSHW_NUGET_PROBE_ATTEMPTS" 20,
        positiveSetting "FSHW_NUGET_PROBE_DELAY_MS" 15000,
        positiveSetting "FSHW_NUGET_PROBE_PROCESS_TIMEOUT_MS" 120000
    with
    | Ok attempts, Ok delayMs, Ok processTimeoutMs ->
        let parent =
            match Environment.GetEnvironmentVariable "FSHW_NUGET_PROBE_PARENT" with
            | null
            | "" -> Path.GetTempPath()
            | value -> Path.GetFullPath value

        Ok
            { Attempts = attempts
              DelayMs = delayMs
              ProcessTimeoutMs = processTimeoutMs
              DotnetExecutable = stringSetting "FSHW_NUGET_PROBE_DOTNET" "dotnet"
              ProbeParent = parent
              // The release path never sets this: the barrier's whole claim is that the
              // package is resolvable from the PUBLIC feed, so the default is nuget.org and
              // nothing else. It is overridable for the same reason FSHW_NUGET_PROBE_DOTNET
              // is — the tests need a feed they can pack a deliberately broken tool into.
              Source = stringSetting "FSHW_NUGET_PROBE_SOURCE" "https://api.nuget.org/v3/index.json"
              // The invocation used to prove the installed command actually RUNS. `--version`
              // is the cheapest argument that exercises the whole shim → apphost → entry
              // assembly chain without starting anything: every tool this repo publishes is
              // a CommandTree CLI and answers it with exit 0.
              ToolRunArguments =
                  (stringSetting "FSHW_NUGET_PROBE_TOOL_ARGS" "--version")
                      .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  |> List.ofArray }
    | Error error, _, _
    | _, Error error, _
    | _, _, Error error -> Error error

let singleProjectValue (project: XDocument) projectPath elementName =
    match
        project.Descendants(XName.Get elementName)
        |> Seq.map _.Value
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.distinct
        |> Seq.toList
    with
    | [ value ] -> Ok value
    | [] -> Error $"%s{projectPath} has no readable <%s{elementName}>"
    | values ->
        let renderedValues = String.concat ", " values
        Error $"%s{projectPath} has ambiguous <%s{elementName}> values: %s{renderedValues}"

let processOutput (task: Threading.Tasks.Task<string>) =
    if task.Wait(TimeSpan.FromSeconds 5.) then task.Result.Trim() else "output drain did not finish within 5s"

let runProcess config (executable: string) (arguments: string list) (environment: (string * string) list) =
    let start = ProcessStartInfo(executable)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    arguments |> List.iter start.ArgumentList.Add

    for key, value in environment do
        start.Environment[key] <- value

    try
        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if child.WaitForExit(config.ProcessTimeoutMs) then
            let detail =
                [ processOutput stdout; processOutput stderr ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " | "

            Exited(child.ExitCode, detail)
        else
            let killDetail =
                try
                    child.Kill(true)
                    if child.WaitForExit(5000) then "process tree killed" else "process tree kill did not exit within 5s"
                with ex ->
                    $"process-tree kill failed: %s{ex.Message}"

            let detail =
                [ killDetail; processOutput stdout; processOutput stderr ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " | "

            TimedOut detail
    with ex ->
        NotStarted ex.Message

/// NU1212 is the SDK saying "this package exists and you asked for it in the wrong
/// shape" — a DotnetToolReference project cannot hold a plain PackageReference. It was
/// once retried twenty times and then reported as "not published", which is how a
/// successful release of FsHotWatch.Cli 0.14.0-alpha.30 was called a failure
/// (AUTOMATION-602). It is a defect in the probe, never evidence about the feed.
let private classifyFailure detail =
    if (detail: string).Contains("NU1212", StringComparison.OrdinalIgnoreCase) then
        ProbeShapeInvalid detail
    else
        Unavailable detail

let runRestore config probeProject probeConfig probePackages =
    match
        runProcess
            config
            config.DotnetExecutable
            [ "restore"
              probeProject
              "--configfile"
              probeConfig
              "--packages"
              probePackages
              "--no-cache"
              "--force"
              "--verbosity"
              "quiet" ]
            []
    with
    | Exited(0, _) -> Verified
    | Exited(_, detail) -> classifyFailure detail
    | TimedOut detail -> ProbeTimedOut detail
    | NotStarted detail -> Unavailable $"could not start restore process: %s{detail}"

let private isExecutable (path: string) =
    if OperatingSystem.IsWindows() then
        true
    else
        let mode = File.GetUnixFileMode path

        mode.HasFlag UnixFileMode.UserExecute
        || mode.HasFlag UnixFileMode.GroupExecute
        || mode.HasFlag UnixFileMode.OtherExecute

/// The installed shim is an APPHOST: it finds the runtime through DOTNET_ROOT and the
/// machine's registered install locations, never through PATH. A probe driven by a
/// `dotnet` that lives somewhere else (a mise/asdf shim, a CI toolcache) therefore
/// installs a tool it then cannot run, and the barrier would report a perfectly good
/// package as broken. Point the shim at the SAME runtime the probe used, unless the
/// caller has already said where it is.
let private toolRunEnvironment (executable: string) =
    match Environment.GetEnvironmentVariable "DOTNET_ROOT" with
    | null
    | "" ->
        let onPath () =
            stringSetting "PATH" ""
            |> _.Split(Path.PathSeparator)
            |> Array.map (fun directory -> Path.Combine(directory, executable))
            |> Array.tryFind File.Exists

        let located =
            if executable.Contains Path.DirectorySeparatorChar then
                Some(Path.GetFullPath executable)
            else
                onPath ()

        match located with
        | None -> []
        | Some path ->
            let real =
                match File.ResolveLinkTarget(path, returnFinalTarget = true) with
                | null -> path
                | target -> target.FullName

            [ ("DOTNET_ROOT", Path.GetDirectoryName real) ]
    | _ -> []

/// A tool's payload is only a tool when it sits at `tools/<tfm>/any/`. Anywhere else
/// and `dotnet tool install` on another SDK, or a consumer restoring the manifest,
/// finds nothing to run — so the location is checked, not just the file's existence.
let private isToolsTfmAny (settingsPath: string) =
    let any = DirectoryInfo(Path.GetDirectoryName settingsPath)
    let tfm = any.Parent
    let tools = if isNull (box tfm) then null else tfm.Parent

    not (isNull (box tools))
    && String.Equals(any.Name, "any", StringComparison.OrdinalIgnoreCase)
    && String.Equals(tools.Name, "tools", StringComparison.OrdinalIgnoreCase)

let private declaredCommands (settingsPath: string) =
    try
        let settings = XDocument.Load settingsPath

        settings.Descendants(XName.Get "Command")
        |> Seq.map (fun command ->
            let name = command.Attribute(XName.Get "Name")
            let entryPoint = command.Attribute(XName.Get "EntryPoint")

            (if isNull (box name) then "" else name.Value),
            (if isNull (box entryPoint) then "" else entryPoint.Value))
        |> Seq.toList
        |> Ok
    with ex ->
        Error ex.Message

/// The delta this ticket is about. `dotnet tool install` exiting 0 proves the package
/// downloaded; it does NOT prove the command the project promises is the command the
/// package ships, that its entry assembly is present, or that the shim runs. Each of
/// those is checked here and each has its own case, so a refusal says what was missing
/// instead of "the tool install failed".
let verifyInstalledTool config (commandName: string) (probeTools: string) =
    let shimName = if OperatingSystem.IsWindows() then $"%s{commandName}.exe" else commandName
    let shim = Path.Combine(probeTools, shimName)

    if not (File.Exists shim) then
        ToolDefective(ShimMissing shim)
    elif not (isExecutable shim) then
        ToolDefective(ShimNotExecutable(shim, string (File.GetUnixFileMode shim)))
    else
        let settingsFiles =
            if Directory.Exists probeTools then
                Directory.GetFiles(probeTools, "DotnetToolSettings.xml", SearchOption.AllDirectories)
            else
                [||]

        match settingsFiles |> Array.filter isToolsTfmAny with
        | [||] when Array.isEmpty settingsFiles -> ToolDefective(ToolSettingsMissing probeTools)
        | [||] -> ToolDefective(ToolSettingsMisplaced settingsFiles[0])
        | placed ->
            let parsed = placed |> Array.map (fun path -> path, declaredCommands path)

            let unreadable =
                parsed
                |> Array.tryPick (fun (path, result) ->
                    match result with
                    | Error detail -> Some(path, detail)
                    | Ok _ -> None)

            match unreadable with
            | Some(path, detail) -> ToolDefective(ToolSettingsUnreadable(path, detail))
            | None ->
                let commands =
                    parsed
                    |> Array.collect (fun (path, result) ->
                        match result with
                        | Ok entries ->
                            entries
                            |> List.map (fun (name, entryPoint) -> name, entryPoint, Path.GetDirectoryName(path: string))
                            |> Array.ofList
                        | Error _ -> [||])

                match
                    commands
                    |> Array.tryFind (fun (name, _, _) -> String.Equals(name, commandName, StringComparison.Ordinal))
                with
                | None ->
                    let packaged =
                        commands |> Array.map (fun (name, _, _) -> name) |> Array.toList

                    ToolDefective(CommandNotDeclared(commandName, packaged))
                | Some(_, entryPoint, directory) ->
                    let entryPointPath = Path.Combine(directory, entryPoint)

                    if not (File.Exists entryPointPath) then
                        ToolDefective(EntryPointMissing(commandName, entryPointPath))
                    else
                        let invocation =
                            String.concat " " (shimName :: config.ToolRunArguments)

                        match runProcess config shim config.ToolRunArguments (toolRunEnvironment config.DotnetExecutable) with
                        | Exited(0, _) -> Verified
                        | Exited(code, detail) ->
                            ToolDefective(CommandRunFailed(commandName, invocation, $"exit %d{code}: %s{detail}"))
                        | TimedOut detail -> ToolDefective(CommandRunTimedOut(commandName, invocation, detail))
                        | NotStarted detail ->
                            ToolDefective(CommandRunFailed(commandName, invocation, $"could not start: %s{detail}"))

/// `dotnet tool install` emits this only AFTER it has downloaded the package and parsed
/// its `DotnetToolSettings.xml` — a missing entry point, a malformed settings file. The
/// package is therefore present and defective, and retrying nineteen more times would
/// end in "still not restorable", which is the false negative this whole barrier exists
/// to stop telling (AUTOMATION-602).
let [<Literal>] private SettingsRejectedMarker = "settings file in the tool's NuGet package is invalid"

let private classifyToolInstallFailure detail =
    if (detail: string).Contains(SettingsRejectedMarker, StringComparison.OrdinalIgnoreCase) then
        ToolDefective(PackageSettingsRejected detail)
    else
        classifyFailure detail

let runToolProbe config packageId version commandName probeConfig (probePackages: string) probeTools =
    let environment =
        [ "NUGET_PACKAGES", probePackages
          "NUGET_HTTP_CACHE_PATH", Path.Combine(probePackages, "http-cache") ]

    match
        runProcess
            config
            config.DotnetExecutable
            [ "tool"
              "install"
              packageId
              "--version"
              version
              "--tool-path"
              probeTools
              "--configfile"
              probeConfig
              "--no-cache" ]
            environment
    with
    | Exited(0, _) -> verifyInstalledTool config commandName probeTools
    | Exited(_, detail) -> classifyToolInstallFailure detail
    | TimedOut detail -> ProbeTimedOut detail
    | NotStarted detail -> Unavailable $"could not start tool-install process: %s{detail}"

let describeDefect packageId version defect =
    let what =
        match defect with
        | ShimMissing expected -> $"no command shim was created at %s{expected}"
        | ShimNotExecutable(path, mode) -> $"the command shim %s{path} is not executable (mode %s{mode})"
        | ToolSettingsMissing root -> $"the installed tool contains no DotnetToolSettings.xml anywhere under %s{root}"
        | ToolSettingsMisplaced found -> $"DotnetToolSettings.xml is at %s{found}, not under tools/<tfm>/any/"
        | ToolSettingsUnreadable(path, detail) -> $"DotnetToolSettings.xml at %s{path} is unreadable: %s{detail}"
        | CommandNotDeclared(declared, packaged) ->
            let rendered =
                if List.isEmpty packaged then
                    "none"
                else
                    String.concat ", " packaged

            $"it declares no command named '%s{declared}' (packaged commands: %s{rendered})"
        | EntryPointMissing(command, expected) -> $"the entry assembly for '%s{command}' is absent at %s{expected}"
        | CommandRunFailed(command, invocation, detail) -> $"'%s{command}' installed but `%s{invocation}` failed — %s{detail}"
        | CommandRunTimedOut(command, invocation, detail) ->
            $"'%s{command}' installed but `%s{invocation}` did not finish within the probe timeout — %s{detail}"
        | PackageSettingsRejected detail -> $"the SDK refused its packaged tool settings: %s{detail}"

    $"tool package defect: %s{packageId} %s{version} was found on the feed, but %s{what}"

let writeProbeFiles config packageId version probeProject probeConfig =
    let escapedPackage = SecurityElement.Escape packageId
    let escapedVersion = SecurityElement.Escape version
    let escapedSource = SecurityElement.Escape config.Source

    let protocol =
        if config.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) then
            " protocolVersion=\"3\""
        else
            ""

    File.WriteAllText(
        probeConfig,
        $"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="probe-source" value="%s{escapedSource}"%s{protocol} />
  </packageSources>
</configuration>
"""
    )

    File.WriteAllText(
        probeProject,
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="%s{escapedPackage}" Version="[%s{escapedVersion}]" /></ItemGroup>
</Project>
"""
    )

let probe config packageId projectPath =
    if not (File.Exists projectPath) then
        Error $"project does not exist: %s{projectPath}"
    else
        try
            let project = XDocument.Load projectPath

            let isTool =
                project.Descendants(XName.Get "PackAsTool")
                |> Seq.exists (fun value -> String.Equals(value.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))

            let commandName =
                if isTool then
                    singleProjectValue project projectPath "ToolCommandName"
                else
                    Ok ""

            match
                singleProjectValue project projectPath "PackageId", singleProjectValue project projectPath "Version", commandName
            with
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> Error error
            | Ok declaredPackageId, Ok _, _ when not (String.Equals(packageId, declaredPackageId, StringComparison.Ordinal)) ->
                Error $"requested %s{packageId}, but %s{projectPath} declares PackageId %s{declaredPackageId}"
            | Ok _, Ok version, Ok command ->
                Directory.CreateDirectory config.ProbeParent |> ignore
                let probeId = Guid.NewGuid().ToString("N")
                let probeRoot = Path.Combine(config.ProbeParent, $"fshw-nuget-probe-%s{probeId}")
                let probeProject = Path.Combine(probeRoot, "probe.csproj")
                let probeConfig = Path.Combine(probeRoot, "NuGet.Config")
                let probePackages = Path.Combine(probeRoot, "packages")
                let probeTools = Path.Combine(probeRoot, "tools")
                Directory.CreateDirectory probeRoot |> ignore

                try
                    writeProbeFiles config packageId version probeProject probeConfig

                    let runAttempt () =
                        if isTool then
                            runToolProbe config packageId version command probeConfig probePackages probeTools
                        else
                            runRestore config probeProject probeConfig probePackages

                    let rec wait attempt lastDetail =
                        if attempt > config.Attempts then
                            Error
                                $"%s{packageId} %s{version} was still not restorable from %s{config.Source} after %d{config.Attempts} attempts. Last restore result: %s{lastDetail}"
                        else
                            let retryOr detail description =
                                if attempt < config.Attempts then
                                    printfn
                                        "NuGet publication barrier: %s %s %s (attempt %d/%d); retrying in %dms"
                                        packageId
                                        version
                                        description
                                        attempt
                                        config.Attempts
                                        config.DelayMs

                                    Thread.Sleep config.DelayMs

                                wait (attempt + 1) detail

                            match runAttempt () with
                            | Verified ->
                                let capability = if isTool then "installable and runnable" else "restorable"

                                Ok
                                    $"NuGet publication barrier: %s{packageId} %s{version} is %s{capability} from %s{config.Source}"
                            | ProbeShapeInvalid detail ->
                                Error
                                    $"probe defect: %s{packageId} %s{version} resolved, but the probe asked for it in an incompatible shape (NU1212): %s{detail}"
                            | ToolDefective defect -> Error(describeDefect packageId version defect)
                            | Unavailable detail -> retryOr $"restore failed: %s{detail}" "unavailable"
                            | ProbeTimedOut detail -> retryOr $"restore timed out: %s{detail}" "restore timed out"

                    wait 1 "no restore attempted"
                finally
                    if Directory.Exists probeRoot then
                        Directory.Delete(probeRoot, true)
        with ex ->
            Error $"could not read release project %s{projectPath}: %s{ex.Message}"

let result =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| packageId; projectPath |] ->
        match configuration () with
        | Error error -> Error error
        | Ok config -> probe config packageId (Path.GetFullPath projectPath)
    | _ -> Error "usage: dotnet fsi scripts/wait-for-nuget.fsx -- <package-id> <project.fsproj>"

match result with
| Ok message -> printfn "%s" message
| Error error ->
    eprintfn "NuGet publication barrier failed: %s" error
    Environment.Exit 1
