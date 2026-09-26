/// The shadow bin: a woven copy of a test app's build output under
/// `<project>/bin/Traced/<tfm>/`, beside `bin/Debug/<tfm>/` so repository-root probing and
/// `AppContext.BaseDirectory`-relative paths behave the same. Every unchanged file is a
/// hardlink; woven files replace their link by rename, never by writing through it.
module TestPrune.Trace.ShadowBin

open System
open System.IO
open System.Reflection.Metadata
open System.Security.Cryptography
open System.Text.Json
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// What to shadow.
type ShadowRequest =
    {
        /// Repository root: an assembly whose PDB names a document under it is woven.
        RepoRoot: string
        /// The test project's directory (holding `bin/Debug/<tfm>/`).
        ProjectDir: string
        /// The test app's assembly name.
        AssemblyName: string
        /// Weave mode for the test assembly itself; every other repository assembly is `Full`.
        WeaveTests: WeaveMode
        /// The site passes to run (union-case/type probes, IO redirects…).
        Passes: Weaver.IWeavePass list
        /// How long JIT verification may take.
        VerifyTimeout: TimeSpan
    }

/// The JIT-verification report.
type VerifyReport =
    { Prepared: int
      Invalid: string list
      SkippedGeneric: int
      Other: int }

/// Why a project cannot be traced. A refused project runs untraced.
type ShadowRefusal =
    | NoBuildOutput of binDebug: string
    | AmbiguousTfm of dirs: string list
    | NoApphost of path: string
    | WeaveRefused of Weaver.WeaveError
    | RecorderVersionSkew of found: string * expected: string
    | DepsJsonUnreadable of reason: string
    | JitInvalid of methods: string list
    | VerifyFailed of exitCode: int * output: string

/// A prepared shadow bin.
type Shadow =
    {
        Dir: string
        Apphost: string
        /// Directory holding the weave's `manifest.tsv` and `documents.tsv`.
        ManifestDir: string
        Manifest: Manifest
        /// Content key of the weave inputs (cache directory name under `obj/traced`).
        WeaveKey: string
        /// True when the woven files came from the cache.
        Reused: bool
        OriginalDepsJsonSha256: string
        Verify: VerifyReport
    }

/// Name of the stamp file written into the shadow bin.
[<Literal>]
let StampName = ".testprune-trace.json"

/// The stamp file's JSON shape. Field names are the serialized names; the type is public
/// because System.Text.Json serializes a non-public F# record as `{}`.
type ShadowStamp =
    { weaveKey: string
      manifestDir: string
      ids: int
      verified: int
      invalid: int }

/// How many weave keys `obj/traced` keeps.
[<Literal>]
let KeptWeaveKeys = 3

/// One line per refusal, for logs.
let describeRefusal =
    function
    | NoBuildOutput d -> $"no build output under %s{d}"
    | AmbiguousTfm ds -> "more than one target framework holds the app: " + String.concat ", " ds
    | NoApphost p -> $"no apphost at %s{p}"
    | WeaveRefused e -> $"weave refused: %A{e}"
    | RecorderVersionSkew(found, expected) -> $"the app references recorder %s{found}; this weaver needs %s{expected}"
    | DepsJsonUnreadable why -> $"deps.json: %s{why}"
    | JitInvalid ms -> "woven IL failed JIT verification: " + String.concat ", " ms
    | VerifyFailed(code, out) -> $"JIT verification did not complete (exit %d{code}): %s{out}"

let private hex (bytes: byte[]) =
    Convert.ToHexString(bytes).ToLowerInvariant()

let private sha256File (path: string) =
    hex (SHA256.HashData(File.ReadAllBytes path))

let private sha256Text (s: string) =
    hex (SHA256.HashData(Text.Encoding.UTF8.GetBytes s))

/// Built from this repository: its portable PDB names at least one document under the root.
let private builtFromRepo (rootPrefix: string) (dll: string) =
    let pdb = Path.ChangeExtension(dll, ".pdb")

    File.Exists pdb
    && (try
            use fs = File.OpenRead pdb
            use p = MetadataReaderProvider.FromPortablePdbStream fs
            let r = p.GetMetadataReader()

            r.Documents
            |> Seq.exists (fun h -> r.GetString(r.GetDocument(h).Name).StartsWith(rootPrefix, StringComparison.Ordinal))
        with _ ->
            false)

let private readReport (path: string) =
    try
        use doc = JsonDocument.Parse(File.ReadAllText path)
        let r = doc.RootElement

        Some
            { Prepared = r.GetProperty("prepared").GetInt32()
              Invalid = [ for v in r.GetProperty("invalid").EnumerateArray() -> v.GetString() ]
              SkippedGeneric = r.GetProperty("skippedGeneric").GetInt32()
              Other = r.GetProperty("other").GetInt32() }
    with _ ->
        None

/// JIT-verify `touched` (assembly -> "<Type>::<method>") by launching `apphost` with the
/// recorder in `shadowDir` as its startup hook. The hook prepares every listed method in
/// the app's own runtime, writes `reportPath` and exits before Main.
let verify
    (shadowDir: string)
    (apphost: string)
    (touched: Map<string, string list>)
    (reportPath: string)
    (timeout: TimeSpan)
    : Result<VerifyReport, ShadowRefusal> =
    let listPath = reportPath + ".methods.tsv"

    File.WriteAllLines(
        listPath,
        [ for KeyValue(asm, keys) in touched do
              for k in keys -> asm + "\t" + k ]
    )

    File.Delete reportPath
    let recorder = Path.Combine(shadowDir, DepsJson.RecorderName + ".dll")

    let code, output =
        Launch.run
            apphost
            []
            [ "DOTNET_STARTUP_HOOKS", recorder
              Contract.VerifyEnv, reportPath
              Contract.VerifyAssembliesEnv, listPath ]
            shadowDir
            timeout

    match (if code = 0 then readReport reportPath else None) with
    | None -> Error(VerifyFailed(code, output))
    | Some report when report.Invalid.IsEmpty -> Ok report
    | Some report -> Error(JitInvalid report.Invalid)

/// A cache entry is usable only when a previous prepare finished it: woven files, a
/// readable manifest and a verify report that found nothing invalid.
let private tryCache (cacheDir: string) =
    let manifestDir = Path.Combine(cacheDir, "manifest")

    if File.Exists(Path.Combine(cacheDir, "done")) then
        match Manifest.read manifestDir, readReport (Path.Combine(cacheDir, "verify.json")) with
        | Ok manifest, Some report ->
            Some(Directory.GetFiles(Path.Combine(cacheDir, "woven"), "*.dll") |> List.ofArray, manifest, report)
        | _ -> None
    else
        None

/// Keep the `KeptWeaveKeys` most recently used weave keys (the current one always).
let private evict (tracedDir: string) (key: string) =
    Directory.SetLastWriteTimeUtc(Path.Combine(tracedDir, key), DateTime.UtcNow)

    for old in
        Directory.GetDirectories tracedDir
        |> Array.sortByDescending Directory.GetLastWriteTimeUtc
        |> Array.indexed
        |> Array.filter (fun (i, _) -> i >= KeptWeaveKeys)
        |> Array.map snd do
        Directory.Delete(old, true)

let private copyOver (shadowDir: string) (file: string) =
    HardLink.replaceWith (Path.Combine(shadowDir, Path.GetFileName file)) (fun tmp -> File.Copy(file, tmp))

let private weaveKey (req: ShadowRequest) (inputs: Weaver.WeaveInput list) =
    let version (t: Type) = t.Assembly.GetName().Version.ToString 3

    sha256Text (
        String.concat
            "\n"
            [ yield "weaver " + version typeof<Weaver.WeaveResult>
              yield "recorder " + version typeof<Probes>
              for p in req.Passes do
                  yield "pass " + p.GetType().FullName
              for i in inputs do
                  let pdb = Path.ChangeExtension(i.Path, ".pdb")
                  yield $"%s{Path.GetFileName i.Path}|%A{i.Mode}|%s{sha256File i.Path}|%s{sha256File pdb}" ]
    )

/// Build (or reuse) the shadow bin for `req`: mirror `bin/Debug/<tfm>`, weave every
/// assembly built from the repository (cached under `obj/traced/<weave key>`), inject the
/// recorder into deps.json (without its PDB, so it never enters the app's coverage),
/// JIT-verify a new weave and write the stamp.
let prepare (req: ShadowRequest) : Result<Shadow, ShadowRefusal> =
    let binDebug = Path.Combine(req.ProjectDir, "bin", "Debug")

    let tfmDirs =
        if Directory.Exists binDebug then
            Directory.GetDirectories binDebug
            |> Array.filter (fun d -> File.Exists(Path.Combine(d, req.AssemblyName + ".dll")))
            |> Array.sort
            |> List.ofArray
        else
            []

    match tfmDirs with
    | [] -> Error(NoBuildOutput binDebug)
    | _ :: _ :: _ -> Error(AmbiguousTfm tfmDirs)
    | [ sourceDir ] ->
        let shadowDir =
            Path.Combine(req.ProjectDir, "bin", "Traced", Path.GetFileName sourceDir)

        let apphost =
            Path.Combine(shadowDir, req.AssemblyName + (if OperatingSystem.IsWindows() then ".exe" else ""))

        HardLink.mirror sourceDir shadowDir |> ignore

        if not (File.Exists apphost) then
            Error(NoApphost apphost)
        else
            let rootPrefix =
                Path.TrimEndingDirectorySeparator(Path.GetFullPath req.RepoRoot)
                + string Path.DirectorySeparatorChar

            let inputs: Weaver.WeaveInput list =
                Directory.GetFiles(sourceDir, "*.dll")
                |> Array.filter (fun f ->
                    Path.GetFileNameWithoutExtension f <> DepsJson.RecorderName
                    && builtFromRepo rootPrefix f)
                |> Array.sort
                |> Array.map (fun f ->
                    { Weaver.WeaveInput.Path = f
                      Weaver.WeaveInput.Mode =
                        if Path.GetFileNameWithoutExtension f = req.AssemblyName then
                            req.WeaveTests
                        else
                            Full })
                |> List.ofArray

            let key = weaveKey req inputs
            let tracedDir = Path.Combine(req.ProjectDir, "obj", "traced")
            let cacheDir = Path.Combine(tracedDir, key)
            let manifestDir = Path.Combine(cacheDir, "manifest")
            let recorderVersion = typeof<Probes>.Assembly.GetName().Version.ToString 3

            let originalDeps =
                File.ReadAllText(Path.Combine(sourceDir, req.AssemblyName + ".deps.json"))

            let woven =
                match tryCache cacheDir with
                | Some(outputs, manifest, report) -> Ok(outputs, manifest, Choice1Of2 report)
                | None ->
                    match Weaver.weave req.Passes inputs (Path.Combine(cacheDir, "woven")) with
                    | Error e -> Error(WeaveRefused e)
                    | Ok r ->
                        Manifest.write manifestDir r.Manifest
                        Ok(r.Outputs, r.Manifest, Choice2Of2 r.Stats.Touched)

            let injected =
                match DepsJson.injectRecorder originalDeps req.AssemblyName recorderVersion with
                | Error e when e.StartsWith DepsJson.SkewPrefix ->
                    Error(RecorderVersionSkew(e.Substring DepsJson.SkewPrefix.Length, recorderVersion))
                | Error e -> Error(DepsJsonUnreadable e)
                | Ok(json, _) -> Ok json

            match woven, injected with
            | Error e, _
            | _, Error e -> Error e
            | Ok(outputs, manifest, verdict), Ok deps ->
                for dll in outputs do
                    copyOver shadowDir dll
                    copyOver shadowDir (Path.ChangeExtension(dll, ".pdb"))

                HardLink.replaceWith (Path.Combine(shadowDir, req.AssemblyName + ".deps.json")) (fun tmp ->
                    File.WriteAllText(tmp, deps))

                copyOver shadowDir typeof<Probes>.Assembly.Location
                // Never the recorder's PDB: MS CodeCoverage skips symbol-less modules, so the
                // recorder stays out of the app's coverage report.
                File.Delete(Path.Combine(shadowDir, DepsJson.RecorderName + ".pdb"))

                let verified =
                    match verdict with
                    | Choice1Of2 report -> Ok report
                    | Choice2Of2 touched ->
                        verify shadowDir apphost touched (Path.Combine(cacheDir, "verify.json")) req.VerifyTimeout

                match verified with
                | Error e -> Error e
                | Ok report ->
                    let reused =
                        match verdict with
                        | Choice1Of2 _ -> true
                        | Choice2Of2 _ ->
                            File.WriteAllText(Path.Combine(cacheDir, "done"), key)
                            false

                    let stamp: ShadowStamp =
                        { weaveKey = key
                          manifestDir = manifestDir
                          ids = manifest.IdCount
                          verified = report.Prepared
                          invalid = report.Invalid.Length }

                    HardLink.replaceWith (Path.Combine(shadowDir, StampName)) (fun tmp ->
                        File.WriteAllText(tmp, JsonSerializer.Serialize stamp))

                    evict tracedDir key

                    Ok
                        { Dir = shadowDir
                          Apphost = apphost
                          ManifestDir = manifestDir
                          Manifest = manifest
                          WeaveKey = key
                          Reused = reused
                          OriginalDepsJsonSha256 = sha256Text originalDeps
                          Verify = report }
