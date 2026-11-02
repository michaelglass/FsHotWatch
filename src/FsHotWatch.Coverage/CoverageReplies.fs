/// The replies of the coverage plugin's commands, as types: each command's reply schema id, the
/// reply it carries, the text it is written as, and the reading of that text back. Both replies
/// are plain text lines rather than JSON, as they always were.
module FsHotWatch.Coverage.CoverageReplies

/// The reply schema of `coverage-status`.
[<Literal>]
let StatusSchema = "coverage/coverage-status-v1"

/// The reply schema of `coverage-ratchet`.
[<Literal>]
let RatchetSchema = "coverage/coverage-ratchet-v1"

/// What `coverage-status` answers: how the last coverage check ended, if one has run.
[<RequireQualifiedAccess>]
type CoverageStatus =
    | NotChecked
    | Passed
    | Failed

module CoverageStatus =
    let private texts =
        [ CoverageStatus.NotChecked, "coverage: no check run yet"
          CoverageStatus.Passed, "coverage: OK"
          CoverageStatus.Failed, "coverage: FAILED (run `fshw errors` for details)" ]

    let serialize (status: CoverageStatus) : string =
        texts |> List.find (fst >> (=) status) |> snd

    /// The status a `coverage-status` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : CoverageStatus option =
        texts |> List.tryFind (snd >> (=) payload) |> Option.map fst

/// What `coverage-ratchet` answers: whether the thresholds were raised to the measured coverage,
/// and if not, why.
[<RequireQualifiedAccess>]
type RatchetReply =
    /// No `coverage.cobertura.xml` was found under the search directory.
    | NoReportFound
    /// None of the configured report paths exists.
    | NoNamedReportExists
    /// The thresholds in this config file were raised to the measured coverage.
    | Updated of configPath: string
    /// The ratchet could not read or write coverage, for this reason.
    | Failed of reason: string
    /// The ratchet did not get the coverage slot within its bound.
    | TimedOut

module RatchetReply =
    let private updatedPrefix = "coverage-ratchet: thresholds updated in "
    let private failedPrefix = "coverage-ratchet failed: "
    let private noReportFound = "coverage-ratchet: no coverage.cobertura.xml found"

    let private noNamedReportExists =
        "coverage-ratchet: none of the named coverage reports exists"

    let private timedOut =
        "coverage-ratchet: did not complete within 5 minutes (a coverage check may be holding the slot); retry"

    let serialize (reply: RatchetReply) : string =
        match reply with
        | RatchetReply.NoReportFound -> noReportFound
        | RatchetReply.NoNamedReportExists -> noNamedReportExists
        | RatchetReply.Updated configPath -> updatedPrefix + configPath
        | RatchetReply.Failed reason -> failedPrefix + reason
        | RatchetReply.TimedOut -> timedOut

    /// The reply a `coverage-ratchet` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : RatchetReply option =
        if payload = noReportFound then
            Some RatchetReply.NoReportFound
        elif payload = noNamedReportExists then
            Some RatchetReply.NoNamedReportExists
        elif payload = timedOut then
            Some RatchetReply.TimedOut
        elif payload.StartsWith(updatedPrefix, System.StringComparison.Ordinal) then
            Some(RatchetReply.Updated(payload.Substring updatedPrefix.Length))
        elif payload.StartsWith(failedPrefix, System.StringComparison.Ordinal) then
            Some(RatchetReply.Failed(payload.Substring failedPrefix.Length))
        else
            None
