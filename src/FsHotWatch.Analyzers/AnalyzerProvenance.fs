/// Provenance of the rules a cached analyzer result claims to have applied.
/// Output digests bind a local build/load; only declared semantic inputs share.
module internal FsHotWatch.Analyzers.AnalyzerProvenance

open System
open System.IO
open System.Xml.Linq
open System.Security.Cryptography

[<NoEquality; NoComparison>]
type Snapshot =
    private
        { Semantic: string
          Materialization: string }

let semantic snapshot = snapshot.Semantic
let materialization snapshot = snapshot.Materialization
let private sha (text: string) = FsHotWatch.CheckCache.sha256Hex text

let private fileHash path =
    File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString

let private name value = XName.Get value

let private required attribute (element: XElement) =
    match element.Attribute(name attribute) with
    | null -> failwith $"Missing {attribute} in {element.Name}"
    | value when String.IsNullOrWhiteSpace value.Value -> failwith $"Empty {attribute} in {element.Name}"
    | value -> value.Value

let private section sectionName (element: XElement) =
    match element.Elements(name sectionName) |> Seq.toList with
    | [ value ] -> value
    | _ -> failwith $"Expected one {sectionName} in {element.Name}"

let private checkHash path expected =
    let actual = fileHash path

    if not (String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) then
        failwith $"Analyzer provenance is stale: {path}"

    actual

let private distinct label values =
    if List.distinct values |> List.length <> List.length values then
        failwith $"Duplicate analyzer provenance {label}"

let private encode values =
    values
    |> List.map (fun (value: string) -> $"{value.Length}:{value}")
    |> String.concat ""

let private discoveryHash root =
    let extensions =
        set
            [ ".fs"
              ".fsi"
              ".fsx"
              ".cs"
              ".fsproj"
              ".csproj"
              ".props"
              ".targets"
              ".json" ]

    let excluded = set [ "bin"; "obj"; ".git"; ".jj"; ".workspaces"; "node_modules" ]

    let rec files directory =
        seq {
            yield!
                Directory.GetFiles directory
                |> Seq.filter (fun path -> extensions.Contains(Path.GetExtension path))

            for child in Directory.GetDirectories directory do
                if not (excluded.Contains(Path.GetFileName child)) then
                    yield! files child
        }

    files root
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
    |> Seq.sort
    |> Seq.toList
    |> encode
    |> sha

let private tryPackageRoot (output: string) =
    let rec walk (directory: DirectoryInfo) =
        if isNull directory then
            None
        elif File.Exists(Path.Combine(directory.FullName, ".nupkg.metadata")) then
            if isNull directory.Parent then
                None
            else
                let packageId = directory.Parent.Name.ToLowerInvariant()
                let nuspec = Path.Combine(directory.FullName, packageId + ".nuspec")

                let metadata =
                    XDocument.Load(nuspec).Descendants()
                    |> Seq.find (fun node -> node.Name.LocalName = "metadata")

                let field fieldName =
                    metadata.Elements()
                    |> Seq.find (fun node -> node.Name.LocalName = fieldName)
                    |> fun node -> node.Value.Trim().ToLowerInvariant()

                let id = field "id"
                let version = field "version"

                if id <> packageId || String.IsNullOrWhiteSpace version then
                    failwith $"Invalid NuGet package provenance: {nuspec}"

                Some(id, version)
        else
            walk directory.Parent

    walk (DirectoryInfo(Path.GetDirectoryName output))

let rec private readOutput evaluations (seen: Set<string>) (output: string) =
    let output = Path.GetFullPath output

    if seen.Contains output then
        failwith $"Cyclic analyzer provenance: {output}"

    let seen = Set.add output seen
    let receiptPath = output + ".fshw-analyzer.xml"
    let packageReceiptPath = output + ".fshw-package.xml"

    if not (File.Exists output) then
        failwith $"Analyzer output is missing: {output}"

    if File.Exists receiptPath then
        let root = XDocument.Load(receiptPath).Root

        if
            isNull root
            || root.Name <> name "AnalyzerProvenance"
            || required "version" root <> "1"
        then
            failwith $"Unsupported analyzer provenance: {receiptPath}"

        if Path.GetFullPath(required "output" root) <> output then
            failwith $"Analyzer provenance names another output: {receiptPath}"

        let actualOutput = checkHash output (required "outputHash" root)
        let files = (section "Inputs" root).Elements(name "File") |> Seq.toList
        let keys = files |> List.map (required "key")
        distinct "input keys" keys
        let inputPaths prefix =
            files
            |> List.filter (fun file -> (required "key" file).StartsWith(prefix, StringComparison.Ordinal))
            |> List.map (required "path")
        AnalyzerEvaluation.validate evaluations (inputPaths "project:") (inputPaths "source:") (section "Evaluation" root)

        if
            not (
                keys
                |> List.exists (fun key -> key.StartsWith("source:", StringComparison.Ordinal))
            )
            || not (
                keys
                |> List.exists (fun key -> key.StartsWith("project:", StringComparison.Ordinal))
            )
        then
            failwith $"Analyzer provenance lacks source/project inputs: {receiptPath}"

        for missing in (section "Inputs" root).Elements(name "Missing") do
            let path = required "path" missing

            if File.Exists path || Directory.Exists path then
                failwith $"A new analyzer build input appeared: {path}"

        let discoveries =
            (section "Discovery" root).Elements(name "Directory") |> Seq.toList

        if List.isEmpty discoveries then
            failwith $"Analyzer provenance lacks input discovery: {receiptPath}"

        for directory in discoveries do
            let directoryPath = required "path" directory

            if discoveryHash directoryPath <> required "hash" directory then
                failwith $"Analyzer input membership changed: {directoryPath}"

        let inputs =
            files
            |> List.map (fun file ->
                let key = required "key" file
                let hash = checkHash (required "path" file) (required "hash" file)
                key, hash)
            // Bind reference bytes locally without making package/compiler output
            // bytes a shared semantic key. Their declared identities follow below.
            |> List.filter (fun (key, _) -> not (key.StartsWith("binding:", StringComparison.Ordinal)))
            |> List.map (fun (key, hash) -> encode [ "input"; key; hash ])

        let options =
            (section "Options" root).Elements(name "Option")
            |> Seq.map (fun option ->
                let optionName = required "name" option
                let value = option.Attribute(name "value")

                if isNull value then
                    failwith $"Missing option value: {optionName}"

                optionName, value.Value)
            |> Seq.toList

        distinct "option names" (options |> List.map fst)

        if List.isEmpty options then
            failwith $"Analyzer provenance lacks compiler options: {receiptPath}"

        let packages =
            (section "Packages" root).Elements(name "Package")
            |> Seq.map (fun package ->
                encode
                    [ "package"
                      (required "id" package).ToLowerInvariant()
                      (required "version" package).ToLowerInvariant() ])
            |> Seq.toList

        let dependencies =
            (section "Dependencies" root).Elements(name "Project")
            |> Seq.map (fun dependency ->
                let dependencyOutput = required "output" dependency
                let identity, _ = readOutput evaluations seen dependencyOutput

                if identity <> required "identity" dependency then
                    failwith $"Analyzer dependency provenance changed: {dependencyOutput}"

                encode [ "project"; required "key" dependency; identity ])
            |> Seq.toList

        let semanticInputs =
            inputs
            @ (options |> List.map (fun (key, value) -> encode [ "option"; key; value ]))
            @ packages
            @ dependencies

        sha (encode ("first-party-v1" :: List.sort semanticInputs)), actualOutput
    elif File.Exists packageReceiptPath then
        let root = XDocument.Load(packageReceiptPath).Root

        if
            isNull root
            || root.Name <> name "PackageProvenance"
            || required "schema" root <> "1"
        then
            failwith $"Unsupported package provenance: {packageReceiptPath}"

        if Path.GetFullPath(required "output" root) <> output then
            failwith $"Package provenance names another output: {packageReceiptPath}"

        let actualOutput = checkHash output (required "outputHash" root)

        sha (
            encode
                [ "package-v1"
                  (required "id" root).ToLowerInvariant()
                  (required "version" root).ToLowerInvariant() ]
        ),
        actualOutput
    else
        match tryPackageRoot output with
        | Some(id, version) ->
            let identity = sha (encode [ "package-v1"; id; version ])
            identity, identity
        | None ->
            failwith
                $"No accountable analyzer provenance for {output}; build the producer with FsHotWatch.AnalyzerProvenance.targets"

/// Fallible filesystem boundary. Missing or stale provenance is never a reusable
/// success, including when an old DLL remains beside freshly edited sources.
let trySnapshot (isExcluded: string -> bool) (paths: string list) : Result<Snapshot, string> =
    try
        let outputs =
            paths
            |> List.collect (fun path ->
                if not (Directory.Exists path) then
                    failwith $"Analyzer directory is missing: {path}"

                let files =
                    Directory.GetFiles(path, "*.dll")
                    |> Array.filter (Path.GetFileNameWithoutExtension >> isExcluded >> not)

                if files.Length = 0 then
                    failwith $"Analyzer directory has no accountable assemblies: {path}"

                files |> Array.toList)

        let evaluations = System.Collections.Generic.Dictionary<string, Result<unit, string>>()

        let identities =
            outputs
            |> List.map (fun output ->
                let identity, local = readOutput evaluations Set.empty output
                encode [ Path.GetFileName output; identity ], encode [ output; local ])

        Ok
            { Semantic = identities |> List.map fst |> List.sort |> encode |> sha
              Materialization = identities |> List.map snd |> List.sort |> encode |> sha }
    with ex ->
        Error ex.Message
