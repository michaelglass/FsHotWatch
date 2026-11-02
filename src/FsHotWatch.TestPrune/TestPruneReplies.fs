/// The replies of the test-prune plugin's commands, as types: each command's reply schema id,
/// the reply it carries, the JSON it is written as, and the reading of that JSON back.
///
/// `test-scope` and `check-reach` declare their schemas here, but their readers are the CLI's
/// `IpcParsing.parseTestRunReport` and `IpcParsing.parseCheckReach`, which the verdict is
/// computed from.
module FsHotWatch.TestPrune.TestPruneReplies

open System.Text.Json

/// The reply schema of `run-tests` and `test-results`.
[<Literal>]
let RunSchema = "test-prune/run-v1"

/// The reply schema of `set-scope`.
[<Literal>]
let SetScopeSchema = "test-prune/set-scope-v1"

/// The reply schema of `test-scope` (read by `IpcParsing.parseTestRunReport`).
[<Literal>]
let TestScopeSchema = "test-prune/test-scope-v1"

/// The reply schema of `check-reach` (read by `IpcParsing.parseCheckReach`).
[<Literal>]
let CheckReachSchema = "test-prune/check-reach-v1"

/// The reply schema of `affected-tests`.
[<Literal>]
let AffectedTestsSchema = "test-prune/affected-tests-v1"

/// The reply schema of `changed-files`.
[<Literal>]
let ChangedFilesSchema = "test-prune/changed-files-v1"

/// The reply schema of `flaky-tests`.
[<Literal>]
let FlakyTestsSchema = "test-prune/flaky-tests-v1"

/// What `run-tests` and `test-results` answer.
[<RequireQualifiedAccess>]
type RunReply =
    /// A run's report, as `RunResults.formatTestResultsJson` writes it: the run's `coverage`
    /// token, its filter and a `projects` array. It is carried as written, because the CLI's
    /// run-report renderer reads its fields.
    | Report of json: string
    /// The run did not produce a result within the caller's wait.
    | Busy of message: string
    /// The command refused, or the run could not be launched, for this reason.
    | Refused of error: string
    /// `test-results`: a run is in progress.
    | Running
    /// `test-results`: a run finished and its results are not yet committed.
    | Settling
    /// `test-results`: no run has completed.
    | NotRun

module RunReply =
    let serialize (reply: RunReply) : string =
        match reply with
        | RunReply.Report json -> json
        | RunReply.Busy message -> JsonSerializer.Serialize({| status = "busy"; message = message |})
        | RunReply.Refused error -> JsonSerializer.Serialize({| error = error |})
        | RunReply.Running -> JsonSerializer.Serialize({| status = "running" |})
        | RunReply.Settling -> JsonSerializer.Serialize({| status = "settling" |})
        | RunReply.NotRun -> JsonSerializer.Serialize({| status = "not run" |})

    let private ofRoot (payload: string) (root: JsonElement) : RunReply option =
        let field = FsHotWatch.Json.tryProp root

        match field "count", FsHotWatch.Json.tryString root "error", FsHotWatch.Json.tryString root "status" with
        | Some _, _, _ -> None
        | None, Some error, _ -> Some(RunReply.Refused error)
        | None, None, Some "busy" -> FsHotWatch.Json.tryString root "message" |> Option.map RunReply.Busy
        | None, None, Some "running" -> Some RunReply.Running
        | None, None, Some "settling" -> Some RunReply.Settling
        | None, None, Some "not run" -> Some RunReply.NotRun
        | None, None, Some _ -> None
        | None, None, None ->
            match field "projects" with
            | Some projects when projects.ValueKind = JsonValueKind.Array -> Some(RunReply.Report payload)
            | _ -> None

    /// The reply a run payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : RunReply option =
        payload |> FsHotWatch.Json.tryReadDocument (ofRoot payload)

/// What `set-scope` answers.
[<RequireQualifiedAccess>]
type ScopeReply =
    /// The scope the next run is folded against was set to this one.
    | ScopeSet of scope: string
    /// The scope was not one `set-scope` knows.
    | ScopeRefused of error: string

module ScopeReply =
    let serialize (reply: ScopeReply) : string =
        match reply with
        | ScopeReply.ScopeSet scope -> JsonSerializer.Serialize({| scope = scope |})
        | ScopeReply.ScopeRefused error -> JsonSerializer.Serialize({| error = error |})

    /// The reply a `set-scope` payload carries; `None` for one this schema does not describe.
    let tryParse (payload: string) : ScopeReply option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryString root "scope", FsHotWatch.Json.tryString root "error" with
            | Some scope, None -> Some(ScopeReply.ScopeSet scope)
            | None, Some error -> Some(ScopeReply.ScopeRefused error)
            | _ -> None)

/// One test `affected-tests` selects.
type AffectedTest =
    { Project: string
      Class: string
      Method: string }

/// What `affected-tests` answers: the tests the changes so far select.
module AffectedTests =
    let serialize (tests: AffectedTest list) : string =
        tests
        |> List.map (fun t ->
            {| project = t.Project
               ``class`` = t.Class
               ``method`` = t.Method |})
        |> JsonSerializer.Serialize

    /// The tests an `affected-tests` payload lists; `None` for one this schema does not describe.
    let tryParse (payload: string) : AffectedTest list option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            if root.ValueKind <> JsonValueKind.Array then
                None
            else
                let tests =
                    root.EnumerateArray()
                    |> Seq.map (fun t ->
                        match
                            FsHotWatch.Json.tryString t "project",
                            FsHotWatch.Json.tryString t "class",
                            FsHotWatch.Json.tryString t "method"
                        with
                        | Some project, Some cls, Some method ->
                            Some
                                { Project = project
                                  Class = cls
                                  Method = method }
                        | _ -> None)
                    |> List.ofSeq

                if List.forall Option.isSome tests then
                    Some(List.choose id tests)
                else
                    None)

/// What `changed-files` answers: the files changed since the last flush.
module ChangedFiles =
    let serialize (files: string list) : string = JsonSerializer.Serialize(files)

    /// The files a `changed-files` payload lists; `None` for one this schema does not describe.
    let tryParse (payload: string) : string list option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            if root.ValueKind <> JsonValueKind.Array then
                None
            else
                let files = root.EnumerateArray() |> List.ofSeq

                if files |> List.forall (fun f -> f.ValueKind = JsonValueKind.String) then
                    Some(files |> List.map _.GetString())
                else
                    None)

/// One test `flaky-tests` lists: its flakiness score and how many runs it was scored over.
type FlakyTest =
    { Name: string
      Flakiness: float
      Runs: int }

/// What `flaky-tests` answers: the flakiest tests recorded.
module FlakyTests =
    let serialize (tests: FlakyTest list) : string =
        let payload =
            tests
            |> List.map (fun t ->
                {| name = t.Name
                   flakiness = t.Flakiness
                   runs = t.Runs |})

        JsonSerializer.Serialize({| tests = payload |})

    let private testOf (t: JsonElement) : FlakyTest option =
        match
            FsHotWatch.Json.tryString t "name", FsHotWatch.Json.tryProp t "flakiness", FsHotWatch.Json.tryInt t "runs"
        with
        | Some name, Some score, Some runs when score.ValueKind = JsonValueKind.Number ->
            Some
                { Name = name
                  Flakiness = score.GetDouble()
                  Runs = runs }
        | _ -> None

    /// The tests a `flaky-tests` payload lists; `None` for one this schema does not describe.
    let tryParse (payload: string) : FlakyTest list option =
        payload
        |> FsHotWatch.Json.tryReadDocument (fun root ->
            match FsHotWatch.Json.tryProp root "tests" with
            | Some tests when tests.ValueKind = JsonValueKind.Array ->
                let read = tests.EnumerateArray() |> Seq.map testOf |> List.ofSeq

                if List.forall Option.isSome read then
                    Some(List.choose id read)
                else
                    None
            | _ -> None)
