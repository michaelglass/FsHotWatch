/// Opt-in per-test trace recording: the `tests.traces` settings from `.fshw.json`.
/// Parsed and carried here; nothing in this file launches or records anything.
namespace FsHotWatch.TestPrune

/// When a test run records per-test traces (`tests.traces.record`).
type TraceRecordPolicy =
    /// Never record. The default when the key is absent.
    | RecordOff
    /// Record only runs that execute every project in full (`confirm`, nightly).
    | RecordFullRuns
    /// Record every run; an impact-selected run refreshes only the tests it ran.
    | RecordEveryRun

/// How the test project's own assembly is woven (`tests.traces.weaveTests`).
type TraceWeaveTests =
    /// Call sites only (`"sites"`, the default).
    | WeaveTestSites
    /// Method-entry probes as well (`"full"`).
    | WeaveTestFull

/// The `tests.traces` block of `.fshw.json`.
type TraceSettings =
    {
        /// When to record.
        Record: TraceRecordPolicy
        /// Repo-relative as written in .fshw.json; resolved against the repo root at registration.
        DbPath: string
        /// How the test assembly itself is woven.
        WeaveTests: TraceWeaveTests
        /// Repo-relative files whose content keys a recorded trace's fingerprint.
        FingerprintInputs: string list
        /// Environment variable names whose values key a recorded trace's fingerprint.
        FingerprintEnv: string list
        /// Upper bound, in seconds, on verifying a woven copy before launching it.
        VerifyTimeoutSec: int
    }

/// Defaults and value parsing for `TraceSettings`.
[<RequireQualifiedAccess>]
module TraceSettings =
    /// Where the trace database lives when `tests.traces.db` is absent.
    [<Literal>]
    let defaultDbPath = ".fshw/test-traces.db"

    /// The verify timeout when `tests.traces.verifyTimeoutSec` is absent.
    [<Literal>]
    let defaultVerifyTimeoutSec = 300

    /// Parse the `record` value (case-insensitive). Unknown text is `None`: the caller
    /// warns and treats it as off.
    let parseRecord (s: string) : TraceRecordPolicy option =
        match s.ToLowerInvariant() with
        | "off" -> Some RecordOff
        | "full-runs" -> Some RecordFullRuns
        | "every-run" -> Some RecordEveryRun
        | _ -> None
