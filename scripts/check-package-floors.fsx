// Packed dependency floors must never exceed the versions this repository pins.
//
// A nuspec dependency is a MINIMUM, written from the version restore resolved at pack time. When
// FSharp.Core floated (`10.1.*`), TestPrune.Trace 0.7.5 was packed the day 10.1.403 shipped and
// declared `FSharp.Core >= 10.1.403`; every consumer pinned to [10.1.402] then failed restore with
// NU1605. FSharp.Compiler.Service carries the same exposure (it requires an EXACT FSharp.Core).
// This check reads the pins from Directory.Packages.props and the floors from real `dotnet pack`
// output, and fails when a pin floats or a packed floor sits above it.
//
// usage:
//   dotnet fsi scripts/check-package-floors.fsx -- pack
//       pack every project semantic-tagger.json releases (Debug, --no-build) and check the result
//   dotnet fsi scripts/check-package-floors.fsx -- check <Directory.Packages.props> <nupkg-dir>
//       check already-packed .nupkg files

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Xml.Linq

let watchedPackages = [ "FSharp.Core"; "FSharp.Compiler.Service" ]

let numericVersion (raw: string) =
    let release = raw.Split('-', '+').[0].Trim()

    match Version.TryParse release with
    | true, version -> Some version
    | _ -> None

let pinnedVersions (propsPath: string) =
    let document = XDocument.Load propsPath

    let pins =
        document.Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = "PackageVersion")
        |> Seq.choose (fun element ->
            match element.Attribute(XName.Get "Include"), element.Attribute(XName.Get "Version") with
            | null, _
            | _, null -> None
            | id, version -> Some(id.Value, version.Value))
        |> Map.ofSeq

    watchedPackages
    |> List.map (fun id ->
        match Map.tryFind id pins with
        | None -> id, Error $"%s{id} has no PackageVersion in %s{propsPath}"
        | Some version when version.Contains '*' || version.Contains '[' || version.Contains '(' ->
            id,
            Error
                $"%s{id} is not pinned: Directory.Packages.props declares '%s{version}', so every pack writes whatever restore resolved that day as the floor"
        | Some version ->
            match numericVersion version with
            | Some parsed -> id, Ok(version, parsed)
            | None -> id, Error $"%s{id} pin '%s{version}' is not a version")

/// The lower bound of a nuspec version range: "1.2.3", "[1.2.3, )", "[1.2.3, 1.2.3]".
let floorOf (range: string) =
    let trimmed = range.Trim()

    if trimmed.StartsWith "[" || trimmed.StartsWith "(" then
        trimmed.Substring(1).Split(',').[0].Trim()
    else
        trimmed

let nuspecDependencies (nupkg: string) =
    use archive = ZipFile.OpenRead nupkg

    let entry =
        archive.Entries
        |> Seq.find (fun entry -> not (entry.FullName.Contains "/") && entry.FullName.EndsWith ".nuspec")

    use stream = entry.Open()
    let document = XDocument.Load stream

    document.Descendants()
    |> Seq.filter (fun element -> element.Name.LocalName = "dependency")
    |> Seq.choose (fun element ->
        match element.Attribute(XName.Get "id"), element.Attribute(XName.Get "version") with
        | null, _
        | _, null -> None
        | id, version -> Some(id.Value, version.Value))
    |> Seq.toList

let check (propsPath: string) (packageDir: string) =
    let pins = pinnedVersions propsPath

    let pinErrors =
        pins
        |> List.choose (fun (_, pin) ->
            match pin with
            | Error error -> Some error
            | Ok _ -> None)

    let packages =
        if Directory.Exists packageDir then
            Directory.GetFiles(packageDir, "*.nupkg") |> Array.sort |> Array.toList
        else
            []

    let emptyErrors =
        if List.isEmpty packages then
            [ $"no .nupkg files in %s{packageDir}: nothing was checked, which is not a pass" ]
        else
            []

    let floorErrors =
        packages
        |> List.collect (fun nupkg ->
            let name = Path.GetFileNameWithoutExtension nupkg

            nuspecDependencies nupkg
            |> List.filter (fun (id, _) -> List.contains id watchedPackages)
            |> List.choose (fun (id, range) ->
                let floor = floorOf range
                printfn "%s: %s >= %s" name id floor

                match List.tryFind (fun (pinned, _) -> pinned = id) pins with
                | Some(_, Ok(pinText, pin)) ->
                    match numericVersion floor with
                    | Some parsed when parsed <= pin -> None
                    | Some _ ->
                        Some
                            $"%s{name} declares %s{id} >= %s{floor}, above the pinned %s{pinText}: consumers pinned at %s{pinText} fail restore with NU1605"
                    | None -> Some $"%s{name} declares %s{id} with an unreadable range '%s{range}'"
                | _ -> None))

    pinErrors @ emptyErrors @ floorErrors

let releasedProjects (root: string) =
    use config =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "semantic-tagger.json")))

    config.RootElement.GetProperty("packages").EnumerateArray()
    |> Seq.collect (fun package ->
        let shared =
            match package.TryGetProperty "fsProjsSharingSameTag" with
            | true, projects ->
                projects.EnumerateArray()
                |> Seq.map (fun project -> project.GetString())
                |> Seq.toList
            | _ -> []

        package.GetProperty("fsproj").GetString() :: shared)
    |> Seq.distinct
    |> Seq.toList

let pack (root: string) (outputDir: string) =
    releasedProjects root
    |> List.choose (fun project ->
        let start = ProcessStartInfo("dotnet")
        start.WorkingDirectory <- root
        start.UseShellExecute <- false

        [ "pack"
          project
          "-c"
          "Debug"
          "--no-build"
          "--no-restore"
          "-o"
          outputDir
          "-nologo" ]
        |> List.iter start.ArgumentList.Add

        use child = Process.Start start
        child.WaitForExit()

        if child.ExitCode = 0 then
            None
        else
            Some $"dotnet pack %s{project} exited %d{child.ExitCode} (run `dotnet build` first: this packs --no-build)")

let errors =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| "check"; propsPath; packageDir |] -> check propsPath packageDir
    | [| "pack" |] ->
        let root = Directory.GetCurrentDirectory()

        let outputDir =
            Path.Combine(Path.GetTempPath(), $"testprune-package-floors-%A{Guid.NewGuid()}")

        try
            match pack root outputDir with
            | [] -> check (Path.Combine(root, "Directory.Packages.props")) outputDir
            | packErrors -> packErrors
        finally
            if Directory.Exists outputDir then
                Directory.Delete(outputDir, true)
    | _ ->
        [ "usage: dotnet fsi scripts/check-package-floors.fsx -- pack | check <Directory.Packages.props> <nupkg-dir>" ]

match errors with
| [] -> printfn "Package floors: every packed FSharp.Core / FSharp.Compiler.Service floor is at or below its pin"
| _ ->
    errors |> List.iter (eprintfn "Package floor check failed: %s")
    exit 1
