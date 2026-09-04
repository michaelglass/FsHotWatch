module FsHotWatch.Tests.FantomasToolTests

// AUTOMATION-447 — the format plugin runs the Fantomas the REPOSITORY pins.
//
// Everything here is about the seam between the plugin and `dotnet tool run fantomas`:
// how the pin is read, what is handed to the process, and how its exit code and text
// are turned back into per-file facts. The process outcomes are the ones the real
// tool produced on 2026-09-04 (fantomas 7.0.5): exit 0 silent when clean, exit 99 with
// `<path> needs formatting` lines, exit 1 with `error: Failed to format <path>: …` on a
// parse error, exit 1 `Run "dotnet tool restore" …` when the pin is not restored.

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.ProcessHelper
open FsHotWatch.Fantomas.FantomasTool
open FsHotWatch.Tests.TestHelpers

let private manifestWith (version: string) =
    $"""{{ "version": 1, "isRoot": true, "tools": {{ "paket": {{ "version": "10.3.1", "commands": ["paket"] }}, "fantomas": {{ "version": "%s{version}", "commands": ["fantomas"], "rollForward": false }} }} }}"""

let private drained (text: string) = ProcessOutput.Drained text

let private pin =
    { Version = "7.0.5"
      ManifestPath = "/repo/.config/dotnet-tools.json" }

/// A runner that answers with a fixed outcome and records what it was asked.
let private scripted (outcome: ProcessOutcome) =
    let calls = ResizeArray<FantomasPin * string * string * TimeSpan>()

    let runner: Runner =
        fun pin args workDir timeout ->
            calls.Add((pin, args, workDir, timeout))
            outcome

    runner, (fun () -> List.ofSeq calls)

// =============================================================================
// The pin
// =============================================================================

[<Fact>]
let ``parseManifest reads the fantomas pin verbatim`` () =
    test
        <@
            parseManifest "/r/.config/dotnet-tools.json" (manifestWith "7.0.5") = Ok
                { Version = "7.0.5"
                  ManifestPath = "/r/.config/dotnet-tools.json" }
        @>

    // A pre-release string is evidence too, and is not normalised.
    test
        <@
            parseManifest "/r/m.json" (manifestWith "8.0.0-alpha-001")
            |> Result.map (fun p -> p.Version) = Ok "8.0.0-alpha-001"
        @>

[<Fact>]
let ``parseManifest matches the tool id case-insensitively, as NuGet does`` () =
    let json =
        """{ "version": 1, "tools": { "Fantomas": { "version": "7.0.5", "commands": ["fantomas"] } } }"""

    test <@ parseManifest "/r/m.json" json |> Result.map (fun p -> p.Version) = Ok "7.0.5" @>

[<Fact>]
let ``parseManifest returns PinMissing when the manifest pins other tools only`` () =
    let json =
        """{ "version": 1, "tools": { "paket": { "version": "10.3.1", "commands": ["paket"] } } }"""

    test <@ parseManifest "/r/m.json" json = Error(PinError.PinMissing "/r/m.json") @>
    test <@ parseManifest "/r/m.json" """{ "version": 1 }""" = Error(PinError.PinMissing "/r/m.json") @>

[<Fact>]
let ``parseManifest returns ManifestUnreadable for malformed JSON and a versionless entry`` () =
    match parseManifest "/r/m.json" "{ not json" with
    | Error(PinError.ManifestUnreadable("/r/m.json", _)) -> ()
    | other -> failwith $"expected ManifestUnreadable, got %A{other}"

    match parseManifest "/r/m.json" """{ "tools": { "fantomas": { "commands": ["fantomas"] } } }""" with
    | Error(PinError.ManifestUnreadable("/r/m.json", reason)) -> test <@ reason.Contains "version" @>
    | other -> failwith $"expected ManifestUnreadable, got %A{other}"

[<Fact>]
let ``readPin names the manifest it could not find`` () =
    withTempDir "fantomas-nopin" (fun dir ->
        let expected = Path.Combine(dir, ".config", "dotnet-tools.json")
        test <@ readPin dir = Error(PinError.ManifestMissing expected) @>
        // The rendered reason carries the path AND the remedy.
        let rendered = PinError.render (PinError.ManifestMissing expected)
        test <@ rendered.Contains expected @>
        test <@ rendered.Contains "dotnet tool install fantomas" @>)

[<Fact>]
let ``readPin returns ManifestUnreadable, with the reason, for a manifest it cannot open`` () =
    withTempDir "fantomas-unreadable" (fun dir ->
        let manifest = Path.Combine(dir, ".config", "dotnet-tools.json")
        Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore
        File.WriteAllText(manifest, manifestWith "7.0.5")
        File.SetUnixFileMode(manifest, UnixFileMode.None)

        try
            match readPin dir with
            | Error(PinError.ManifestUnreadable(path, reason)) ->
                test <@ path = manifest @>
                test <@ reason.Contains "UnauthorizedAccessException" @>
                let rendered = PinError.render (PinError.ManifestUnreadable(path, reason))
                test <@ rendered.Contains manifest && rendered.Contains "UnauthorizedAccessException" @>
            | other -> failwith $"expected ManifestUnreadable, got %A{other}"
        finally
            File.SetUnixFileMode(manifest, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

[<Fact>]
let ``PinError.render names the manifest and the install command for a missing pin`` () =
    let rendered = PinError.render (PinError.PinMissing "/r/.config/dotnet-tools.json")
    test <@ rendered.Contains "/r/.config/dotnet-tools.json" @>
    test <@ rendered.Contains "dotnet tool install fantomas" @>

[<Fact>]
let ``ToolFailure.render names the exit code and output of a failed tool`` () =
    let rendered = ToolFailure.render (ToolFailure.Failed(2, "boom"))
    test <@ rendered = "dotnet fantomas exited 2: boom" @>

[<Fact>]
let ``readPin reads the repository manifest and describe names version and file`` () =
    withTempDir "fantomas-pin" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".config")) |> ignore
        File.WriteAllText(Path.Combine(dir, ".config", "dotnet-tools.json"), manifestWith "7.0.5")

        match readPin dir with
        | Ok p ->
            test <@ p.Version = "7.0.5" @>
            test <@ describe dir p = "dotnet fantomas 7.0.5 (pinned in .config/dotnet-tools.json)" @>
        | Error e -> failwith $"expected a pin, got %A{e}")

// =============================================================================
// The tool's text
// =============================================================================

[<Fact>]
let ``parseOutput maps needs-formatting lines back to the requested absolute paths`` () =
    let a = "/w/Bad.fs"
    let b = "/w/sub/Sub.fs"
    let good = "/w/Good.fs"

    let report =
        parseOutput "/w" [ a; b; good ] "/w/Bad.fs needs formatting\n/w/sub/Sub.fs needs formatting\n"

    test <@ report.NeedsFormatting = [ a; b ] @>
    test <@ List.isEmpty report.FormatErrors @>

[<Fact>]
let ``parseOutput resolves a workDir-relative echo`` () =
    // The tool echoes paths as given; a relative invocation gets relative echoes.
    let a = Path.Combine(Path.GetTempPath(), "w", "Bad.fs") |> Path.GetFullPath
    let workDir = Path.Combine(Path.GetTempPath(), "w") |> Path.GetFullPath
    let report = parseOutput workDir [ a ] "Bad.fs needs formatting"
    test <@ report.NeedsFormatting = [ a ] @>

[<Fact>]
let ``parseOutput ignores lines about files that were not requested`` () =
    let report =
        parseOutput "/w" [ "/w/A.fs" ] "/w/Other.fs needs formatting\nsome noise\n"

    test <@ List.isEmpty report.NeedsFormatting @>

[<Fact>]
let ``parseOutput extracts a per-file format error with its first line of reason`` () =
    let broken = "/w/Broken.fs"

    let text =
        String.concat
            "\n"
            [ "error: Failed to format /w/Broken.fs: Fantomas.Core.ParseException: ParseException"
              "  [{ Severity = Error"
              "     SubCategory = \"parse\" }]"
              "   at Fantomas.Core.CodeFormatterImpl.parse@24.Invoke(Unit unitVar)"
              "/w/Broken.fs needs formatting"
              "" ]

    let report = parseOutput "/w" [ broken; "/w/Good.fs" ] text
    test <@ report.FormatErrors = [ broken, "Fantomas.Core.ParseException: ParseException" ] @>
    // The tool also lists a file it could not parse as needing formatting; both facts survive.
    test <@ report.NeedsFormatting = [ broken ] @>

[<Fact>]
let ``parseOutput reads the rewrite-mode error line, which spells the path differently`` () =
    // `dotnet tool run fantomas <files>` (no `--check`) reports a parse failure as
    // `Failed to format file: <path> : <reason>` — a `file:` prefix and a space before
    // the colon that the `--check` line does not have. Observed with fantomas 7.0.5.
    let broken = "/w/sub/Broken.fs"

    let text =
        String.concat
            "\n"
            [ "  Formatted │ 0 │ Ignored │ 0 │ Unchanged │ 1 │ Errored │ 1  "
              "Failed to format file: /w/sub/Broken.fs : Could not parse file."
              "" ]

    let report = parseOutput "/w" [ broken; "/w/Good.fs" ] text
    test <@ report.FormatErrors = [ broken, "Could not parse file." ] @>
    test <@ List.isEmpty report.NeedsFormatting @>

    // And relative, as the tool echoes a path given relative to the working directory.
    let relative =
        parseOutput "/w" [ broken ] "Failed to format file: sub/Broken.fs : Could not parse file."

    test <@ relative.FormatErrors = [ broken, "Could not parse file." ] @>

// =============================================================================
// check: exit codes → facts
// =============================================================================

[<Fact>]
let ``check hands the pin, --check and every quoted path to the runner, from the repo root`` () =
    let runner, calls = scripted (Succeeded(drained ""))
    let files = [ "/w/src/A.fs"; "/w/src/B C.fs" ]

    let result = check runner pin "/w" (TimeSpan.FromSeconds 7.0) files

    test
        <@
            result = Ok
                { NeedsFormatting = []
                  FormatErrors = [] }
        @>

    test <@ calls () = [ pin, "--check \"/w/src/A.fs\" \"/w/src/B C.fs\"", "/w", TimeSpan.FromSeconds 7.0 ] @>

[<Fact>]
let ``check with no files never spawns`` () =
    let runner, calls = scripted (Succeeded(drained ""))

    test
        <@
            check runner pin "/w" (TimeSpan.FromSeconds 1.0) [] = Ok
                { NeedsFormatting = []
                  FormatErrors = [] }
        @>

    test <@ List.isEmpty (calls ()) @>

[<Fact>]
let ``check reads exit 99 as needs-formatting for the named files`` () =
    let runner, _ =
        scripted (Failed(NeedsFormattingExitCode, drained "/w/A.fs needs formatting"))

    let result =
        check runner pin "/w" (TimeSpan.FromSeconds 1.0) [ "/w/A.fs"; "/w/B.fs" ]

    test <@ result |> Result.map (fun r -> r.NeedsFormatting) = Ok [ "/w/A.fs" ] @>

[<Fact>]
let ``check reads an unrestored pin as the typed NotRestored failure`` () =
    let sdkText =
        "Run \"dotnet tool restore\" to make the \"fantomas\" command available."

    let runner, _ = scripted (Failed(1, drained sdkText))

    match check runner pin "/w" (TimeSpan.FromSeconds 1.0) [ "/w/A.fs" ] with
    | Error(ToolFailure.NotRestored(p, output)) ->
        test <@ p = pin @>
        test <@ output = sdkText @>
        let rendered = ToolFailure.render (ToolFailure.NotRestored(p, output))
        test <@ rendered.Contains "7.0.5" @>
        test <@ rendered.Contains "dotnet tool restore" @>
    | other -> failwith $"expected NotRestored, got %A{other}"

[<Fact>]
let ``check reads exit 1 with per-file errors as findings, not as a tool failure`` () =
    let runner, _ =
        scripted (
            Failed(1, drained "error: Failed to format /w/Broken.fs: ParseException\n/w/Broken.fs needs formatting")
        )

    match check runner pin "/w" (TimeSpan.FromSeconds 1.0) [ "/w/Broken.fs"; "/w/Good.fs" ] with
    | Ok report ->
        test <@ report.FormatErrors = [ "/w/Broken.fs", "ParseException" ] @>
        test <@ report.NeedsFormatting = [ "/w/Broken.fs" ] @>
    | Error e -> failwith $"expected findings, got %A{e}"

[<Fact>]
let ``check reads exit 1 without per-file errors as the tool failing`` () =
    let runner, _ = scripted (Failed(1, drained "Unhandled exception: something else"))

    match check runner pin "/w" (TimeSpan.FromSeconds 1.0) [ "/w/A.fs" ] with
    | Error(ToolFailure.Failed(1, output)) -> test <@ output.Contains "something else" @>
    | other -> failwith $"expected Failed, got %A{other}"

[<Fact>]
let ``check reads a killed child as TimedOut`` () =
    let runner, _ =
        scripted (
            TimedOut(
                TimeSpan.FromSeconds 1.0,
                ProcessOutput.DrainTimedOut("", TimeSpan.FromSeconds 2.0),
                KillOutcome.Killed
            )
        )

    match check runner pin "/w" (TimeSpan.FromSeconds 1.0) [ "/w/A.fs" ] with
    | Error(ToolFailure.TimedOut(after, KillOutcome.Killed)) -> test <@ after = TimeSpan.FromSeconds 1.0 @>
    | other -> failwith $"expected TimedOut, got %A{other}"

[<Fact>]
let ``check chunks a large batch into several invocations and folds the reports`` () =
    let calls = ResizeArray<string>()

    let runner: Runner =
        fun _ args _ _ ->
            calls.Add args
            // Report the first path of every chunk as unformatted.
            let first = args.Split('"', StringSplitOptions.RemoveEmptyEntries).[1]
            Failed(NeedsFormattingExitCode, drained $"%s{first} needs formatting")

    let files = [ for i in 1..450 -> $"/w/F%03d{i}.fs" ]

    match check runner pin "/w" (TimeSpan.FromSeconds 1.0) files with
    | Ok report ->
        test <@ calls.Count = 3 @>
        test <@ report.NeedsFormatting = [ "/w/F001.fs"; "/w/F201.fs"; "/w/F401.fs" ] @>
    | Error e -> failwith $"unexpected %A{e}"

// =============================================================================
// format: what changed is measured, not reported
// =============================================================================

[<Fact>]
let ``format returns exactly the files whose bytes the tool changed`` () =
    withTempDir "fantomas-format" (fun dir ->
        let a = Path.Combine(dir, "A.fs")
        let b = Path.Combine(dir, "B.fs")
        File.WriteAllText(a, "module A\nlet   x=1\n")
        File.WriteAllText(b, "module B\n\nlet x = 1\n")

        // A runner that rewrites A the way a formatter would, and leaves B alone.
        let runner: Runner =
            fun _ args _ _ ->
                test <@ args = $"\"%s{a}\" \"%s{b}\"" @>
                File.WriteAllText(a, "module A\n\nlet x = 1\n")
                Succeeded(drained "Formatted │ 1 │ Ignored │ 0 │ Unchanged │ 1 │ Errored │ 0")

        match format runner pin dir (TimeSpan.FromSeconds 1.0) [ a; b ] with
        | Ok report ->
            test <@ report.Modified = [ a ] @>
            test <@ List.isEmpty report.FormatErrors @>
        | Error e -> failwith $"unexpected %A{e}")

[<Fact>]
let ``format never reports a file it could not read before or after the run as modified`` () =
    withTempDir "fantomas-format-vanish" (fun dir ->
        let missing = Path.Combine(dir, "Missing.fs")
        let vanishing = Path.Combine(dir, "Vanishing.fs")
        File.WriteAllText(vanishing, "module V\n")

        // The tool deletes one file (a hypothetical, but the shape is "unreadable
        // after") and is handed one that never existed ("unreadable before").
        let runner: Runner =
            fun _ _ _ _ ->
                File.Delete vanishing
                Succeeded(drained "")

        match format runner pin dir (TimeSpan.FromSeconds 1.0) [ missing; vanishing ] with
        | Ok report -> test <@ List.isEmpty report.Modified @>
        | Error e -> failwith $"unexpected %A{e}")

[<Fact>]
let ``format with no files never spawns`` () =
    let runner, calls = scripted (Succeeded(drained ""))
    test <@ format runner pin "/w" (TimeSpan.FromSeconds 1.0) [] = Ok { Modified = []; FormatErrors = [] } @>
    test <@ List.isEmpty (calls ()) @>

// =============================================================================
// The config that decides the verdict is a cache-key input
// =============================================================================

[<Fact>]
let ``editorConfigInputs collects every editorconfig from the repo root down to the file`` () =
    withTempDir "fantomas-editorconfig" (fun dir ->
        let sub = Path.Combine(dir, "src", "deep")
        Directory.CreateDirectory sub |> ignore
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(dir, "src", ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let file = Path.Combine(sub, "A.fs")
        File.WriteAllText(file, "module A\n")

        let inputs = editorConfigInputs dir [ file ]
        let rootConfig = Path.Combine(dir, ".editorconfig")
        let srcConfig = Path.Combine(dir, "src", ".editorconfig")

        test
            <@
                inputs = [ $"editorconfig:%s{rootConfig}", "root = true\n"
                           $"editorconfig:%s{srcConfig}", "[*.fs]\nmax_line_length = 80\n" ]
            @>

        // A file that does not exist yet still resolves its directories' configs.
        test <@ editorConfigInputs dir [ Path.Combine(dir, "src", "B.fs") ] |> List.length = 2 @>

        // No config files, no inputs.
        withTempDir "fantomas-noeditorconfig" (fun bare ->
            test <@ List.isEmpty (editorConfigInputs bare [ Path.Combine(bare, "A.fs") ]) @>)

        // A file OUTSIDE the root walks to the filesystem root and stops there, and
        // never reports this repository's configs.
        withTempDir "fantomas-outside" (fun elsewhere ->
            let outside = editorConfigInputs dir [ Path.Combine(elsewhere, "B.fs") ]
            test <@ outside |> List.forall (fun (label, _) -> not (label.Contains dir)) @>))
