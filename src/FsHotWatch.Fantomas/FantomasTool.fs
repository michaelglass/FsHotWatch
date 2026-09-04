/// The Fantomas the REPOSITORY pins, and nothing else.
///
/// AUTOMATION-447. Both format components used to link their own `Fantomas.Core` and
/// format in-process with that library's defaults, while hosted CI ran the repository's
/// pinned `dotnet fantomas` tool (its version, its `.editorconfig`). Two formatters can
/// only agree by coincidence, and a local `formatted 0 files` said nothing about which
/// one had been consulted. So the plugin no longer carries a formatter at all: it reads
/// the pin out of `.config/dotnet-tools.json`, runs `dotnet tool run fantomas` — the same
/// resolution CI's `dotnet fantomas` uses — and states the version it ran on every status
/// line. A repository that pins no Fantomas gets a typed refusal, never a green.
module FsHotWatch.Fantomas.FantomasTool

open System
open System.IO
open System.Text.Json
open FsHotWatch.ProcessHelper

/// Where `dotnet tool run` looks: the local tool manifest at the repository root.
let manifestPath (repoRoot: string) : string =
    Path.Combine(repoRoot, ".config", "dotnet-tools.json")

/// The Fantomas a repository pins. `Version` is the manifest's string verbatim — it is
/// evidence, so it is never normalised.
type FantomasPin =
    { Version: string
      ManifestPath: string }

/// Why no pinned Fantomas could be found. Each case names the file the operator has to
/// fix, because the remedy differs: a missing manifest wants `dotnet new tool-manifest`,
/// a missing entry wants `dotnet tool install fantomas`.
[<RequireQualifiedAccess>]
type PinError =
    /// No `.config/dotnet-tools.json` at the repository root.
    | ManifestMissing of expectedPath: string
    /// The manifest exists but is not JSON this reader understands.
    | ManifestUnreadable of path: string * reason: string
    /// The manifest exists and pins tools, but `fantomas` is not one of them.
    | PinMissing of manifestPath: string

[<RequireQualifiedAccess>]
module PinError =
    let render (error: PinError) : string =
        match error with
        | PinError.ManifestMissing path ->
            $"no tool manifest at %s{path} — `dotnet new tool-manifest && dotnet tool install fantomas` pins the formatter the format plugin runs"
        | PinError.ManifestUnreadable(path, reason) -> $"could not read the tool manifest %s{path}: %s{reason}"
        | PinError.PinMissing path ->
            $"%s{path} pins no `fantomas` — `dotnet tool install fantomas` adds the pin the format plugin runs (CI's `dotnet fantomas` resolves against the same file)"

/// Read the `fantomas` pin out of manifest JSON. Pure, so the shape is testable without
/// a filesystem. Tool ids are NuGet package ids and therefore case-insensitive.
let parseManifest (manifestPath: string) (json: string) : Result<FantomasPin, PinError> =
    try
        use doc = JsonDocument.Parse json

        match doc.RootElement.TryGetProperty "tools" with
        | true, tools when tools.ValueKind = JsonValueKind.Object ->
            let entry =
                tools.EnumerateObject()
                |> Seq.tryFind (fun p -> String.Equals(p.Name, "fantomas", StringComparison.OrdinalIgnoreCase))

            match entry with
            | None -> Error(PinError.PinMissing manifestPath)
            | Some p ->
                match p.Value.TryGetProperty "version" with
                | true, v when
                    v.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(v.GetString()))
                    ->
                    Ok
                        { Version = v.GetString()
                          ManifestPath = manifestPath }
                | _ -> Error(PinError.ManifestUnreadable(manifestPath, "the `fantomas` entry has no string `version`"))
        | _ -> Error(PinError.PinMissing manifestPath)
    with :? JsonException as ex ->
        Error(PinError.ManifestUnreadable(manifestPath, ex.Message))

/// The pin for a repository, read fresh every time — a manifest edit must be seen on
/// the next event, not on the next daemon.
let readPin (repoRoot: string) : Result<FantomasPin, PinError> =
    let path = manifestPath repoRoot

    if not (File.Exists path) then
        Error(PinError.ManifestMissing path)
    else
        // MGA-ERROR-REPORT-001:ok — an unreadable manifest is returned as a typed PinError, not swallowed
        try
            parseManifest path (File.ReadAllText path)
        with ex ->
            Error(PinError.ManifestUnreadable(path, $"%s{ex.GetType().Name}: %s{ex.Message}"))

/// The evidence line every format status carries: which binary, which version, pinned
/// where. `dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)`.
let describe (repoRoot: string) (pin: FantomasPin) : string =
    $"dotnet fantomas %s{pin.Version} (pinned in %s{Path.GetRelativePath(repoRoot, pin.ManifestPath)})"

/// THE seam between the plugin and the process. Production is `dotnetToolRunner`; a
/// test substitutes a recorder to prove which pin and which arguments the plugin hands
/// over, without a NuGet cache in the loop.
///
/// `pin` is passed so a recorder can assert the version agreement; the real runner
/// does not need it — `dotnet tool run` resolves the version from the manifest itself,
/// which is exactly the property this module exists for.
/// Arguments, in order: the pin, the argument string after `fantomas`, the working
/// directory, the timeout.
type Runner = FantomasPin -> string -> string -> TimeSpan -> ProcessOutcome

/// `dotnet tool run fantomas …` from the repository root, so the manifest that resolves
/// it is the one `readPin` read. Fantomas prints nothing for a clean `--check`, so
/// output cannot prove liveness and the timeout is the bound (`ProcessBounds.silent`).
let dotnetToolRunner: Runner =
    fun _pin args workDir timeout ->
        runProcess "dotnet" $"tool run fantomas %s{args}" workDir [] (ProcessBounds.silent timeout)

/// A file path as one shell-safe argument.
let private quote (path: string) : string =
    "\"" + path.Replace("\"", "\\\"") + "\""

/// Why the pinned tool could not deliver a verdict at all. Distinct from a file it could
/// not format (that is a per-file finding inside a successful run): these are failures of
/// the TOOL, and the plugin turns every one of them into a failed status.
[<RequireQualifiedAccess; NoComparison>]
type ToolFailure =
    /// The manifest pins a version that is not in the NuGet cache. The SDK's own
    /// message names the remedy (`dotnet tool restore`).
    | NotRestored of pin: FantomasPin * output: string
    /// The tool ran past its budget and was killed.
    | TimedOut of after: TimeSpan * kill: KillOutcome
    /// A non-zero exit that is neither "needs formatting" (99) nor a per-file format
    /// error: the tool itself failed.
    | Failed of exitCode: int * output: string

[<RequireQualifiedAccess>]
module ToolFailure =
    let render (failure: ToolFailure) : string =
        match failure with
        | ToolFailure.NotRestored(pin, output) ->
            $"fantomas %s{pin.Version} is pinned in %s{pin.ManifestPath} but not restored — run `dotnet tool restore`: %s{output}"
        | ToolFailure.TimedOut(after, kill) ->
            $"dotnet fantomas timed out after %d{int after.TotalSeconds}s (%s{renderKillBrief kill})"
        | ToolFailure.Failed(code, output) -> $"dotnet fantomas exited %d{code}: %s{output}"

/// What one `--check` run found. Every path is the absolute path the caller passed in.
type CheckReport =
    {
        /// Files the pinned Fantomas would rewrite.
        NeedsFormatting: string list
        /// Files the pinned Fantomas could not process at all (a parse error), with
        /// its first line of explanation.
        FormatErrors: (string * string) list
    }

/// What one rewrite run did.
type FormatReport =
    {
        /// Files whose bytes the pinned Fantomas changed.
        Modified: string list
        /// As in `CheckReport`.
        FormatErrors: (string * string) list
    }

/// Fantomas's `--check` exit code for "at least one file needs formatting".
[<Literal>]
let NeedsFormattingExitCode = 99

/// Resolve a path Fantomas printed back to the absolute path the caller passed. The
/// tool echoes paths as given, so an exact match is the normal case; the fallback covers
/// a relative echo from a `workDir`-relative invocation.
let private resolveEcho (workDir: string) (requested: Set<string>) (echoed: string) : string option =
    if requested.Contains echoed then
        Some echoed
    else
        let full = Path.GetFullPath(Path.Combine(workDir, echoed))
        if requested.Contains full then Some full else None

/// Pure reading of the tool's text. `<path> needs formatting` lines and the two
/// shapes of per-file error — `error: Failed to format <path>: <reason>` from `--check`,
/// `Failed to format file: <path> : <reason>` from a rewrite — are the facts it states
/// per file; everything else (the coloured summary table, stack traces) is noise.
let parseOutput (workDir: string) (requested: string list) (output: string) : CheckReport =
    let requestedSet = Set.ofList requested

    let lines =
        output.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun l -> l.Trim())

    let needsFormatting =
        lines
        |> Array.choose (fun line ->
            if line.EndsWith(" needs formatting", StringComparison.Ordinal) then
                resolveEcho workDir requestedSet (line.Substring(0, line.Length - " needs formatting".Length))
            else
                None)
        |> List.ofArray
        |> List.distinct

    let formatErrors =
        lines
        |> Array.choose (fun line ->
            let marker = "Failed to format "

            match line.IndexOf(marker, StringComparison.Ordinal) with
            | -1 -> None
            | i ->
                let rest =
                    let afterMarker = line.Substring(i + marker.Length)

                    if afterMarker.StartsWith("file:", StringComparison.Ordinal) then
                        afterMarker.Substring("file:".Length).TrimStart()
                    else
                        afterMarker

                // The path is everything up to the LAST ` : ` / `: ` that starts the
                // reason; resolve greedily from the longest candidate so a path
                // containing `: ` still matches when it is one we asked for.
                let candidates =
                    [ for j in rest.Length - 1 .. -1 .. 1 do
                          if j + 1 < rest.Length && rest[j] = ':' && rest[j + 1] = ' ' then
                              yield rest.Substring(0, j).TrimEnd(), rest.Substring(j + 2).Trim() ]

                candidates
                |> List.tryPick (fun (path, reason) ->
                    resolveEcho workDir requestedSet path |> Option.map (fun p -> p, reason)))
        |> List.ofArray
        |> List.distinctBy fst

    { NeedsFormatting = needsFormatting
      FormatErrors = formatErrors }

/// Files per invocation: keeps the argument string far under every platform's limit
/// while still amortising the tool's start-up over a batch.
[<Literal>]
let private ChunkSize = 200

/// Was this the SDK refusing to run the tool, rather than the tool speaking?
let private isNotRestored (output: string) =
    output.Contains("dotnet tool restore", StringComparison.OrdinalIgnoreCase)

/// Classify one process outcome into what the tool said, or why it could not.
let private interpret
    (pin: FantomasPin)
    (workDir: string)
    (files: string list)
    (outcome: ProcessOutcome)
    : Result<CheckReport, ToolFailure> =
    match outcome with
    | ProcessOutcome.Succeeded _ ->
        Ok
            { NeedsFormatting = []
              FormatErrors = [] }
    | ProcessOutcome.Failed(code, output) when code = NeedsFormattingExitCode ->
        Ok(parseOutput workDir files (ProcessOutput.text output))
    | ProcessOutcome.Failed(code, output) ->
        let text = ProcessOutput.text output

        if isNotRestored text then
            Error(ToolFailure.NotRestored(pin, text))
        else
            let report = parseOutput workDir files text

            // Exit 1 with per-file `Failed to format` lines is the tool REPORTING on
            // files, not failing: those files are findings. With no such line, the
            // tool itself failed and the exit code is the only fact.
            if List.isEmpty report.FormatErrors then
                Error(ToolFailure.Failed(code, text))
            else
                Ok report
    | ProcessOutcome.TimedOut(after, _, kill) -> Error(ToolFailure.TimedOut(after, kill))

/// Run the pinned tool over `files` in chunks, folding the reports; the first tool
/// failure ends the run.
let private runChunked
    (runner: Runner)
    (pin: FantomasPin)
    (repoRoot: string)
    (timeout: TimeSpan)
    (leadingArgs: string)
    (files: string list)
    : Result<CheckReport, ToolFailure> =
    let empty =
        { NeedsFormatting = []
          FormatErrors = [] }

    (Ok empty, List.chunkBySize ChunkSize files)
    ||> List.fold (fun acc chunk ->
        acc
        |> Result.bind (fun report ->
            let args = leadingArgs + (chunk |> List.map quote |> String.concat " ")

            runner pin args repoRoot timeout
            |> interpret pin repoRoot chunk
            |> Result.map (fun r ->
                { NeedsFormatting = report.NeedsFormatting @ r.NeedsFormatting
                  FormatErrors = report.FormatErrors @ r.FormatErrors })))

/// `dotnet tool run fantomas --check <files>`: which of `files` the pinned Fantomas
/// would rewrite. Nothing on disk changes.
let check
    (runner: Runner)
    (pin: FantomasPin)
    (repoRoot: string)
    (timeout: TimeSpan)
    (files: string list)
    : Result<CheckReport, ToolFailure> =
    if List.isEmpty files then
        Ok
            { NeedsFormatting = []
              FormatErrors = [] }
    else
        runChunked runner pin repoRoot timeout "--check " files

/// `dotnet tool run fantomas <files>`: rewrite in place. The tool does not list what it
/// changed (its summary is a count), so `Modified` is measured — the bytes before
/// against the bytes after — which is also the only definition the daemon's watcher
/// suppression can use.
let format
    (runner: Runner)
    (pin: FantomasPin)
    (repoRoot: string)
    (timeout: TimeSpan)
    (files: string list)
    : Result<FormatReport, ToolFailure> =
    if List.isEmpty files then
        Ok { Modified = []; FormatErrors = [] }
    else
        let before =
            files
            |> List.map (fun f ->
                f,
                (try
                    Some(File.ReadAllBytes f)
                 with _ ->
                     None))
            |> Map.ofList

        runChunked runner pin repoRoot timeout "" files
        |> Result.map (fun report ->
            let modified =
                files
                |> List.filter (fun f ->
                    match Map.tryFind f before with
                    | Some(Some bytes) ->
                        (try
                            not (ReadOnlySpan(bytes).SequenceEqual(ReadOnlySpan(File.ReadAllBytes f)))
                         with _ ->
                             false)
                    | _ -> false)

            { Modified = modified
              FormatErrors = report.FormatErrors })

/// The `.editorconfig` files that decide how the pinned tool formats `file`: every one
/// from the repository root down to the file's directory, as (label, content) pairs
/// for a cache key. A formatter setting is an input to the verdict exactly as the source
/// bytes are; a key that omitted it would replay `format OK` across a config edit.
let editorConfigInputs (repoRoot: string) (files: string list) : (string * string) list =
    let root = Path.GetFullPath repoRoot

    let dirsOf (file: string) =
        let rec up (dir: DirectoryInfo) acc =
            if isNull dir then
                acc
            else
                let acc = dir.FullName :: acc

                if String.Equals(dir.FullName, root, StringComparison.Ordinal) then
                    acc
                else
                    up dir.Parent acc

        up (DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath file))) []

    files
    |> List.collect dirsOf
    |> List.distinct
    |> List.choose (fun dir ->
        let candidate = Path.Combine(dir, ".editorconfig")

        // A config that exists but cannot be read is still an input; substituting
        // "nothing" would key the verdict as if no config applied.
        if File.Exists candidate then
            // The label is REPO-RELATIVE so the same `.editorconfig` produces the same
            // key input in every checkout of the repository; the value is its content.
            let key = FsHotWatch.CachePathIdentity.keyOf (Some root) candidate
            Some($"editorconfig:%s{key}", File.ReadAllText candidate)
        else
            None)
