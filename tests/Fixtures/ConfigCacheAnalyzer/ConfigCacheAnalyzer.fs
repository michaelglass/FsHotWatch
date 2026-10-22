/// A test fixture analyzer whose configuration outlives the configuration it was
/// built from, unless its host gives it a fresh load context.
///
/// It reads the nearest `.editorconfig` and `fsharplint.json` above the analyzed file
/// and caches what it found in a STATIC, keyed by directory, and never looks again.
/// That is the shape of the FSharpLint shim's `configCache` that turned a downstream
/// gate red: a warm daemon saw the config change (its cache key moved), re-ran the
/// analyzer, and the analyzer answered from the config it had cached before the edit.
///
/// Every finding names the load context it ran in and a per-load id, so a test can
/// tell which analyzer path was reloaded and which was not.
module ConfigCacheAnalyzer.Analyzer

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Runtime.Loader
open FSharp.Analyzers.SDK
open FSharp.Compiler.Text

/// Minted once per load of this assembly: a fresh load context, a fresh id.
let private loadId = Guid.NewGuid().ToString("N")

/// (config file name, directory) -> the content found for it, cached forever.
let private configCache = ConcurrentDictionary<string * string, string>()

let rec private nearest (fileName: string) (dir: DirectoryInfo) : string =
    if isNull dir then
        "none"
    else
        let candidate = Path.Combine(dir.FullName, fileName)

        if File.Exists candidate then
            (File.ReadAllText candidate).Trim()
        else
            nearest fileName dir.Parent

let private configFor (fileName: string) (analyzedFile: string) : string =
    let dir = Path.GetDirectoryName(Path.GetFullPath analyzedFile)

    configCache.GetOrAdd((fileName, dir), fun (name, d) -> nearest name (DirectoryInfo d))

let private contextName () : string =
    match AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()) with
    | null -> "none"
    | context -> context.Name

[<CliAnalyzer("ConfigCacheAnalyzer", "Reports the configuration it cached for the analyzed file")>]
let configCacheAnalyzer: Analyzer<CliContext> =
    fun (ctx: CliContext) ->
        async {
            let editorConfig = configFor ".editorconfig" ctx.FileName
            let fsharpLint = configFor "fsharplint.json" ctx.FileName

            let text =
                $"context=%s{contextName ()} load=%s{loadId} editorconfig=%s{editorConfig} fsharplint=%s{fsharpLint}"

            return
                [ { Type = "ConfigCacheAnalyzer"
                    Message = text
                    Code = "CFG001"
                    Severity = Severity.Warning
                    Range = Range.mkRange ctx.FileName (Position.mkPos 1 0) (Position.mkPos 1 0)
                    Fixes = [] } ]
        }
