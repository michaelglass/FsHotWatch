/// A run's CLAIM on this repo — the on-disk fact that a check is in flight RIGHT NOW,
/// written before the run starts and removed when it ends.
///
/// AUTOMATION-573. The verdict file is stamped only at COMPLETION. Mid-run it still
/// holds the PREVIOUS run's result and, when the tree has not moved, that result parses
/// cleanly, matches on `treeHash`, matches on producer, and reads GREEN. Only `command`
/// and `producedAt` separated "the run that finished" from "the run that is happening",
/// and comparing those two by eye is not a machine-readable answer — it is a puzzle, and
/// it has already been solved wrongly in practice. Under continuous verification, where
/// something is nearly always running, nearly every read is a read against an in-flight
/// run, so the stale green is the normal case rather than an edge one.
///
/// A claim is a FILE for the same reason the verdict is one (ADR-013): `fshw verdict`
/// touches no socket, starts no daemon and triggers no run, so `.fshw/` is the only
/// thing it may consult. Asking the daemon would make a read able to perturb what it
/// reads.
///
/// ONE FILE PER INVOCATION under `.fshw/in-flight/`, never a single shared file: two
/// concurrent checks in one workspace must not be able to release each other's claim,
/// and with a shared file the second claimant's release would clear the marker while the
/// first was still running — a hole in exactly the window the marker exists to cover.
module FsHotWatch.Cli.RunClaim

open System
open System.IO
open System.Text.Json
open FsHotWatch

/// The wire schema. Versioned like the verdict's, so a future shape change is a
/// DECLARED break rather than a silent misparse.
[<Literal>]
let Schema = "fshw-run-claim-v1"

/// One run's claim. Everything here exists to answer ONE of two questions: WHICH run
/// this is (so the verdict that run publishes is not mistaken for a previous one), and
/// whether the claim is still HELD (so a crashed run cannot poison the workspace
/// forever).
type Claim =
    {
        /// The invocation this claim belongs to — the same id the verdict records as
        /// `attribution.invocationId`. This is the whole join: a verdict carrying THIS id
        /// was published BY this run, and a verdict carrying any other id was published by
        /// an EARLIER one.
        InvocationId: string
        /// The process holding the claim. Liveness, not identity — see `isHeld`.
        Pid: int
        /// The machine that wrote it. A `.fshw/` reached from another host cannot have its
        /// pids probed, so a foreign claim is held until it is removed by its owner.
        Host: string
        /// `check` or `confirm` (`Verdict.Command.token`). Reported, never decided on.
        Command: string
        StartedAtUtc: DateTime
    }

/// The directory claims live in.
let dirPath (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "in-flight")

/// Repo-relative, because that is what the CLI PRINTS.
[<Literal>]
let RelativeDir = ".fshw/in-flight"

let private claimPath (repoRoot: string) (invocationId: string) : string =
    Path.Combine(dirPath repoRoot, invocationId + ".json")

let private jsonOptions = JsonSerializerOptions(WriteIndented = true)

/// Serialize a claim. Named so a test can pin the wire format without reaching into
/// the writer's file handling.
let serialize (c: Claim) : string =
    JsonSerializer.Serialize(
        {| schema = Schema
           invocationId = c.InvocationId
           pid = c.Pid
           host = c.Host
           command = c.Command
           startedAtUtc = c.StartedAtUtc.ToString("o") |},
        jsonOptions
    )

/// Strict parse. `None` for anything this build cannot read as a claim — an unknown
/// schema, a missing field, a malformed date.
///
/// UNPARSEABLE IS NOT ABSENT, and the caller must not treat it as such: `liveIn` folds a
/// `None` into a SENTINEL claim rather than dropping it, so a corrupt claim file still
/// says "a run is in flight" and cannot become a way to read a stale green. Parsing
/// decides only how much we can SAY about the run, never whether there is one.
let deserialize (json: string) : Claim option =
    try
        let root = JsonDocument.Parse(json).RootElement

        let str (name: string) =
            match root.TryGetProperty name with
            | true, el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
            | _ -> None

        let num (name: string) =
            match root.TryGetProperty name with
            | true, el when el.ValueKind = JsonValueKind.Number ->
                match el.TryGetInt32() with
                | true, v -> Some v
                | _ -> None
            | _ -> None

        match str "schema", str "invocationId", num "pid", str "host", str "command", str "startedAtUtc" with
        | Some schema, Some invocationId, Some pid, Some host, Some command, Some startedAt when schema = Schema ->
            match
                DateTime.TryParse(
                    startedAt,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.AdjustToUniversal
                )
            with
            | true, dt ->
                Some
                    { InvocationId = invocationId
                      Pid = pid
                      Host = host
                      Command = command
                      StartedAtUtc = dt }
            | _ -> None
        | _ -> None
    with :? JsonException ->
        None

/// What a claim file that exists but cannot be read reduces to. It says the ONE thing
/// that is still known — a run started and has not removed its marker — and says the
/// rest is unknown, rather than paraphrasing silence into "no run".
///
/// The file name goes in `Command`, because that is the field a reader is SHOWN
/// (`describe`), and "a claim I cannot read, in this file" is the whole of what there is
/// to say. `InvocationId` gets a value that no real invocation can collide with, so the
/// sentinel can never be mistaken for the run that published the verdict.
///
/// The empty `Host` is what makes it un-releasable by liveness: no machine matches, so
/// `isHeld` keeps it. It is cleared when its owner removes the file, or by hand.
let internal unreadableClaim (fileName: string) : Claim =
    { InvocationId = "unreadable:" + fileName
      Pid = 0
      Host = ""
      Command = "(unreadable claim in " + fileName + ")"
      StartedAtUtc = DateTime.MinValue }

/// Is this claim still HELD?
///
/// PURE, with liveness injected, so the policy is testable without spawning processes.
///
/// Every unknown leans HELD — a foreign host whose pids we cannot probe, a probe that
/// errored. This is the same lean as `daemonProcessAliveWith`, and here it is also the
/// fail-CLOSED direction: a claim wrongly kept costs a re-run, while a claim wrongly
/// released hands back the stale green this whole file exists to stop.
let internal isHeld (thisHost: string) (isAlive: int -> bool) (c: Claim) : bool =
    if not (String.Equals(c.Host, thisHost, StringComparison.OrdinalIgnoreCase)) then
        true
    else
        isAlive c.Pid

/// True unless the process is PROVABLY gone. Mirrors `Program.daemonProcessAliveWith`'s
/// probe: only "no process with that id" is proof; every other outcome is unknown, and
/// unknown means alive.
///
/// Note that pid 0 and pid 1 answer ALIVE on macOS — the probe finds real kernel/launchd
/// processes there. That is not a defect to work around: this asks "may I release this
/// claim?", and the answer for a pid that resolves to a running process is no.
let internal processAlive (pid: int) : bool =
    try
        not (Diagnostics.Process.GetProcessById(pid).HasExited)
    with
    | :? ArgumentException -> false
    | _ -> true

/// The claims held on this repo right now, from the files on disk.
///
/// PURE in its environment (host, liveness, and the raw file contents), so a test can
/// drive abandonment, corruption and foreign hosts without processes or clocks. Returns
/// the held claims AND the files whose claims are provably abandoned, so the caller can
/// do hygiene without a second pass deciding it again.
let internal liveIn
    (thisHost: string)
    (isAlive: int -> bool)
    (files: (string * string) list)
    : Claim list * string list =
    files
    |> List.fold
        (fun (held, abandoned) (path, contents) ->
            let claim =
                match deserialize contents with
                | Some c -> c
                | None -> unreadableClaim (Path.GetFileName path)

            if isHeld thisHost isAlive claim then
                claim :: held, abandoned
            else
                held, path :: abandoned)
        ([], [])
    |> fun (held, abandoned) -> List.rev held, List.rev abandoned

/// The claims held on this repo right now.
///
/// TOTAL and never throwing: a `.fshw/` that cannot be listed or read is reported as NO
/// claims. That is the one place this leans open, and deliberately — an unreadable
/// state directory already fails the verdict read itself (`Verdict.Reading.Unreadable`),
/// and turning it into a permanent "a run is in flight" would wedge the workspace with
/// no way out.
///
/// Provably-abandoned claim files are deleted as they are found, the same hygiene
/// `cleanStalePidfileWith` does for `daemon.pid`: cleaned up by the next command rather
/// than left for an external reaper.
let live (repoRoot: string) : Claim list =
    let dir = dirPath repoRoot

    try
        if not (Directory.Exists dir) then
            []
        else
            let files =
                Directory.GetFiles(dir, "*.json")
                |> Array.toList
                |> List.choose (fun path ->
                    try
                        Some(path, File.ReadAllText path)
                    with
                    // A file that vanished between listing and reading was released
                    // while we looked; a file we cannot read is folded into the
                    // unreadable sentinel by `liveIn`, so only true disappearance
                    // drops out here.
                    | :? FileNotFoundException
                    | :? DirectoryNotFoundException -> None
                    | :? IOException -> Some(path, "")
                    | :? UnauthorizedAccessException -> Some(path, ""))

            let held, abandoned = liveIn Environment.MachineName processAlive files

            for path in abandoned do
                try
                    File.Delete path
                with ex ->
                    Logging.debug "run-claim" $"could not delete an abandoned claim: %s{ex.Message}"

            held
    with ex ->
        Logging.debug "run-claim" $"could not read %s{RelativeDir}: %s{ex.Message}"
        []

/// Claim the repo for this invocation. Best-effort by design, exactly like publishing
/// the verdict: a repo whose `.fshw/` cannot be written must still get its exit code,
/// and a marker is an additional surface, never a new way to fail.
///
/// Returns the claim it wrote, or `None` when it could not write one — which the caller
/// passes straight back to `release`, so a failed claim releases nothing.
let acquire (repoRoot: string) (command: string) (invocationId: string) : Claim option =
    let claim =
        { InvocationId = invocationId
          Pid = Diagnostics.Process.GetCurrentProcess().Id
          Host = Environment.MachineName
          Command = command
          StartedAtUtc = DateTime.UtcNow }

    try
        Directory.CreateDirectory(dirPath repoRoot) |> ignore
        FsHwPaths.atomicWriteAllText (claimPath repoRoot invocationId) (serialize claim)
        Some claim
    with ex ->
        Logging.warn "run-claim" $"could not claim %s{RelativeDir}: %s{ex.Message}"
        None

/// Release this invocation's claim. Deletes only the file named by the claim's own
/// invocation id, so a concurrent run's claim is untouchable from here.
///
/// Idempotent and silent about an already-absent file: the caller latches this (a normal
/// return, an exception and a signal all reach it), and a second release must not warn.
let release (repoRoot: string) (claim: Claim option) : unit =
    match claim with
    | None -> ()
    | Some c ->
        try
            File.Delete(claimPath repoRoot c.InvocationId)
        with ex ->
            Logging.warn
                "run-claim"
                $"could not release this run's claim on %s{RelativeDir} — a later read will report a run in \
                  flight until its process is seen to have exited: %s{ex.Message}"

/// One line naming a claim, for the reason string a reader is handed. It names the run
/// well enough to go and look at it — which process, on which box, since when — without
/// the reader having to open `.fshw/in-flight/` to find out.
let describe (c: Claim) : string =
    // No host means nothing is known about the process — the `unreadableClaim` sentinel.
    // Printing its placeholder pid and its `DateTime.MinValue` would dress an absence up
    // as three facts, and a reader chasing "pid 0 on , started 00:54:00" is a reader we
    // sent somewhere that does not exist.
    if String.IsNullOrEmpty c.Host then
        c.Command
    else
        let started = c.StartedAtUtc.ToLocalTime().ToString("HH:mm:ss")
        $"%s{c.Command} (pid %d{c.Pid} on %s{c.Host}, started %s{started})"
