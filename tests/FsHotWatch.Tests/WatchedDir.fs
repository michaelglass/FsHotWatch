/// A temp directory that something is WATCHING — and the only write shape a
/// test is allowed to perform while it is watched.
///
/// On macOS a brand-new temp directory carries 4-20s of FSEvents cold-start
/// latency, and after a large initial batch fseventsd may coalesce for 15-30s
/// regardless of `kFSEventStreamCreateFlagNoDefer`. So "sleep briefly, write
/// once, wait a fixed budget" can miss the one write that mattered and fail
/// having proven nothing about the watcher.
///
/// While the directory is watched the only mutation in scope is `WriteUntil`,
/// which writes REPEATEDLY (via `TestHelpers.probeLoop`) until the event is seen
/// AND returns whether it was seen — so neither "write once and hope" nor "wait,
/// then assert on something else" is expressible.
///
/// `UnwatchedDir` (the setup phase, before any watcher exists) does hand out
/// paths, because every production entry point under test takes one —
/// `watchConfigFile (dir.Seed(".fshw.json", "{}"))`. A single write is honest
/// there: nothing is listening yet. The watched phase exposes NO path; it
/// answers questions about content (`ReadText`, `Exists`) instead. A test that
/// needs the absolute path while watching should capture the one `Seed`
/// returned.
module FsHotWatch.Tests.WatchedDir

open System
open System.IO
open FsHotWatch.Tests.TestHelpers

/// Probing budget. Generous on purpose: the loop exits as soon as the event
/// arrives, so this only bounds the pathological case (cold FSEvents on a
/// saturated machine).
[<Literal>]
let DefaultProbeTimeoutMs = 15000

/// The temp directory BEFORE anything watches it. Seed fixture files and build
/// the paths the production watcher needs here; this type stops being reachable
/// once the watcher is live.
[<Sealed>]
type UnwatchedDir internal (root: string) =

    /// The directory itself, for entry points that take a repo root
    /// (`watchRepoConfigFile dir.Root`).
    member _.Root = root

    /// Absolute path of `relative` inside the directory.
    member _.PathTo(relative: string) = Path.Combine(root, relative)

    /// Create (or overwrite) a file before the watcher exists, returning the
    /// absolute path the caller hands to the watcher under test.
    member this.Seed(relative: string, contents: string) : string =
        let path = this.PathTo relative
        Directory.CreateDirectory(Path.GetDirectoryName(path: string)) |> ignore
        File.WriteAllText(path, contents)
        path

/// The same temp directory, now WATCHED. `WriteUntil` / `WriteEachUntil` are
/// the whole mutable surface.
[<Sealed>]
type WatchedDir internal (root: string) =

    member private _.Prepare(relative: string) =
        let path = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName(path: string)) |> ignore
        path

    /// Write `contents` to `relative` REPEATEDLY (a fresh write every ~2s, via
    /// `probeLoop`) until `observed ()` turns true or the budget expires. Assert
    /// on the returned bool; there is no other way to learn whether the watcher
    /// fired.
    member this.WriteUntil(relative: string, contents: string, observed: unit -> bool, ?timeoutMs: int) : bool =
        this.WriteEachUntil(relative, (fun _ -> contents), observed, ?timeoutMs = timeoutMs)

    /// As `WriteUntil`, but with per-probe contents. Use this when the watcher
    /// under test dedups by CONTENT (the daemon's `ContentDedup.Tracker` does):
    /// identical bytes rewritten N times are one change to such a watcher, so
    /// repeated probing would be silently defeated and the fixture would report a
    /// timeout the production code was right to produce.
    member this.WriteEachUntil
        (relative: string, contents: int -> string, observed: unit -> bool, ?timeoutMs: int)
        : bool =
        let path = this.Prepare relative

        probeLoop (fun n -> File.WriteAllText(path, contents n)) observed (defaultArg timeoutMs DefaultProbeTimeoutMs)

        observed ()

    /// Read a file's current contents — assert on what the watcher's callback
    /// wrote, without handing out a writable path.
    member _.ReadText(relative: string) =
        File.ReadAllText(Path.Combine(root, relative))

    member _.Exists(relative: string) =
        File.Exists(Path.Combine(root, relative))

/// Run `body` against a watched temp directory.
///
/// `start` gets the directory before anything is watching it: seed files, then
/// return the live watcher (an `IDisposable` — `watchConfigFile` /
/// `watchRepoConfigFile` already are). It is disposed before the directory is
/// removed, so no callback can fire into a deleted tree. Lifetime mirrors
/// `withTempDir`, including when `body` throws.
let withWatchedDir (prefix: string) (start: UnwatchedDir -> IDisposable) (body: WatchedDir -> 'a) : 'a =
    withTempDir prefix (fun root ->
        use _watcher = start (UnwatchedDir root)
        body (WatchedDir root))
