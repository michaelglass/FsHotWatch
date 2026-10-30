/// The record a daemon leaves when it is shut down while it still owns work.
///
/// `fshw stop`, a signal and a `.fshw.json` edit all end a daemon by cancelling it, and
/// cancelling a build kills the compiler wherever it was. What it was writing stays
/// half-written — a 132-byte assembly in `obj/`, a test output directory missing one
/// copy — and nothing on disk said why. The agents that met that damage read it as an
/// out-of-memory kill, or as their own daemon dying unprompted.
///
/// So the daemon writes `.fshw/interrupted.json` at the instant it is cancelled, naming
/// the work it owned, and only when it owned some: an idle daemon that stops leaves
/// nothing behind. Three readers use it. `fshw stop` says what it interrupted. A `check`
/// whose daemon went away under it names the shutdown as its reason. And the next
/// daemon's first scan repairs the damage: it re-stamps every source it dispatches, so
/// that no output written by the killed work is newer than the sources it was built
/// from. A timestamp up-to-date check (MSBuild's, and fshw's artifact-freshness gate)
/// then rebuilds instead of trusting it. The record is removed once that is done.
module FsHotWatch.Interruption

open System
open System.IO
open System.Text.Json

/// What a daemon was doing when it was shut down.
type Record =
    {
        /// When the shutdown was requested (UTC).
        At: DateTime
        /// The daemon's process id.
        Pid: int
        /// The work it owned at that instant: plugin names, and `scan` for a scan.
        InFlight: string list
    }

/// `.fshw/interrupted.json`.
let path (repoRoot: string) : string =
    Path.Combine(FsHwPaths.root repoRoot, "interrupted.json")

/// One sentence naming the shutdown as the cause, for a log line or a verdict reason.
let describe (record: Record) : string =
    let work = String.concat ", " record.InFlight
    let at = record.At.ToString("o")

    $"the daemon (pid %d{record.Pid}) was shut down at %s{at} while %s{work} was running — \
      a requested stop (`fshw stop`, a signal or a .fshw.json edit), not a crash or an out-of-memory kill. \
      Output that work was writing may be incomplete; the next scan re-stamps the sources so the next \
      build replaces it"

/// Write the record when the daemon owned work. Never throws: the daemon is stopping.
/// A root that has gone is not recreated to hold it.
let record (repoRoot: string) (record: Record) : unit =
    if not record.InFlight.IsEmpty && Directory.Exists repoRoot then
        try
            let json =
                JsonSerializer.Serialize
                    {| at = record.At.ToUniversalTime()
                       pid = record.Pid
                       inFlight = record.InFlight |}

            FsHwPaths.atomicWriteAllText (path repoRoot) json
        with ex ->
            Logging.warn "daemon" $"could not record the interrupted shutdown: %s{ex.GetType().Name}: %s{ex.Message}"

/// The record the last interrupted shutdown left, if any. `None` when absent or unreadable.
let tryRead (repoRoot: string) : Record option =
    try
        use doc = JsonDocument.Parse(File.ReadAllText(path repoRoot))
        let root = doc.RootElement

        Some
            { At = root.GetProperty("at").GetDateTime().ToUniversalTime()
              Pid = root.GetProperty("pid").GetInt32()
              InFlight =
                root.GetProperty("inFlight").EnumerateArray()
                |> Seq.map (fun name -> name.GetString())
                |> Seq.toList }
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> None
    | ex ->
        Logging.debug "daemon" $"could not read the interruption record: %s{ex.GetType().Name}: %s{ex.Message}"
        None

/// The record, when it describes a shutdown at or after `since`.
let since (since: DateTime) (record: Record option) : Record option =
    record |> Option.filter (fun r -> r.At >= since.ToUniversalTime())

/// Re-stamp `files` to now and remove the record, when one is owed. Returns the record
/// that was repaired. A file that cannot be re-stamped is logged and skipped.
let repair (repoRoot: string) (files: string list) : Record option =
    match tryRead repoRoot with
    | None -> None
    | Some owed ->
        let now = DateTime.UtcNow

        for file in files do
            try
                if File.Exists file then
                    File.SetLastWriteTimeUtc(file, now)
            with ex ->
                Logging.warn "scan" $"could not re-stamp %s{file} after an interrupted shutdown: %s{ex.Message}"

        try
            File.Delete(path repoRoot)
        with ex ->
            Logging.warn "scan" $"could not clear the interruption record: %s{ex.GetType().Name}: %s{ex.Message}"

        Some owed
