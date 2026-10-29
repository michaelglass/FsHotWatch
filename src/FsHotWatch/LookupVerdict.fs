/// The verdict as a LOOKUP (ADR-059 §3): for a tree, each required action either has a
/// recorded outcome under the key of the inputs it ran over, or it does not. Nothing here
/// reads a daemon, a generation or a clock — only the answers the outcome store gives for
/// keys — so the same store answers the same way before and after any restart.
///
/// A requirement names one or more ALTERNATIVES, each a set of keys that must all have
/// passed. The answer folds them conservatively:
///   * any key of any alternative whose newest record FAILED makes the requirement red —
///     a fail is evidence against the inputs it names, and a cheaper alternative passing
///     does not outrank it;
///   * otherwise an alternative whose keys all PASSED satisfies it;
///   * otherwise it is missing: nothing on record decides it, so it must run.
/// The verdict is red if any requirement is, else missing if any is, else green.
module FsHotWatch.LookupVerdict

/// One required piece of evidence, by name, satisfied by any one of its alternatives.
type Requirement =
    { Name: string
      Alternatives: ActionKey.ActionKey list list }

module Requirement =
    /// A requirement one key satisfies.
    let single (name: string) (key: ActionKey.ActionKey) : Requirement =
        { Name = name
          Alternatives = [ [ key ] ] }

[<RequireQualifiedAccess>]
type Answer =
    | Green
    /// The requirements that failed, in the order they were required.
    | Red of failing: string list
    /// The requirements nothing on record decides, in the order they were required.
    | Missing of missing: string list

[<RequireQualifiedAccess>]
type private Status =
    | Passed
    | Failed
    | Undecided

let private statusOf (lookup: string -> OutcomeStore.Outcome option) (r: Requirement) : Status =
    let outcomes =
        r.Alternatives
        |> List.map (List.map (fun (k: ActionKey.ActionKey) -> lookup k.Value))

    if outcomes |> List.exists (List.contains (Some OutcomeStore.Outcome.Fail)) then
        Status.Failed
    elif
        outcomes
        |> List.exists (fun alt ->
            not (List.isEmpty alt)
            && alt |> List.forall ((=) (Some OutcomeStore.Outcome.Pass)))
    then
        Status.Passed
    else
        Status.Undecided

/// The lookup verdict of `requirements` against `lookup`, the newest recorded outcome of
/// a key value (`OutcomeStore.Store.Lookup`).
let decide (requirements: Requirement list) (lookup: string -> OutcomeStore.Outcome option) : Answer =
    let statuses = requirements |> List.map (fun r -> r.Name, statusOf lookup r)

    let named (status: Status) =
        statuses |> List.filter (snd >> (=) status) |> List.map fst

    match named Status.Failed, named Status.Undecided with
    | [], [] -> Answer.Green
    | [], missing -> Answer.Missing missing
    | failing, _ -> Answer.Red failing
