/// A temp directory that something is WATCHING — and the only write shape a
/// test is allowed to perform while it is watched.
///
/// WHY A TYPE AND NOT ANOTHER HELPER. The flake this replaces is not a missing
/// helper (`probeLoop` has existed for months). It is an expressible sentence:
///
///     Thread.Sleep(100)                      // "give the watcher a moment"
///     File.WriteAllText(path, contents)      // ONE write
///     Assert.True(signal.Wait(5000), "…")    // fixed budget
///
/// On macOS a brand-new temp directory carries 4-20s of FSEvents cold-start
/// latency, and after a large initial batch fseventsd may coalesce for 15-30s
/// regardless of `kFSEventStreamCreateFlagNoDefer`. So the one write that
/// mattered can land before the watcher is live and then never be reported at
/// all — the test fails having proven nothing about the watcher. Two tests
/// flaked on exactly this on 2026-08-12.
///
/// `WatchedDir` deletes the sentence rather than discouraging it. While the
/// directory is watched the only mutation in scope is `WriteUntil`, which
///
///   * writes REPEATEDLY (via `TestHelpers.probeLoop`) until the event is seen,
///     so "write once and hope" cannot be expressed; and
///   * RETURNS whether it was seen, so "wait, then assert on something else"
///     cannot be expressed either — the wait itself is a value the test must
///     dispose of, which is the same reason `waitUntilTrue` exists beside
///     `waitUntil`.
///
/// PATHS. `UnwatchedDir` (the setup phase, before any watcher exists) does hand
/// out paths, because every production entry point under test takes one —
/// `watchConfigFile (dir.Seed(".fshw.json", "{}"))`. A plain single write is
/// honest there: nothing is listening yet, so no event can be missed. The
/// watched phase deliberately exposes NO path — it answers questions about
/// content (`ReadText`, `Exists`) instead of handing back a write handle. A
/// test that genuinely needs the absolute path while watching should capture
/// the one `Seed` returned; that keeps every unguarded write in the phase where
/// unguarded writes are correct.
module FsHotWatch.Tests.WatchedDir

open System
open System.IO
open FsHotWatch.Tests.TestHelpers

/// Probing budget. Generous on purpose: the loop exits as soon as the event
/// arrives, so this only bounds the pathological case (cold FSEvents plus a
/// saturated machine), and a too-small budget is precisely the fixed-deadline
/// assumption this fixture exists to remove.
[<Literal>]
let DefaultProbeTimeoutMs = 15000

/// The temp directory BEFORE anything watches it. Seed fixture files and build
/// the paths the production watcher needs here — single writes are correct in
/// this phase, and this type ceases to be reachable once the watcher is live.
[<Sealed>]
type UnwatchedDir internal (root: string) =

    /// The directory itself, for entry points that take a repo root
    /// (`watchRepoConfigFile dir.Root`).
    member _.Root = root

    /// Absolute path of `relative` inside the directory.
    member _.PathTo(relative: string) = Path.Combine(root, relative)

    /// Create (or overwrite) a file before the watcher exists, returning its
    /// absolute path — which is what the caller hands to the production watcher
    /// under test.
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
    /// `probeLoop`) until `observed ()` turns true or the budget expires.
    /// Returns whether it was observed — assert on the returned bool; there is
    /// no other way to learn whether the watcher ever fired.
    member this.WriteUntil(relative: string, contents: string, observed: unit -> bool, ?timeoutMs: int) : bool =
        this.WriteEachUntil(relative, (fun _ -> contents), observed, ?timeoutMs = timeoutMs)

    /// As `WriteUntil`, but with per-probe contents. Use this when the watcher
    /// under test dedups by CONTENT (the daemon's `ContentDedup.Tracker` does):
    /// identical bytes rewritten N times are one change to such a watcher, so
    /// repeated probing would be silently defeated and the fixture would report
    /// a timeout that the production code was right to produce.
    member this.WriteEachUntil
        (relative: string, contents: int -> string, observed: unit -> bool, ?timeoutMs: int)
        : bool =
        let path = this.Prepare relative

        probeLoop (fun n -> File.WriteAllText(path, contents n)) observed (defaultArg timeoutMs DefaultProbeTimeoutMs)

        observed ()

    /// Read a file's current contents — for asserting on what the watcher's
    /// callback wrote, without handing out a path that could be written to.
    member _.ReadText(relative: string) =
        File.ReadAllText(Path.Combine(root, relative))

    member _.Exists(relative: string) =
        File.Exists(Path.Combine(root, relative))

/// Run `body` against a watched temp directory.
///
/// `start` gets the directory before anything is watching it: seed files, then
/// return the live watcher (an `IDisposable` — `watchConfigFile` /
/// `watchRepoConfigFile` already are). It is disposed before the directory is
/// removed, so no callback can fire into a deleted tree.
///
/// Lifetime mirrors `withTempDir`: created here, deleted here (resiliently),
/// including when `body` throws.
let withWatchedDir (prefix: string) (start: UnwatchedDir -> IDisposable) (body: WatchedDir -> 'a) : 'a =
    withTempDir prefix (fun root ->
        use _watcher = start (UnwatchedDir root)
        body (WatchedDir root))
