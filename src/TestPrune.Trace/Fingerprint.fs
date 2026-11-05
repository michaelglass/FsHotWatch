/// The environment fingerprint E: which environment a trace was recorded in. A stored trace
/// is only ever read back under the fingerprint it was recorded with, so anything that can
/// change what a test executes without changing the repository's sources belongs here.
module TestPrune.Trace.Fingerprint

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open TestPrune.Trace.Model

/// Everything folded into a fingerprint.
type Inputs =
    {
        /// The runtime description the recorder reported.
        Runtime: string
        Os: string
        Arch: string
        /// SHA-256 of the test app's original (unwoven) deps.json.
        DepsJsonSha256: string
        RecorderVersion: string
        WeaverVersion: string
        /// What a stored content hash means; TestPrune.Core's `SchemaVersion`.
        HashScheme: int
        /// How the test assembly itself was woven. `Full` probes the test assembly's own
        /// methods, which `SitesOnly` does not see, so a trace recorded under one mode says
        /// nothing about what the other would record.
        WeaveTests: WeaveMode
        /// (repo-relative path, SHA-256 or "missing") per configured fingerprint file.
        ConfigFiles: (string * string) list
        /// (variable name, SHA-256 of the value or "unset") per configured variable.
        /// The value itself is never stored.
        ConfigEnv: (string * string) list
    }

/// The canonical JSON shape of a fingerprint. Fields are declared in alphabetical order
/// with the serialized names, so the JSON is stable and field order is explicit.
type CanonicalFingerprint =
    { arch: string
      deps: string
      env: string[]
      files: string[]
      hashScheme: int
      os: string
      recorder: string
      runtime: string
      weaveTests: string
      weaver: string }

let private sha256Hex (bytes: byte[]) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// The canonical JSON a fingerprint hashes. Configured files and variables are sorted, so the
/// order they were configured in does not matter.
let toJson (i: Inputs) : string =
    let pairs (xs: (string * string) list) =
        xs |> List.sort |> List.map (fun (k, v) -> k + "=" + v) |> List.toArray

    JsonSerializer.Serialize(
        { arch = i.Arch
          deps = i.DepsJsonSha256
          env = pairs i.ConfigEnv
          files = pairs i.ConfigFiles
          hashScheme = i.HashScheme
          os = i.Os
          recorder = i.RecorderVersion
          runtime = i.Runtime
          weaveTests =
            match i.WeaveTests with
            | SitesOnly -> "sites"
            | Full -> "full"
          weaver = i.WeaverVersion }
        : CanonicalFingerprint
    )

/// Lowercase SHA-256 hex of the inputs' canonical JSON (`toJson`).
let compute (i: Inputs) : string =
    toJson i |> Encoding.UTF8.GetBytes |> sha256Hex

/// The field names whose values differ between two fingerprints' canonical JSON (`toJson`),
/// sorted, a field only one side has among them: what a prediction miss names. The stored form
/// (`Store.FingerprintInputs`) is that JSON.
let diffJson (a: string) (b: string) : string list =
    let fields (json: string) =
        using (JsonDocument.Parse json) (fun doc ->
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.GetRawText())
            |> Map.ofSeq)

    let fa, fb = fields a, fields b

    Set.union (fa.Keys |> Set.ofSeq) (fb.Keys |> Set.ofSeq)
    |> Set.filter (fun k -> fa.TryFind k <> fb.TryFind k)
    |> Set.toList

/// The canonical field names whose values differ between two fingerprints' inputs, sorted.
let diff (a: Inputs) (b: Inputs) : string list = diffJson (toJson a) (toJson b)

/// SHA-256 hex of a repo-relative file's bytes, or "missing" when it does not exist.
let hashFile (repoRoot: string) (relPath: string) : string =
    let p = Path.Combine(repoRoot, relPath)

    if File.Exists p then
        sha256Hex (File.ReadAllBytes p)
    else
        "missing"

let private version (asm: Reflection.Assembly) = asm.GetName().Version.ToString 3

/// The inputs of a launch that runs on `runtime`, `os` and `arch`: those, the original
/// deps.json hash, this weaver's and recorder's versions, core's schema version as the hash
/// scheme, the test assembly's weave mode, and the configured files and environment
/// variables. `gather` reads the three strings from a recorded dump; a prediction from
/// `RuntimeIdentity` and the resolved runtime.
let ofEnvironment
    (repoRoot: string)
    (files: string list)
    (env: string list)
    (runtime: string, os: string, arch: string)
    (depsJsonSha256: string)
    (weaveTests: WeaveMode)
    : Inputs =
    { Runtime = runtime
      Os = os
      Arch = arch
      DepsJsonSha256 = depsJsonSha256
      RecorderVersion = version typeof<TestPrune.Trace.Recorder.Probes>.Assembly
      WeaverVersion = version typeof<Inputs>.Assembly
      // Any change to what a content hash means bumps core's SchemaVersion (stored hashes
      // are recomputed), so it is the hash-scheme number: traces from an older scheme
      // become a fingerprint mismatch, never a spurious "still verifies".
      HashScheme = TestPrune.Database.SchemaVersion
      WeaveTests = weaveTests
      ConfigFiles = files |> List.map (fun f -> f, hashFile repoRoot f)
      ConfigEnv =
        env
        |> List.map (fun n ->
            n,
            match Environment.GetEnvironmentVariable n with
            | null -> "unset"
            | v -> sha256Hex (Encoding.UTF8.GetBytes v)) }

/// Gather the inputs for one traced launch from its main process's dump (`ofEnvironment`).
let gather
    (repoRoot: string)
    (files: string list)
    (env: string list)
    (dump: ProcessDump)
    (depsJsonSha256: string)
    (weaveTests: WeaveMode)
    : Inputs =
    ofEnvironment repoRoot files env (dump.Runtime, dump.Os, dump.Arch) depsJsonSha256 weaveTests
