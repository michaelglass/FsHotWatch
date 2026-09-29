/// Nested patterns: a case whose field is matched too, as a case or as a literal. Each match
/// tests the outer case and then its field, with probes on the case class and its field
/// between the two tests.
module FxLib.Nested

type Summary =
    | InSync
    | Pending of adds: int

type Reading =
    | Measured of summary: Summary * changes: int
    | NotMeasurable of reason: string

/// A union case nested in a case pattern.
let failure (reading: Reading) =
    match reading with
    | Measured(InSync, _) -> None
    | other -> Some other

type Outcome =
    | Suite of passed: bool
    | Command of exitCode: int

/// A case whose field is tested against a literal.
let describeOutcome (outcome: Outcome) =
    match outcome with
    | Suite _ -> "suite"
    | Command 0 -> "exit 0"
    | Command code -> "exit " + string code

type Relation =
    | Equal
    | Behind of count: int
    | Ahead of count: int

type Divergence =
    | SameLine of relation: Relation
    | Forked of missing: string list
    | CannotCheck

let private divergenceOf (behind: int) (ahead: int) =
    if behind < 0 || ahead < 0 then
        CannotCheck
    elif behind > 0 && ahead > 0 then
        Forked [ string behind; string ahead ]
    elif behind > 0 then
        SameLine(Behind behind)
    elif ahead > 0 then
        SameLine(Ahead ahead)
    else
        SameLine Equal

/// Several arms nesting cases in one case, on a computed value.
let divergence (behind: int) (ahead: int) =
    match divergenceOf behind ahead with
    | SameLine Equal -> 0
    | SameLine(Ahead count) -> count
    | SameLine(Behind count) -> -count
    | Forked missing -> missing.Length
    | CannotCheck -> -1
