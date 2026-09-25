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
        /// (repo-relative path, SHA-256 or "missing") per configured fingerprint file.
        ConfigFiles: (string * string) list
        /// (variable name, SHA-256 of the value or "unset") per configured variable.
        /// The value itself is never stored.
        ConfigEnv: (string * string) list
    }

let private sha256Hex (bytes: byte[]) =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// Lowercase SHA-256 hex of the inputs' canonical JSON. Configured files and variables are
/// sorted, so the order they were configured in does not matter.
let compute (i: Inputs) : string =
    let pairs (xs: (string * string) list) =
        xs |> List.sort |> List.map (fun (k, v) -> k + "=" + v) |> List.toArray

    // Anonymous-record fields serialize in a fixed (alphabetical) order.
    JsonSerializer.Serialize(
        {| runtime = i.Runtime
           os = i.Os
           arch = i.Arch
           deps = i.DepsJsonSha256
           recorder = i.RecorderVersion
           weaver = i.WeaverVersion
           hashScheme = i.HashScheme
           files = pairs i.ConfigFiles
           env = pairs i.ConfigEnv |}
    )
    |> Encoding.UTF8.GetBytes
    |> sha256Hex

/// SHA-256 hex of a repo-relative file's bytes, or "missing" when it does not exist.
let hashFile (repoRoot: string) (relPath: string) : string =
    let p = Path.Combine(repoRoot, relPath)

    if File.Exists p then
        sha256Hex (File.ReadAllBytes p)
    else
        "missing"

let private version (asm: Reflection.Assembly) = asm.GetName().Version.ToString 3

/// Gather the inputs for one traced launch: the runtime from its main process's dump, the
/// original deps.json hash, this weaver's and recorder's versions, core's schema version as
/// the hash scheme, and the configured files and environment variables.
let gather
    (repoRoot: string)
    (files: string list)
    (env: string list)
    (dump: ProcessDump)
    (depsJsonSha256: string)
    : Inputs =
    { Runtime = dump.Runtime
      Os = dump.Os
      Arch = dump.Arch
      DepsJsonSha256 = depsJsonSha256
      RecorderVersion = version typeof<TestPrune.Trace.Recorder.Probes>.Assembly
      WeaverVersion = version typeof<Inputs>.Assembly
      // Any change to what a content hash means bumps core's SchemaVersion (stored hashes
      // are recomputed), so it is the hash-scheme number: traces from an older scheme
      // become a fingerprint mismatch, never a spurious "still verifies".
      HashScheme = TestPrune.Database.SchemaVersion
      ConfigFiles = files |> List.map (fun f -> f, hashFile repoRoot f)
      ConfigEnv =
        env
        |> List.map (fun n ->
            n,
            match Environment.GetEnvironmentVariable n with
            | null -> "unset"
            | v -> sha256Hex (Encoding.UTF8.GetBytes v)) }
