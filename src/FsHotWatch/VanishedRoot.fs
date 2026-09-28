/// A daemon whose worktree has been deleted has nothing left to serve, and ends.
///
/// Neither a detached per-worktree daemon nor a repository host's session is tied to
/// the process that started it, so neither can tell from its parent that it is no
/// longer wanted. A deleted root says so for either: a test's temporary repository
/// removed by its cleanup, a workspace forgotten and removed. Without this, such a
/// daemon watches a directory that no longer exists until someone kills it, and a host
/// holding such a session never goes idle.
///
/// Whether the root still exists is not the question: the daemon's own next write
/// (`.fshw/scan-metrics.jsonl`, its log) creates the directory again. The question is
/// whether the root is still the one the daemon started in, which a token only this
/// daemon wrote there answers.
module FsHotWatch.VanishedRoot

open System
open System.IO
open System.Threading

/// How often a running daemon looks for its root.
let DefaultCheckEvery = TimeSpan.FromSeconds 30.0

/// The file under `.fshw/` holding the running daemon's token.
[<Literal>]
let WitnessFile = "root-witness"

/// Mark `root` as this daemon's, and return whether it still is: true while the token
/// written now is still there. A root that cannot be marked is logged and always reads
/// as present, so a failure to mark never shuts a daemon down.
let mark (root: string) (log: string -> unit) : unit -> bool =
    let path = Path.Combine(FsHwPaths.root root, WitnessFile)
    let token = Guid.NewGuid().ToString "N"

    try
        if not (Directory.Exists root) then
            raise (DirectoryNotFoundException root)

        Directory.CreateDirectory(FsHwPaths.root root) |> ignore
        File.WriteAllText(path, token)

        fun () ->
            try
                File.ReadAllText path = token
            with _ ->
                false
    with ex ->
        log $"cannot mark %s{root} as this daemon's (%s{ex.Message}); deleting it will not end the daemon"
        fun () -> true

/// Every `every`, ask `present`; the first time it says the root is gone, log and call
/// `shutdown`, once. The caller owns the returned timer.
let watch (every: TimeSpan) (present: unit -> bool) (shutdown: unit -> unit) (log: string -> unit) : IDisposable =
    let fired = ref 0

    let tick (_: obj) =
        if not (present ()) && Interlocked.Exchange(&fired.contents, 1) = 0 then
            log "the worktree root this daemon started in is gone; shutting down"
            shutdown ()

    new Timer(TimerCallback tick, null, every, every) :> IDisposable
