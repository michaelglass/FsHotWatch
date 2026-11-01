/// FCS project options and snapshots for a standalone script or a project's files: the
/// checker inputs `AstAnalyzer` analyses, independent of what it extracts from them.
module TestPrune.ScriptOptions

open System
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.Text

#nowarn "57" // Experimental snapshot API

/// Serializes concurrent GetProjectOptionsFromScript calls.
/// FCS has internal state corruption when script options are loaded concurrently.
let private scriptSemaphore = new SemaphoreSlim(1, 1)

/// Create a project snapshot from project options, using file modification times as version keys.
/// FCS uses version strings to skip re-checking files that haven't changed.
let createProjectSnapshot (projectOptions: FSharpProjectOptions) =
    let getFileSnapshot (_opts: FSharpProjectOptions) (fileName: string) =
        async {
            let version =
                if File.Exists(fileName) then
                    File.GetLastWriteTimeUtc(fileName).Ticks |> string
                else
                    "0"

            let getSource () =
                task {
                    let text =
                        if File.Exists(fileName) then
                            File.ReadAllText(fileName)
                        else
                            ""

                    return SourceTextNew.ofString text
                }

            return FSharpFileSnapshot.Create(fileName, version, getSource)
        }

    FSharpProjectSnapshot.FromOptions(projectOptions, getFileSnapshot)

/// Detect 'open' statements in source code to find cross-file dependencies.
let private detectOpenedModules (source: string) : string list =
    source.Split('\n')
    |> Array.map (fun line ->
        let trimmed = line.Trim()

        if trimmed.StartsWith("open ") then
            trimmed.Substring(5).Trim()
        else
            "")
    |> Array.filter (fun s -> not (String.IsNullOrEmpty(s)))
    |> Array.toList

/// Find script files in the same directory that match opened modules.
/// Returns both actual files on disk and hypothetical files that match the module names.
let private findRelatedScriptFiles (currentFile: string) (openedModules: string list) : string list =
    if List.isEmpty openedModules then
        []
    else
        let dirPath = Path.GetDirectoryName(currentFile)

        try
            let availableFiles =
                if Directory.Exists(dirPath) then
                    Directory.GetFiles(dirPath, "*.fsx")
                    |> Array.filter (fun f -> f <> currentFile)
                    |> Array.toList
                else
                    []

            let foundFiles =
                availableFiles
                |> List.filter (fun f ->
                    let fileName = Path.GetFileNameWithoutExtension(f)
                    openedModules |> List.exists (fun m -> m = fileName))

            // Paths for opened modules whose files don't exist on disk — sources analyzed
            // in memory.
            let hypotheticalFiles =
                openedModules
                |> List.map (fun m -> Path.Combine(dirPath, m + ".fsx"))
                |> List.filter (fun f -> f <> currentFile && not (List.contains f foundFiles))

            foundFiles @ hypotheticalFiles |> List.distinct
        with ex ->
            eprintfn $"  Warning: findRelatedScriptFiles failed: %s{ex.Message}"
            []

/// FCS GetProjectOptionsFromScript can return paths relative to the script location
/// rather than the process cwd. Normalise them so FCS can load assemblies regardless
/// of where the CLI was invoked.
let internal resolveToAbsolute (basePath: string) (path: string) =
    if String.IsNullOrEmpty(path) || Path.IsPathRooted(path) then
        path
    else
        Path.GetFullPath(Path.Combine(basePath, path))

/// -r: entries in OtherOptions are DLL reference paths that may be relative.
/// Other entries (e.g. --noframework) are compiler flags, not paths — leave them untouched.
let private resolveReferenceOptions (baseDir: string) (opts: string array) =
    opts
    |> Array.map (fun opt ->
        if opt.StartsWith("-r:", StringComparison.Ordinal) then
            let path = opt[3..]
            let resolved = resolveToAbsolute baseDir path

            if Object.ReferenceEquals(resolved, path) then
                opt
            else
                "-r:" + resolved
        else
            opt)

/// Convenience: create project options from a script source string.
/// Detects 'open' statements and includes related script files in the SourceFiles array.
let getScriptOptions (checker: FSharpChecker) (sourceFileName: string) (source: string) =
    async {
        let sourceText = SourceText.ofString source

        let! ct = Async.CancellationToken
        do! scriptSemaphore.WaitAsync(ct) |> Async.AwaitTask

        let! projOptions, _diagnostics =
            try
                checker.GetProjectOptionsFromScript(sourceFileName, sourceText, assumeDotNetFramework = false)
            finally
                scriptSemaphore.Release() |> ignore

        let openedModules = detectOpenedModules source
        let relatedFiles = findRelatedScriptFiles sourceFileName openedModules

        let enhancedSourceFiles =
            if List.isEmpty relatedFiles then
                projOptions.SourceFiles
            else
                (relatedFiles |> List.toArray)
                |> Array.append projOptions.SourceFiles
                |> Array.distinct

        let baseDir =
            match Path.GetDirectoryName(sourceFileName) with
            | null -> Directory.GetCurrentDirectory()
            | dir -> dir

        let enhancedOptions =
            { projOptions with
                SourceFiles = enhancedSourceFiles |> Array.map (resolveToAbsolute baseDir)
                OtherOptions = projOptions.OtherOptions |> resolveReferenceOptions baseDir }

        return enhancedOptions
    }
