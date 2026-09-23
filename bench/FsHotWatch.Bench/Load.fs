/// The box's load when a sample was taken, and whether that makes the sample
/// comparable.
///
/// ADR-003 watched the identical binary measure 6.1 GB and 4.2 GB peak in two runs as
/// load swung 33→273. A number taken on a contended box is not comparable with one
/// taken on a quiet box, so every record carries the load it was taken under, and the
/// summary refuses to compare contended records unless told to.
///
/// Contention is judged BEFORE the harness starts its own daemons (their scans load
/// the box by design) plus, at every sample, whether any daemon the harness did not
/// start is alive.
module FsHotWatch.Bench.Load

open System
open System.Globalization
open System.Text.RegularExpressions

/// One reading of the box.
type Snapshot =
    {
        Load1: float
        Load5: float
        Load15: float
        Cpus: int
        MemBytes: int64
        /// `memory_pressure -Q` "System-wide memory free percentage".
        MemFreePercent: int option
        /// `vm.swapusage` used, bytes.
        SwapUsedBytes: int64 option
        /// Pids of FsHotWatch daemons alive that this harness did not start.
        ForeignDaemons: int list
        /// `pmset -g batt`'s "Now drawing from '…'": `AC Power` or `Battery Power`.
        /// `None` when unreadable (no battery report), which is not contention.
        PowerSource: string option
    }

/// Parse `sysctl -n vm.loadavg`: `{ 37.03 116.03 149.15 }`.
let parseLoadAvg (text: string) : (float * float * float) option =
    let parts =
        text.Trim().Trim('{', '}').Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

    let num (s: string) =
        match Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    match parts |> Array.map num with
    | [| Some a; Some b; Some c |] -> Some(a, b, c)
    | _ -> None

let private freePct = Regex(@"free percentage:\s*(?<p>\d+)%", RegexOptions.Compiled)

/// Parse `memory_pressure -Q`'s "System-wide memory free percentage: 68%".
let parseMemFreePercent (text: string) : int option =
    let m = freePct.Match(text)
    if m.Success then Some(int m.Groups.["p"].Value) else None

let private swapUsed =
    Regex(@"used = (?<n>[\d.]+)(?<u>[KMG])", RegexOptions.Compiled)

/// Parse `sysctl -n vm.swapusage`'s `used = 6290.81M` into bytes.
let parseSwapUsed (text: string) : int64 option =
    let m = swapUsed.Match(text)

    if not m.Success then
        None
    else
        let n = Double.Parse(m.Groups.["n"].Value, CultureInfo.InvariantCulture)

        let scale =
            match m.Groups.["u"].Value with
            | "K" -> 1024.0
            | "M" -> 1024.0 * 1024.0
            | _ -> 1024.0 * 1024.0 * 1024.0

        Some(int64 (n * scale))

let private drawingFrom =
    Regex(@"Now drawing from '(?<src>[^']+)'", RegexOptions.Compiled)

/// Parse `pmset -g batt`'s power source line.
let parsePowerSource (text: string) : string option =
    let m = drawingFrom.Match(text)
    if m.Success then Some m.Groups.["src"].Value else None

/// Thresholds for calling the box quiet.
type QuietBar =
    {
        /// 1-minute load average ceiling, as a fraction of the CPU count.
        MaxLoadPerCpu: float
        /// Minimum system-wide free-memory percentage.
        MinMemFreePercent: int
    }

/// The default bar: load below half the cores, at least 30% of memory free, and no
/// daemon the harness did not start.
let defaultBar =
    { MaxLoadPerCpu = 0.5
      MinMemFreePercent = 30 }

/// Why a snapshot is contended; empty means quiet. Battery power is contention under
/// any bar: a laptop on battery throttles and sleeps, and either one invalidates a
/// latency or a footprint taken across it.
let contention (bar: QuietBar) (snap: Snapshot) : string list =
    [ if snap.Load1 > bar.MaxLoadPerCpu * float snap.Cpus then
          $"load1 %.1f{snap.Load1} > %.1f{bar.MaxLoadPerCpu * float snap.Cpus} (%.2f{bar.MaxLoadPerCpu} x %d{snap.Cpus} cpus)"
      match snap.MemFreePercent with
      | Some p when p < bar.MinMemFreePercent -> $"memory free %d{p}%% < %d{bar.MinMemFreePercent}%%"
      | _ -> ()
      if not (List.isEmpty snap.ForeignDaemons) then
          let pids = snap.ForeignDaemons |> List.map string |> String.concat ","
          $"%d{List.length snap.ForeignDaemons} foreign fshw daemon(s) alive: %s{pids}"
      match snap.PowerSource with
      | Some source when source <> "AC Power" -> $"on %s{source}, not AC: throttling and sleep invalidate latency"
      | _ -> () ]
