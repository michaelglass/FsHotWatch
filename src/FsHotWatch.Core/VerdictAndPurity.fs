/// Closed verdict and purity unions (AUTOMATION-1635, T3 of AUTOMATION-1632).
///
/// Both are CLOSED: every consumer matches them exhaustively, so a new case is a compile
/// error at every site that must decide what it means, instead of a fallthrough that
/// quietly treats it as one of the old ones.
namespace FsHotWatch.Core

open System

/// A list with at least one element. `Red` and `Missing` carry one, so "red, with nothing
/// failing" and "missing, with nothing missing" cannot be built.
type NonEmpty<'T> =
    {
        /// The first element.
        Head: 'T
        /// The rest, possibly none.
        Tail: 'T list
    }

/// Builds and reads non-empty lists.
[<RequireQualifiedAccess>]
module NonEmpty =
    /// `items` as a non-empty list; `None` when there are none.
    let ofList (items: 'T list) : NonEmpty<'T> option =
        match items with
        | head :: tail -> Some { Head = head; Tail = tail }
        | [] -> None

    /// Every element, in order.
    let toList (items: NonEmpty<'T>) : 'T list = items.Head :: items.Tail

/// Something that failed: a test, a file, a rule.
type Failure =
    {
        /// What failed, as the producer names it.
        Name: string
        /// Why, as the producer reports it.
        Detail: string
    }

/// What a check concluded.
[<RequireQualifiedAccess>]
type Verdict =
    /// Everything it covered passed, and nothing it needed was missing.
    | Green
    /// At least one thing failed.
    | Red of failing: NonEmpty<Failure>
    /// Nothing failed, but these keys have no outcome yet, so it cannot be green.
    | Missing of keys: NonEmpty<KeyRef>

/// Combines a check's parts into its verdict.
[<RequireQualifiedAccess>]
module Verdict =
    /// The verdict over `failing` and `missing`. Precedence Red > Missing > Green: a known
    /// failure is definitive whatever is still missing (filling a gap cannot turn it green),
    /// and a gap is never green.
    let ofParts (failing: Failure list) (missing: KeyRef list) : Verdict =
        match NonEmpty.ofList failing, NonEmpty.ofList missing with
        | Some failures, _ -> Verdict.Red failures
        | None, Some keys -> Verdict.Missing keys
        | None, None -> Verdict.Green

/// How long an impure outcome may be replayed.
///
/// PROVISIONAL: decided by the AUTOMATION-1636 Need/oracle pilot. Both cases are
/// placeholders, not accepted design; only the existence of a validity carried by
/// `Impure` is fixed here.
[<RequireQualifiedAccess>]
type Validity =
    /// Provisional: only within the run that produced it.
    | ThisRunOnly
    /// Provisional: until this instant.
    | Until of expires: DateTimeOffset

/// Whether an outcome is a function of its key. The union is CLOSED and its three cases
/// are fixed; what `Oracle` means and what `Validity` holds are PROVISIONAL, decided by
/// the AUTOMATION-1636 Need/oracle pilot.
[<RequireQualifiedAccess>]
type Purity =
    /// Determined by its key's inputs alone: replayable whenever the key matches.
    | Pure
    /// PROVISIONAL: decided by the AUTOMATION-1636 Need/oracle pilot. Working reading:
    /// determined by its inputs and an authority outside them (a test host, a compiler).
    | Oracle
    /// Depends on state the key does not capture (time, network, machine). How long it
    /// may be replayed is PROVISIONAL (see `Validity`).
    | Impure of validity: Validity
