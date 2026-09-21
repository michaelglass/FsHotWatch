/// Shared FCS diagnostic suppression helpers used by both the user-visible
/// error-reporting path (`FsHotWatch.Daemon.reportFcsDiagnostics`) and the
/// TestPrune cache-poisoning gate (`FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors`).
///
/// Keeping these in one module guarantees the two paths agree on which codes are
/// considered "noise": an asymmetry between them trips the gate on phantom errors in
/// projects that rely on `<TreatWarningsAsErrors>` + `#nowarn` directives.
///
/// It also owns the SELF-INCOMPATIBLE type guard (see the block comment further
/// down): the decision that a diagnostic claiming a type is incompatible with
/// ITSELF is a fault in our own checking and never code feedback. That decision is
/// deliberately NOT shared with `hasFcsErrors` — see `classifyDiagnostic`.
module FsHotWatch.FcsDiagnosticFilter

/// Parse `#nowarn` directives from F# source text, returning the set of suppressed
/// warning codes. Workaround for https://github.com/dotnet/fsharp/issues/9796 —
/// FCS TransparentCompiler ignores `#nowarn` directives for warnaserror codes.
/// When that issue is resolved this function and its callers can be removed.
let parseNowarnCodes (source: string) : Set<int> =
    source.Split('\n')
    |> Array.filter (fun line -> line.TrimStart().StartsWith("#nowarn"))
    |> Array.collect (fun line -> line.TrimStart().Split('"'))
    |> Array.choose (fun part ->
        match System.Int32.TryParse(part) with
        | true, code -> Some code
        | _ -> None)
    |> Set.ofArray

/// Effective suppression set: caller-configured codes ∪ per-file `#nowarn`.
let allSuppressedCodes (configured: Set<int>) (source: string) : Set<int> =
    Set.union configured (parseNowarnCodes source)

// ---------------------------------------------------------------------------
// Self-incompatible type diagnostics
// ---------------------------------------------------------------------------
//
// FCS sometimes reports a type mismatch whose two sides render to the SAME
// string:
//
//     error FS0001: This expression was expected to have type
//         'Intelligence.Domain.BriefEntryEditV3.Edit'
//     but here has type
//         'Intelligence.Domain.BriefEntryEditV3.Edit'
//
// Observed at scale: one full `fshw confirm` over a large repo produced 335
// `reddenedBy` entries, 334 of which had exactly this shape. A `dotnet build`
// of the SAME tree produced 0 errors. Every one of them was phantom.
//
// WHY A SELF-IDENTICAL RENDER IS NEVER REAL CODE FEEDBACK
//
// The two `'…'` slots in these messages are not `ToString()` on each type
// independently. They are the output of `NicePrint.minimalStringsOfTwoTypes`,
// whose entire job is to render two types so that a reader can TELL THEM
// APART. It escalates: short names first, then module/namespace qualification,
// then assembly identity. Measured against this exact compiler:
//
//   two same-named types in different modules   -> 'A.T'   vs 'B.T'
//   two same-named types in different assemblies ->
//       'Dup.T (LibA, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null)'
//       'Dup.T (LibB, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null)'
//
// So when both sides STILL render identically, the compiler has exhausted
// every axis it knows how to distinguish types on — name, namespace, assembly
// name, version, culture, public key token — and found no difference. There is
// no edit the reader could make in response, because nothing was said to
// differ. Whatever produced the diagnostic is therefore inside our checking,
// not inside the code being checked.
//
// WHAT CAUSES IT IS NOT KNOWN, AND THIS GUARD DOES NOT DEPEND ON KNOWING
//
// The obvious story — FCS holding two `FSharpEntity` objects for one type, from
// two live compilations of the same assembly — is a GUESS, and the one concrete
// version of it that was investigated (a `LoadTime`/`Stamp` defect reaching
// `AreSameForChecking`) was measured and RETRACTED: `useTransparentCompiler` is
// on, and TransparentCompiler never calls that function. The mechanism was real
// and on a path FCS does not take.
//
// None of that weakens what is written above, which is the entire basis for the
// guard: the argument is about what the COMPILER SAID, not about why it said it.
// A diagnostic that names no difference cannot be acted on, whatever produced
// it. Keep the two apart — the incident proves the symptom, never the diagnosis
// — and do not let a future mechanism story, or its retraction, be read as
// evidence for or against this predicate.
//
// The `{2}` slot matters too. The templates end with a trailing slot the
// compiler fills with type-parameter constraint text — the place it puts the
// distinguishing information when the two rendered names alone do not carry
// it. `tryRenderedTypePair` therefore requires that slot to be EMPTY: a
// message with a trailing explanation is left alone and reported normally.
//
// WHY THIS IS A MESSAGE PARSE AND NOT A STRUCTURED-DATA CHECK
//
// `FSharpDiagnostic.ExtendedData` can carry a
// `TypeMismatchDiagnosticExtendedData` with `ExpectedType`/`ActualType` as
// `FSharpType` values, and reaching for those looks like the more robust
// choice. It is strictly WEAKER here. Formatting each `FSharpType`
// independently does not run the two-type escalation above, so the genuine
// cross-assembly conflict measured above would format as `Dup.T` and `Dup.T`
// and be misclassified as phantom — the exact false positive this guard must
// not have. The rendered message IS the compiler's own
// "are these distinguishable?" verdict, so it is the right thing to read.
// `ExtendedData` is also not constructible outside FCS, so it could not be
// exercised by a test.

open System
open System.Collections.Concurrent
open FsHotWatch.ErrorLedger

/// The FCS message families whose two `'…'` slots are both filled by
/// `NicePrint.minimalStringsOfTwoTypes`. Each pair is (opening text, separator).
///
/// Taken verbatim from `FSStrings.resources` in FSharp.Compiler.Service
/// 43.12.401 — not from memory:
///
///   ErrorFromAddingTypeEquation1
///     "This expression was expected to have type\n    '{1}'    \nbut here has type\n    '{0}'    {2}"
///   ErrorFromAddingTypeEquation2
///     "Type mismatch. Expecting a\n    '{0}'    \nbut given a\n    '{1}'    {2}\n"
///   ErrorsFromAddingSubsumptionConstraint
///     "Type constraint mismatch. The type \n    '{0}'    \nis not compatible with type\n    '{1}'    {2}\n"
///
/// The tuple-shaped siblings (`ErrorFromAddingTypeEquation1Tuple`,
/// `…2Tuple`, `…Tuples`) are deliberately NOT here. Those compare a tuple
/// against a non-tuple, or two tuples of DIFFERENT LENGTH, so their two slots
/// are not two renderings of the same question and an identical render would
/// not mean what it means above.
let private twoTypeMessageFamilies =
    [ "This expression was expected to have type", "but here has type"
      "Type mismatch. Expecting a", "but given a"
      "Type constraint mismatch. The type", "is not compatible with type" ]

let private whitespaceRun =
    System.Text.RegularExpressions.Regex(@"\s+", System.Text.RegularExpressions.RegexOptions.Compiled)

/// FCS renders these messages across several lines with runs of padding spaces,
/// and callers may have normalized the newlines already. Collapse both so the
/// split below sees one shape.
let private collapseWhitespace (s: string) = whitespaceRun.Replace(s, " ").Trim()

/// Strip the single quotes the templates wrap each rendered type in.
/// `None` when the fragment is not exactly a quoted run — which is how the
/// trailing `{2}` constraint slot is detected: with `{2}` non-empty the second
/// fragment does not END at its closing quote.
let private unquote (fragment: string) : string option =
    let t = fragment.Trim()

    if t.Length >= 2 && t.[0] = '\'' && t.[t.Length - 1] = '\'' then
        Some(t.Substring(1, t.Length - 2))
    else
        None

/// Split a two-type mismatch message into its two RENDERED type strings, in
/// message order. `None` for anything that is not one of the three known
/// families, or that carries a trailing constraint explanation.
///
/// Fails CLOSED in every branch: an unrecognised message yields `None`, and
/// every caller reports a `None` as an ordinary diagnostic.
let tryRenderedTypePair (message: string) : (string * string) option =
    if String.IsNullOrWhiteSpace message then
        None
    else
        let collapsed = collapseWhitespace message

        twoTypeMessageFamilies
        |> List.tryPick (fun (opening, separator) ->
            if not (collapsed.StartsWith(opening, StringComparison.Ordinal)) then
                None
            else
                let rest = collapsed.Substring(opening.Length)
                let padded = " " + separator + " "

                match rest.IndexOf(padded, StringComparison.Ordinal) with
                | -1 -> None
                | i ->
                    let left = rest.Substring(0, i)
                    let right = rest.Substring(i + padded.Length)

                    match unquote left, unquote right with
                    | Some a, Some b -> Some(a, b)
                    | _ -> None)

/// The rendered type name when a diagnostic declares a type incompatible with
/// ITSELF, `None` otherwise. See the block comment above for why this can never
/// be feedback about the code being checked — and for why that holds without
/// knowing what produces it.
let trySelfIncompatibleType (message: string) : string option =
    match tryRenderedTypePair message with
    | Some(expected, actual) when String.Equals(expected, actual, StringComparison.Ordinal) -> Some expected
    | _ -> None

/// True iff `message` declares a type incompatible with itself.
let isSelfIncompatibleTypeMessage (message: string) : bool =
    (trySelfIncompatibleType message).IsSome

/// One self-incompatible diagnostic, kept as its own type so it cannot be
/// mistaken for — or converted into — a reportable `ErrorEntry`.
type SelfIncompatibleDiagnostic =
    {
        /// The single rendered type name both sides of the mismatch printed.
        RenderedType: string
        Line: int
        Column: int
    }

/// What one FCS diagnostic IS, decided once. Total and mutually exclusive.
///
/// `SelfIncompatible` carries a `SelfIncompatibleDiagnostic`, never an
/// `ErrorEntry`, and there is no function anywhere that turns one into the
/// other. That is the point: the ledger's reporting path takes `ErrorEntry`
/// values, so a self-incompatible diagnostic has no representation that could
/// reach `reddenedBy` — it is not filtered out at the edge, it never has the
/// shape the edge accepts.
[<RequireQualifiedAccess>]
type FcsDiagnosticClass =
    | Reportable of ErrorEntry
    | Suppressed
    | SelfIncompatible of SelfIncompatibleDiagnostic

/// Classify one already-extracted diagnostic. Kept free of FCS types so the
/// whole decision is testable without a compiler: callers project
/// `FSharpDiagnostic` onto these five fields.
///
/// Order matters. Suppression is checked FIRST so a code the operator has
/// already silenced is reported as silenced rather than as an internal fault,
/// which keeps the self-incompatible counter a measure of real cache
/// corruption instead of a mix.
let classifyDiagnostic
    (suppressedCodes: Set<int>)
    (errorNumber: int)
    (message: string)
    (severity: DiagnosticSeverity)
    (line: int)
    (column: int)
    : FcsDiagnosticClass =
    if suppressedCodes.Contains errorNumber then
        FcsDiagnosticClass.Suppressed
    else
        match trySelfIncompatibleType message with
        | Some renderedType ->
            FcsDiagnosticClass.SelfIncompatible
                { RenderedType = renderedType
                  Line = line
                  Column = column }
        | None ->
            FcsDiagnosticClass.Reportable
                { Message = message
                  Severity = severity
                  Line = line
                  Column = column
                  Detail = None }

/// The warn line each self-incompatible diagnostic is logged under. Answers
/// "what did I break?" in the first clause, because that is the reader's first
/// question on seeing a type named against itself — and names the observation
/// rather than a cause, which is not known.
let internal selfIncompatibleLogLine (project: string) (file: string) (d: SelfIncompatibleDiagnostic) : string =
    $"self-incompatible type in %s{project}: %s{file}(%d{d.Line},%d{d.Column}) reported '%s{d.RenderedType}' as incompatible with ITSELF — a fault in fshw's checking, not an error in your code"

/// One warn line per self-incompatible diagnostic. Returned as a list rather
/// than logged from a loop at the call site so the shape is exercised here,
/// where a test can produce a fault on demand.
let internal selfIncompatibleLogLines
    (project: string)
    (file: string)
    (faults: SelfIncompatibleDiagnostic list)
    : string list =
    faults |> List.map (selfIncompatibleLogLine project file)

/// The ledger entries a batch of self-incompatible diagnostics becomes.
///
/// `Info`, never `Error` or `Warning`: these are reported under a ledger key of
/// their own so a reader can find them, and `Info` is the only severity that
/// cannot redden a run under any policy — `warningsAreFailures` included. A
/// fault in our checker is not a finding about the reader's code, so it must not
/// decide the colour of their run.
let internal selfIncompatibleLedgerEntries
    (project: string)
    (faults: SelfIncompatibleDiagnostic list)
    : ErrorEntry list =
    faults
    |> List.map (fun d ->
        { Message =
            $"fshw internal: FCS reported '%s{d.RenderedType}' as incompatible with ITSELF in %s{project}, and said so again after its checker state was dropped and the file re-checked. Both sides of the mismatch rendered identically, so the compiler named no difference to act on: this is NOT an error in your code. What causes it is not yet known — please report it."
          Severity = DiagnosticSeverity.Info
          Line = d.Line
          Column = d.Column
          Detail = None })

/// The message logged when a project's checker state is dropped and the file
/// re-checked.
let internal recheckLogLine (project: string) (file: string) : string =
    $"%s{project}: FCS reported a type as incompatible with itself in %s{file} — dropping the project's checker state and re-checking once"

/// Retry policy for the only remedy available: drop the project's FCS
/// configuration and check the file again.
///
/// Empirical, not derived. With the cause unknown, dropping the checker's state
/// is a guess at the class of thing that might clear it — cheap, bounded, and
/// worth one attempt. It is NOT known to work, which is exactly why a survivor
/// is surfaced rather than swallowed.
///
/// Bounded per project rather than per occurrence. The observed incident
/// produced 334 self-incompatible diagnostics in a single run; re-typechecking
/// a whole project once per diagnostic would cost more than the bug does. One
/// invalidation clears the stale entity for every file in the project, so one
/// retry per project per cooldown is both sufficient and the ceiling.
let internal shouldRecheckProject (cooldown: TimeSpan) (now: DateTime) (lastRetry: DateTime option) : bool =
    match lastRetry with
    | None -> true
    | Some last -> now - last >= cooldown

/// Drop the checker's state for this project and ask again, once, when a first
/// answer declares a type incompatible with itself.
///
/// Generic in the answer so the whole recovery is exercisable without a
/// compiler: FCS types appear only in the `messagesOf` projection the caller
/// supplies. `shouldRetry` is the per-project budget, `onRetry` is the state
/// drop and its bookkeeping, `recheck` is the second ask.
///
/// `onRetry` runs BEFORE `recheck` and exactly once, so the budget is spent even
/// if the second ask throws — a re-check that fails must not leave the project
/// eligible to be invalidated again on the next file.
let internal recheckIfSelfIncompatible
    (messagesOf: 'answer -> string seq)
    (isSelfIncompatible: string -> bool)
    (budgetAllows: bool)
    (onRetry: unit -> unit)
    (recheck: unit -> Async<'answer>)
    (first: 'answer)
    : Async<'answer> =
    async {
        let stale = messagesOf first |> Seq.exists isSelfIncompatible

        if not (stale && budgetAllows) then
            return first
        else
            onRetry ()
            return! recheck ()
    }

/// The per-project budget for dropping checker state, kept here rather
/// than as a bare dictionary in the pipeline so both of its answers can be
/// exercised without a compiler in the loop.
type internal RecheckBudget(cooldown: TimeSpan) =
    let lastRetry = ConcurrentDictionary<string, DateTime>()

    /// May this project's checker state be dropped right now?
    member _.Allows(project: string, now: DateTime) : bool =
        let last =
            match lastRetry.TryGetValue project with
            | true, t -> Some t
            | false, _ -> None

        shouldRecheckProject cooldown now last

    /// Record that it was. Called before the re-check, so a re-check that throws
    /// still costs the budget rather than leaving the project eligible again on
    /// the very next file.
    member _.Spend(project: string, now: DateTime) : unit = lastRetry[project] <- now

/// Default gap between two self-incompatible retries of the SAME project.
let internal defaultRecheckCooldown = TimeSpan.FromMinutes 5.0
