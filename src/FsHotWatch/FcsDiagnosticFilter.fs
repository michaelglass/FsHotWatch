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

/// One FCS message family whose two `'…'` slots are both filled by
/// `NicePrint.minimalStringsOfTwoTypes`: the text before the first slot, the text
/// between the two, what may follow each closing quote, and whether the template
/// ends in the `{2}` constraint slot.
type private TwoTypeFamily =
    {
        Opening: string
        Separator: string
        /// Punctuation the template puts straight after each slot's closing quote.
        AfterSlot: string
        /// Whether the template carries the trailing `{2}` slot. A family without one
        /// has nowhere to say that two identically-rendered type VARIABLES differ in
        /// their constraints, so for it a render that names a type variable is refused.
        HasConstraintSlot: bool
    }

/// Taken verbatim from `FSStrings.resources` / `FSComp.txt` in
/// FSharp.Compiler.Service 43.12.401 — not from memory:
///
///   ErrorFromAddingTypeEquation1
///     "This expression was expected to have type\n    '{1}'    \nbut here has type\n    '{0}'    {2}"
///   ErrorFromAddingTypeEquation2
///     "Type mismatch. Expecting a\n    '{0}'    \nbut given a\n    '{1}'    {2}\n"
///   ErrorsFromAddingSubsumptionConstraint
///     "Type constraint mismatch. The type \n    '{0}'    \nis not compatible with type\n    '{1}'    {2}\n"
///   ConstraintSolverTypesNotInEqualityRelation2
///     "The type '{0}' does not match the type '{1}'"
///   followingPatternMatchClauseHasWrongType
///     "All branches of a pattern match expression must return values implicitly convertible
///      to the type of the first branch, which here is '{0}'. This branch returns a value of type '{1}'."
///   ifExpression
///     "All branches of an 'if' expression must return values implicitly convertible to the
///      type of the first branch, which here is '{0}'. This branch returns a value of type '{1}'."
///
/// The last three render their pair with the same two-type escalation and then DROP its
/// constraint text, which is why they carry `HasConstraintSlot = false`.
///
/// The tuple-shaped siblings (`ErrorFromAddingTypeEquation1Tuple`,
/// `…2Tuple`, `…Tuples`) are deliberately NOT here. Those compare a tuple
/// against a non-tuple, or two tuples of DIFFERENT LENGTH, so their two slots
/// are not two renderings of the same question and an identical render would
/// not mean what it means above.
let private twoTypeMessageFamilies =
    let withConstraintSlot opening separator =
        { Opening = opening
          Separator = separator
          AfterSlot = ""
          HasConstraintSlot = true }

    let branches (expression: string) =
        { Opening =
            $"All branches of %s{expression} must return values implicitly convertible to the type of the first branch, which here is"
          Separator = "This branch returns a value of type"
          AfterSlot = "."
          HasConstraintSlot = false }

    [ withConstraintSlot "This expression was expected to have type" "but here has type"
      withConstraintSlot "Type mismatch. Expecting a" "but given a"
      withConstraintSlot "Type constraint mismatch. The type" "is not compatible with type"
      { Opening = "The type"
        Separator = "does not match the type"
        AfterSlot = ""
        HasConstraintSlot = false }
      branches "a pattern match expression"
      branches "an 'if' expression" ]

let private whitespaceRun =
    System.Text.RegularExpressions.Regex(@"[\s\p{Cc}]+", System.Text.RegularExpressions.RegexOptions.Compiled)

/// FCS renders these messages across several lines with runs of padding spaces,
/// and callers may have normalized the newlines already. Collapse both so the
/// split below sees one shape.
///
/// `\p{Cc}` is there because `\s` is not enough, and this cost a release. .NET's
/// `\s` is `[\f\n\r\t\v\x85\p{Z}]` — every character it matches is whitespace or a
/// Unicode SEPARATOR. FCS separates the two rendered types with GROUP SEPARATOR
/// (U+001D), which is a CONTROL character in category Cc and matches none of
/// them. So the separator survived the collapse, the ` but here has type `
/// lookup below could not find its padded form, `tryRenderedTypePair` returned
/// `None`, and 64 self-incompatible diagnostics were reported as ordinary code
/// errors on a tree whose 32,557 tests all passed.
///
/// The guard failed CLOSED, which is the right direction — but "fails closed"
/// and "works" are different claims, and only the second one is useful here.
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

/// A type variable in a rendered type: `'a` or `^a`, at the start or after anything
/// that cannot continue an identifier. A name that merely ENDS in a prime (`Foo'`)
/// does not match.
let private typeVariable =
    System.Text.RegularExpressions.Regex(@"(^|[^\w.'^])['^]\w", System.Text.RegularExpressions.RegexOptions.Compiled)

/// One slot's rendered type: the fragment, less the punctuation the template puts
/// after it, less its quotes.
let private slotOf (family: TwoTypeFamily) (fragment: string) : string option =
    let t = fragment.Trim()

    if t.EndsWith(family.AfterSlot, StringComparison.Ordinal) then
        unquote (t.Substring(0, t.Length - family.AfterSlot.Length))
    else
        None

/// Split a two-type mismatch message into its two RENDERED type strings, in
/// message order. `None` for anything that is not one of the known families,
/// that carries a trailing constraint explanation, or — in a family with no
/// constraint slot — that names a type variable on either side.
///
/// Fails CLOSED in every branch: an unrecognised message yields `None`, and
/// every caller reports a `None` as an ordinary diagnostic.
let tryRenderedTypePair (message: string) : (string * string) option =
    if String.IsNullOrWhiteSpace message then
        None
    else
        let collapsed = collapseWhitespace message

        twoTypeMessageFamilies
        |> List.tryPick (fun family ->
            if not (collapsed.StartsWith(family.Opening, StringComparison.Ordinal)) then
                None
            else
                let rest = collapsed.Substring(family.Opening.Length)
                let padded = family.AfterSlot + " " + family.Separator + " "

                match rest.IndexOf(padded, StringComparison.Ordinal) with
                | -1 -> None
                | i ->
                    let left = rest.Substring(0, i + family.AfterSlot.Length)
                    let right = rest.Substring(i + padded.Length)

                    match slotOf family left, slotOf family right with
                    | Some a, Some b when
                        family.HasConstraintSlot
                        || not (typeVariable.IsMatch a || typeVariable.IsMatch b)
                        ->
                        Some(a, b)
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
            $"fshw internal: FCS reported '%s{d.RenderedType}' as incompatible with ITSELF in %s{project}. Both sides of the mismatch rendered identically, so the compiler named no difference to act on: this is NOT an error in your code, and nothing else this check said about the file is trusted either. fshw re-checks such a file at most once, and the daemon log says whether this one was. What causes it is not yet known — please report it."
          Severity = DiagnosticSeverity.Info
          Line = d.Line
          Column = d.Column
          Detail = None })

/// The ledger entries the OTHER diagnostics of a suspect check become: a check
/// that reported a type incompatible with ITSELF has shown that its answer for this
/// file is not a reading of the code, so none of what it said about the file is a
/// finding — the errors that follow from the phantom mismatch (an inferred type
/// that no longer unifies, a match that no longer looks complete) least of all, and
/// no message parse can tell those apart from real ones.
///
/// They are NOT made informational. Some of them may be real, and demoting a real
/// error to `Info` would let a broken file go green. Each keeps its severity and is
/// reported under `fcs-internal`, which the verdict classifies as a checker fault:
/// a run whose only failures are these has no verdict — neither red nor green — and
/// a genuine error anywhere else still reddens it.
let internal suspectCheckEntries (entries: ErrorEntry list) : ErrorEntry list =
    entries
    |> List.map (fun e ->
        { e with
            Message =
                $"fshw internal: not a finding — this file's check also reported a type as incompatible with ITSELF, so nothing it said about the file is trusted. Re-run after `fshw stop` for a real answer. FCS said: %s{e.Message}" })

/// The message logged when a project's checker state is dropped and the file
/// re-checked.
let internal recheckLogLine (project: string) (file: string) : string =
    $"%s{project}: FCS reported a type as incompatible with itself in %s{file} — dropping the project's checker state and re-checking once"

/// The message logged when a self-incompatible answer was produced in a generation of
/// the project's checker state that another check has since dropped: it is re-checked
/// in the current one, which costs no budget because nothing is dropped.
let internal generationRecheckLogLine (project: string) (file: string) (started: int64) (current: int64) : string =
    $"%s{project}: FCS reported a type as incompatible with itself in %s{file}, checked in generation %d{started}; the project is now in generation %d{current}, so re-checking once there (no state dropped)"

/// The message logged when a self-incompatible answer is kept: the generation it was
/// produced in is still current and another file spent the budget for dropping it.
let internal recheckDeniedLogLine (project: string) (file: string) (spentBy: string) (spentAt: DateTime) : string =
    let at =
        spentAt.ToString("HH:mm:ss.fff", Globalization.CultureInfo.InvariantCulture)

    $"%s{project}: FCS reported a type as incompatible with itself in %s{file} — self-incompatible, budget spent by %s{spentBy} at %s{at}Z, first answer kept; its errors are reported as checker faults, not findings"

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
/// invalidation starts a new generation for every file in the project, so one
/// DROP per project per cooldown is the ceiling. It bounds only drops: a check
/// whose answer came from a generation someone else has already dropped is
/// re-checked in the current one for free (see `recheckIfSelfIncompatible`).
let internal shouldRecheckProject (cooldown: TimeSpan) (now: DateTime) (lastRetry: DateTime option) : bool =
    match lastRetry with
    | None -> true
    | Some last -> now - last >= cooldown

/// What became of one check's answer under `recheckIfSelfIncompatible`.
[<RequireQualifiedAccess>]
type RecheckOutcome =
    /// The answer declared no type incompatible with itself; it was kept.
    | NotNeeded
    /// The answer came from a generation of the project's checker state that has since
    /// been dropped, so the file was re-checked in the current one without a drop.
    | RecheckedInCurrentGeneration of started: int64 * current: int64
    /// This check dropped the project's checker state and re-checked.
    | StateDropped
    /// The generation it came from is still current and the drop budget was spent,
    /// by `spentBy` at `spentAt`: the first answer was kept.
    | BudgetSpent of spentBy: string * spentAt: DateTime

/// Ask again, at most once, when a first answer declares a type incompatible with
/// itself.
///
/// `decide` says whether and how (see `RecheckBudget.Decide`), and has already done
/// any drop it decided on by the time it returns: a re-check that throws cannot
/// leave the project eligible to be dropped again on the next file. `recheck` builds
/// its snapshot when called, so it is in whatever generation is current by then.
///
/// Generic in the answer so the whole recovery is exercisable without a
/// compiler: FCS types appear only in the `messagesOf` projection the caller
/// supplies.
let internal recheckIfSelfIncompatible
    (messagesOf: 'answer -> string seq)
    (isSelfIncompatible: string -> bool)
    (decide: unit -> RecheckOutcome)
    (recheck: unit -> Async<'answer>)
    (first: 'answer)
    : Async<'answer * RecheckOutcome> =
    async {
        if not (messagesOf first |> Seq.exists isSelfIncompatible) then
            return first, RecheckOutcome.NotNeeded
        else
            match decide () with
            | RecheckOutcome.NotNeeded
            | RecheckOutcome.BudgetSpent _ as kept -> return first, kept
            | RecheckOutcome.RecheckedInCurrentGeneration _
            | RecheckOutcome.StateDropped as asked ->
                let! second = recheck ()
                return second, asked
    }

/// The per-project budget for dropping checker state, and the one place that decides
/// how a self-incompatible answer is asked again, so its answers can be exercised
/// without a compiler in the loop.
type internal RecheckBudget(cooldown: TimeSpan) =
    let lastSpent = ConcurrentDictionary<string, DateTime * string>()
    let gates = ConcurrentDictionary<string, obj>()

    /// How a self-incompatible answer to `file`, computed in generation `startedIn` of
    /// `project`'s checker state, is asked again. Tried in this order:
    ///
    ///   * the project's generation has ADVANCED past `startedIn` — another check
    ///     already dropped the state this answer came from — so it is re-checked in
    ///     the current generation. No budget: nothing is dropped. Without this, every
    ///     check of a project in flight when one of them dropped the state finished
    ///     on the dropped generation and kept its answer, because the one drop the
    ///     budget allowed had been spent by the first file to finish.
    ///   * otherwise, if the budget allows, it is spent for `file` and `drop` runs.
    ///   * otherwise the first answer is kept, naming who spent the budget and when.
    ///
    /// Atomic per project: the generation is read and the drop made under one lock, so
    /// no check can see the budget spent by a drop whose new generation it cannot see
    /// yet — that window would keep a poisoned answer the drop had already cured.
    member _.Decide
        (
            project: string,
            file: string,
            now: DateTime,
            startedIn: int64,
            currentGeneration: unit -> int64,
            drop: unit -> unit
        ) : RecheckOutcome =
        lock (gates.GetOrAdd(project, fun _ -> obj ())) (fun () ->
            let current = currentGeneration ()

            match lastSpent.TryGetValue project with
            | _ when current > startedIn -> RecheckOutcome.RecheckedInCurrentGeneration(startedIn, current)
            | true, (at, by) when not (shouldRecheckProject cooldown now (Some at)) ->
                RecheckOutcome.BudgetSpent(by, at)
            | _ ->
                lastSpent[project] <- (now, file)
                drop ()
                RecheckOutcome.StateDropped)

/// Default gap between two drops of the SAME project's checker state.
let internal defaultRecheckCooldown = TimeSpan.FromMinutes 5.0

/// `recheckIfSelfIncompatible` as the check pipeline runs it: decided by `budget`,
/// with `dropState` as the drop, and every outcome but the ordinary one logged
/// through `log`. `project` is the budget's key; its file name is what the log
/// lines show. Kept apart from the pipeline so N concurrent checks of one project
/// can be driven through the SAME code the pipeline runs, with a fake checker in
/// place of FCS.
let internal recheckWithBudget
    (budget: RecheckBudget)
    (project: string)
    (file: string)
    (now: unit -> DateTime)
    (log: string -> unit)
    (messagesOf: 'answer -> string seq)
    (startedIn: int64)
    (currentGeneration: unit -> int64)
    (dropState: unit -> unit)
    (recheck: unit -> Async<'answer>)
    (first: 'answer)
    : Async<'answer * RecheckOutcome> =
    async {
        let projectName = IO.Path.GetFileName project

        let decide () =
            let drop () =
                log (recheckLogLine projectName file)
                dropState ()

            let outcome =
                budget.Decide(project, file, now (), startedIn, currentGeneration, drop)

            match outcome with
            | RecheckOutcome.RecheckedInCurrentGeneration(started, current) ->
                log (generationRecheckLogLine projectName file started current)
            | RecheckOutcome.BudgetSpent(spentBy, spentAt) ->
                log (recheckDeniedLogLine projectName file spentBy spentAt)
            | RecheckOutcome.NotNeeded
            | RecheckOutcome.StateDropped -> ()

            outcome

        return! recheckIfSelfIncompatible messagesOf isSelfIncompatibleTypeMessage decide recheck first
    }
