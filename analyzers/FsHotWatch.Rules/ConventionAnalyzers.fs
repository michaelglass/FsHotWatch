/// FsHotWatch's own convention rules, run by the check itself (`.fshw.json`
/// `analyzers.paths` points at this project's bin, and `mise run ci` / the
/// GitHub workflow's `confirm --run-once` load it). These enforce only the
/// residue the type system cannot reach:
///
///   FSHW-CLAIM-001 — a `RunClaim` (the result of `PluginCtx.RunExclusive`)
///   must be handled, never discarded. FS0020 + TreatWarningsAsErrors force
///   the value to be acknowledged, but `|> ignore` remains a legal escape —
///   and "silently drop the refused claim" is the dropped-work bug this rule
///   exists to catch.
///
///   FSHW-CLOCK-001 — no local clocks. Every timestamp in the daemon is UTC; a
///   lone `DateTime.Now` skews the one elapsed a human reads when coverage gates
///   them. Making it unrepresentable would beat detecting it (host-stamped
///   transition times), but that refactor is out of scope — so the class is banned
///   here instead.
///
///   FSHW-VERDICT-001 — a RUN-level verdict may not be a fold of the PER-PROJECT
///   `TestResult.isPassed`. This is the sharpest case of "unrepresentable would
///   beat detecting it" and the one place it is genuinely unreachable: `isPassed`
///   is TOTAL and deliberately TRUE for `TestsNoMatch`, so `Map.forall isPassed`
///   over a run where every project executed nothing type-checks, is total, and
///   folds to green (AUTOMATION-272). No signature can distinguish the two
///   questions while one predicate is the honest answer to both — so the *fold*
///   is what gets named here.
///
///   FSHW-WAIT-001 — in TEST sources: no `Thread.Sleep` standing in for
///   synchronisation with an event the test then asserts on. The sleep is a bet
///   on latency, and when it loses the test produces a red that names a
///   production bug which does not exist (2026-08-12: two watcher tests, 4-20s
///   of FSEvents cold-start on a fresh temp dir). The fix — write REPEATEDLY
///   until the event arrives — is a fixture, `WatchedDir`, whose only mutation
///   does exactly that; this rule is the residue, because "call the fixture" is
///   not something a signature can insist on. See `Detect.sleepSynchronisations`
///   for exactly what fires, what was measured and declined, and how a
///   deliberate sleep opts out.
module FsHotWatch.Rules.ConventionAnalyzers

open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.ASTCollecting
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Pure syntactic detectors, separated from the SDK plumbing so the rules are
/// testable from a bare `ParsedInput`.
module Detect =

    let private lastIs (name: string) (ids: Ident list) =
        match List.tryLast ids with
        | Some id -> id.idText = name
        | None -> false

    let rec private unwrapParen (e: SynExpr) =
        match e with
        | SynExpr.Paren(expr = inner) -> unwrapParen inner
        | _ -> e

    /// True when the expression is (an application chain rooted in) a
    /// `RunExclusive` member/function call — `ctx.RunExclusive key work`,
    /// `(f x).RunExclusive key work`, partial applications included.
    let rec private isRunExclusiveCall (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) -> isRunExclusiveCall f
        | SynExpr.TypeApp(expr = inner) -> isRunExclusiveCall inner
        | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> lastIs "RunExclusive" ids
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> lastIs "RunExclusive" ids
        | SynExpr.Ident id -> id.idText = "RunExclusive"
        | _ -> false

    let private isIdentNamed (name: string) (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.Ident id -> id.idText = name
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> lastIs name ids
        | _ -> false

    /// The discard shapes a `RunClaim` must never flow into. Covered:
    ///   `… .RunExclusive … |> ignore`
    ///   `ignore (… .RunExclusive …)`
    ///   `let _ = … .RunExclusive …`
    /// (A bare statement-position discard is already a compile error via
    /// FS0020 + TreatWarningsAsErrors.)
    let private discardedClaimInExpr (e: SynExpr) : range option =
        match e with
        // lhs |> ignore  ≡  App(App(op_PipeRight, lhs), ignore)
        | SynExpr.App(funcExpr = SynExpr.App(isInfix = true; funcExpr = op; argExpr = lhs); argExpr = rhs) when
            isIdentNamed "op_PipeRight" op
            && isIdentNamed "ignore" rhs
            && isRunExclusiveCall lhs
            ->
            Some e.Range
        // ignore (…)
        | SynExpr.App(funcExpr = f; argExpr = a) when isIdentNamed "ignore" f && isRunExclusiveCall a -> Some e.Range
        | _ -> None

    let private isWildPat (p: SynPat) =
        let rec unwrap (p: SynPat) =
            match p with
            | SynPat.Paren(pat = inner) -> unwrap inner
            | _ -> p

        match unwrap p with
        | SynPat.Wild _ -> true
        | _ -> false

    /// `System.DateTime.Now` / `DateTime.Now` / `DateTimeOffset.Now` — a local
    /// clock read, as an identifier path or a dot-get off one.
    let private localClockInExpr (e: SynExpr) : range option =
        let isLocalClockPath (ids: Ident list) =
            match List.rev ids |> List.map (fun i -> i.idText) with
            | "Now" :: ty :: _ when ty = "DateTime" || ty = "DateTimeOffset" -> true
            | _ -> false

        match e with
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when isLocalClockPath ids -> Some e.Range
        | SynExpr.DotGet(expr = receiver; longDotId = SynLongIdent(id = ids)) when lastIs "Now" ids ->
            match unwrapParen receiver with
            | SynExpr.Ident id when id.idText = "DateTime" || id.idText = "DateTimeOffset" -> Some e.Range
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = rIds)) when
                lastIs "DateTime" rIds || lastIs "DateTimeOffset" rIds
                ->
                Some e.Range
            | _ -> None
        | _ -> None

    /// `Map.forall` / `List.forall` / `Seq.forall` / a bare `forall` — the head of
    /// a (possibly partially applied) `forall` call.
    let rec private isForallFunc (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) -> isForallFunc f
        | SynExpr.TypeApp(expr = inner) -> isForallFunc inner
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> lastIs "forall" ids
        | SynExpr.Ident id -> id.idText = "forall"
        | _ -> false

    /// `TestResult.isPassed` in predicate position: passed by name, or as a lambda
    /// whose body is *only* that call — `fun _ r -> TestResult.isPassed r`.
    ///
    /// Deliberately narrow. Anything wrapping the call (`not (isPassed r)`,
    /// `isPassed r && executedTests r`) roots the application chain in some other
    /// function and is NOT this rule's business: negating the predicate to select
    /// the non-green is the correct, common use.
    ///
    /// The SCOPE fold — a bare `Map.forall (fun _ r -> not (wasFiltered r))` — is
    /// likewise uncovered, and asked for on the grounds that it too is vacuously
    /// true for an empty map. Measured and declined, because the two folds are not
    /// the same bug:
    ///
    ///   * `isPassed` is wrong on a NON-empty map — three zero-match projects fold
    ///     to green — and no guard beside the fold can see that, because the
    ///     predicate itself is the lie. Naming the fold is the only place left.
    ///   * a scope fold is RIGHT on every non-empty map: `wasFiltered` returns true
    ///     for NoMatch/Deferred/Errored precisely so that it is. It is wrong only on
    ///     the empty map — `forall`'s vacuity, shared by every fold in this tree.
    ///
    /// REVISED (AUTOMATION-281). Two legs of the original argument have since been
    /// falsified by the code they described, so they are struck rather than left to
    /// mislead:
    ///
    ///   * It said the vacuity is "discharged where it arises, beside the use". For
    ///     THIS fold it no longer is — the discharge moved inside
    ///     `TestResult.ranFullSuite`.
    ///   * It said the rule would have nothing to point at, because `ranFullSuite`'s
    ///     body IS the bare fold and its empty-map `true` a pinned contract, so
    ///     recommending it would be DRY advice wearing a false-green warning's
    ///     clothes. `ranFullSuite` is now `executedAnything results && Map.forall …`
    ///     and its empty map is pinned `false`. It is therefore strictly SAFER than
    ///     a hand-rolled fold, and naming it would be real advice.
    ///
    /// The decision not to widen still stands, on the leg that survives: the only
    /// scope fold in the tree is `ranFullSuite`'s own definition, so a naive rule
    /// flags exactly the one sanctioned site and an allowlisted rule flags nothing.
    /// Zero true positives, present or plausible — that is the whole case now.
    /// Reopen it on evidence (a second hand-rolled scope fold appearing), not on the
    /// struck reasoning above.
    ///
    /// Revisit sooner if scope gains a `RunVerification`-shaped total answer that
    /// distinguishes "nothing ran" from "everything ran unfiltered". Then there is
    /// something to point at, and widening becomes worth its false-positive budget.
    let rec private isPassedPredicate (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.Lambda(body = body) -> isPassedPredicate body
        | SynExpr.App(funcExpr = f) -> isPassedPredicate f
        | SynExpr.Ident id -> id.idText = "isPassed"
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> lastIs "isPassed" ids
        | _ -> false

    /// A fold of the per-project pass predicate over a whole run's results:
    /// `results |> Map.forall (fun _ r -> TestResult.isPassed r)`,
    /// `List.forall TestResult.isPassed results`, and the partial applications.
    let private passFoldInExpr (e: SynExpr) : range option =
        match e with
        | SynExpr.App(funcExpr = f; argExpr = pred) when isForallFunc f && isPassedPredicate pred -> Some e.Range
        | _ -> None

    /// Names that ask the RUN-level question — "did this run verify anything?" —
    /// as opposed to the per-project one. A fold sitting in the same decision as
    /// one of these is already asking it, so the rule stays quiet there. That is
    /// not a loophole: it is the correct shape (TestPrune's cacheable-green gate
    /// conjoins `allPassed` with `not (allZeroMatchOf …)`), and the rule exists to
    /// catch the fold that asks NOTHING else.
    let private isVerificationName (name: string) =
        name = "verificationOf"
        || name = "allZeroMatchOf"
        || name = "allZeroMatch"
        || name = "verifiedNothing"
        || name = "RunVerification"
        || name = "AllZeroMatch"
        || name = "NoProjectsSelected"

    let private mentionsVerification (ids: Ident list) =
        ids |> List.exists (fun i -> isVerificationName i.idText)

    // --- FSHW-WAIT-001: sleeping instead of synchronising (test sources) ---

    /// The one greppable escape hatch for FSHW-WAIT-001. Put it on the sleep's
    /// own line, or anywhere in the contiguous comment block directly above it,
    /// with the reason:
    ///
    ///     // FSHW-WAIT-001 ok: negative assertion — nothing may arrive in 500ms
    ///     Thread.Sleep(500)
    ///     Assert.False(signal.Wait(0))
    ///
    /// `rg "FSHW-WAIT-001 ok"` then lists every sanctioned sleep in the suite,
    /// which is the property that matters: the exceptions have to be countable.
    [<Literal>]
    let OptOutMarker = "FSHW-WAIT-001 ok"

    /// Which files this rule polices. Deliberately name-based rather than
    /// project-based: an analyzer sees one file at a time and its `FileName` is
    /// the only thing that survives into the AST, and everything the rule names
    /// as the fix (`probeLoop`, `waitUntilTrue`, `withWatchedDir`) lives in the
    /// test assemblies.
    let isTestSource (fileName: string) : bool =
        let normalized = fileName.Replace('\\', '/')
        let name = normalized.Split('/') |> Array.tryLast |> Option.defaultValue ""

        name.EndsWith("Tests.fs", System.StringComparison.Ordinal)
        || name.StartsWith("TestHelpers", System.StringComparison.Ordinal)
        || normalized.Contains("/tests/")

    let private isThreadSleepPath (ids: Ident list) =
        match List.rev ids |> List.map (fun i -> i.idText) with
        | "Sleep" :: "Thread" :: _ -> true
        | _ -> false

    /// A `Thread.Sleep …` CALL, in any spelling that reaches the same method:
    /// `Thread.Sleep 10`, `System.Threading.Thread.Sleep(10)`,
    /// `Threading.Thread.Sleep(ts)`. A bare `Thread.Sleep` passed as a function
    /// value is not a call and is not this rule's business.
    let private threadSleepCall (e: SynExpr) : range option =
        match e with
        | SynExpr.App(funcExpr = f) ->
            match unwrapParen f with
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when isThreadSleepPath ids -> Some e.Range
            | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) when lastIs "Sleep" ids -> Some e.Range
            | _ -> None
        | _ -> None

    let private isFixedBudgetWaitName (ids: Ident list) =
        match List.tryLast ids with
        | Some id ->
            id.idText = "Wait"
            || id.idText = "WaitOne"
            || id.idText = "WaitAny"
            || id.idText = "WaitAll"
        | None -> false

    /// A one-shot wait on a signal whose budget is fixed — `signal.Wait(5000)`,
    /// `handle.WaitOne(…)`, `WaitHandle.WaitAll(…)` — or a bare read of `.IsSet`,
    /// which is the same question asked without a budget. Only the ones sitting
    /// INSIDE an assertion make a preceding sleep a synchronisation device; see
    /// `sleepSynchronisations`.
    let private eventWaitInExpr (e: SynExpr) : range option =
        match e with
        | SynExpr.App(funcExpr = f) ->
            match unwrapParen f with
            | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) when isFixedBudgetWaitName ids -> Some e.Range
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when isFixedBudgetWaitName ids -> Some e.Range
            | _ -> None
        | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) when lastIs "IsSet" ids -> Some e.Range
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when lastIs "IsSet" ids -> Some e.Range
        | _ -> None

    /// The sanctioned polling constructs. A `signal.IsSet` that is the PREDICATE
    /// of one of these is not a fixed-budget wait — it is the poll — so an
    /// unrelated earlier sleep must not be flagged because of it.
    let private isPollingCallName (name: string) =
        name = "probeLoop"
        || name = "probeUntilEvent"
        || name = "waitUntil"
        || name = "waitUntilTrue"
        || name = "waitForTerminalStatus"
        || name = "WriteUntil"
        || name = "WriteEachUntil"

    let rec private isPollingCall (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) -> isPollingCall f
        | SynExpr.TypeApp(expr = inner) -> isPollingCall inner
        | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> ids |> List.exists (fun i -> isPollingCallName i.idText)
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) ->
            ids |> List.exists (fun i -> isPollingCallName i.idText)
        | SynExpr.Ident id -> isPollingCallName id.idText
        | _ -> false

    /// An ASSERTION: Unquote's `test <@ … @>`, or any `Assert.*` call. The
    /// discriminator that makes this rule shippable — see `sleepSynchronisations`.
    let rec private isAssertionCall (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) -> isAssertionCall f
        | SynExpr.TypeApp(expr = inner) -> isAssertionCall inner
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) ->
            match ids with
            | first :: _ :: _ -> first.idText = "Assert"
            | _ -> false
        | SynExpr.Ident id -> id.idText = "test"
        | _ -> false

    let private posLeq (aLine: int, aCol: int) (bLine: int, bCol: int) =
        aLine < bLine || (aLine = bLine && aCol <= bCol)

    let private rangeContains (outer: range) (inner: range) =
        posLeq (outer.StartLine, outer.StartColumn) (inner.StartLine, inner.StartColumn)
        && posLeq (inner.EndLine, inner.EndColumn) (outer.EndLine, outer.EndColumn)

    type private Collector() =
        inherit SyntaxCollectorBase()

        member val DiscardedClaims = ResizeArray<range>()
        member val LocalClocks = ResizeArray<range>()
        member val PassFolds = ResizeArray<range>()
        member val VerificationGuards = ResizeArray<range>()

        /// Ranges of the enclosing DECISION a fold could be guarded by: a
        /// `let … in <rest>` (whose body holds the conjuncts that follow the
        /// binding) and a match arm. Scoped this tightly on purpose — the
        /// enclosing *function* in TestPrune is thousands of lines long, and
        /// suppressing a whole handler because it mentions `verificationOf`
        /// somewhere would blind the rule exactly where both bugs lived.
        member val DecisionScopes = ResizeArray<range>()

        /// FSHW-WAIT-001 material: every `Thread.Sleep` call, every fixed-budget
        /// event wait, every sanctioned polling call (whose interior waits do NOT
        /// count), every assertion (only waits INSIDE one count), and every
        /// binding body (the "same function" scope).
        member val Sleeps = ResizeArray<range>()

        member val EventWaits = ResizeArray<range>()
        member val PollingScopes = ResizeArray<range>()
        member val AssertionScopes = ResizeArray<range>()
        member val BindingScopes = ResizeArray<range>()

        override this.WalkExpr(_path, expr) =
            match discardedClaimInExpr expr with
            | Some r -> this.DiscardedClaims.Add r
            | None -> ()

            match threadSleepCall expr with
            | Some r -> this.Sleeps.Add r
            | None -> ()

            match eventWaitInExpr expr with
            | Some r -> this.EventWaits.Add r
            | None -> ()

            match expr with
            | SynExpr.App _ when isPollingCall expr -> this.PollingScopes.Add expr.Range
            | _ -> ()

            match expr with
            | SynExpr.App _ when isAssertionCall expr -> this.AssertionScopes.Add expr.Range
            | _ -> ()

            match localClockInExpr expr with
            | Some r -> this.LocalClocks.Add r
            | None -> ()

            match passFoldInExpr expr with
            | Some r -> this.PassFolds.Add r
            | None -> ()

            match expr with
            | SynExpr.LetOrUse _ -> this.DecisionScopes.Add expr.Range
            | SynExpr.Ident id when isVerificationName id.idText -> this.VerificationGuards.Add expr.Range
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when mentionsVerification ids ->
                this.VerificationGuards.Add expr.Range
            | _ -> ()

        override this.WalkClause(_path, clause) = this.DecisionScopes.Add clause.Range

        override this.WalkPat(_path, pat) =
            match pat with
            | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) when mentionsVerification ids ->
                this.VerificationGuards.Add pat.Range
            | _ -> ()

        override this.WalkBinding(_path, binding) =
            // Every binding is a "same function" scope for the sleep rule —
            // members included, which is what the `RealWatchTests` class needs.
            this.BindingScopes.Add binding.RangeOfBindingWithRhs

            match binding with
            | SynBinding(headPat = pat; expr = body) when isWildPat pat && isRunExclusiveCall body ->
                this.DiscardedClaims.Add binding.RangeOfBindingWithRhs
            | _ -> ()

    let private collect (input: ParsedInput) : Collector =
        let collector = Collector()
        walkAst collector input
        collector

    /// Ranges where a `RunExclusive` result is discarded.
    let discardedRunClaims (input: ParsedInput) : range list =
        (collect input).DiscardedClaims |> List.ofSeq

    /// Ranges where a local (non-UTC) clock is read.
    let localClocks (input: ParsedInput) : range list =
        (collect input).LocalClocks |> List.ofSeq

    /// Ranges where a per-project pass fold stands in for a run-level verdict —
    /// with no run-level verification question anywhere in the same decision.
    ///
    /// The scope is the INNERMOST enclosing `let …` expression or match arm: for
    /// properly nested ranges that is the one with the latest start position.
    let runLevelPassFolds (input: ParsedInput) : range list =
        let c = collect input
        let scopes = List.ofSeq c.DecisionScopes
        let guards = List.ofSeq c.VerificationGuards

        let guardedInScope (fold: range) =
            scopes
            |> List.filter (fun s -> rangeContains s fold)
            |> function
                | [] -> false
                | containing ->
                    let innermost = containing |> List.maxBy (fun s -> (s.StartLine, s.StartColumn))

                    guards |> List.exists (rangeContains innermost)

        c.PassFolds |> List.ofSeq |> List.filter (guardedInScope >> not)

    /// FSHW-WAIT-001. Ranges where a TEST source sleeps as a way of synchronising
    /// with an event it then ASSERTS on.
    ///
    /// WHAT FIRES, and why it is this narrow. A bare `Thread.Sleep` is not
    /// reported: most are legitimate (letting a debounce window elapse, letting
    /// a plugin drain before a temp dir is deleted, asserting a NEGATIVE — that
    /// nothing arrives inside a window). The bug class is the sleep that stands
    /// in for synchronisation, and its signature is the pair:
    ///
    ///     Thread.Sleep(100)                    // "give the watcher a moment"
    ///     File.WriteAllText(path, contents)    // ONE write
    ///     Assert.True(signal.Wait(5000), …)    // one fixed budget, one chance
    ///
    /// So a sleep is flagged only when a fixed-budget event wait (`.Wait(…)`,
    /// `.WaitOne(…)`, `.WaitAll(…)`, or a bare `.IsSet`) appears LATER IN THE
    /// SAME BINDING BODY *and* that wait is INSIDE AN ASSERTION (`Assert.…` or
    /// Unquote's `test`). Two exclusions do the real work, both measured against
    /// this suite rather than guessed:
    ///
    ///   * waits inside a sanctioned polling call (`probeLoop`, `probeUntilEvent`,
    ///     `waitUntil`, `waitUntilTrue`, `WatchedDir.WriteUntil`) never count —
    ///     there the `.IsSet` IS the poll's predicate. This is also what keeps the
    ///     rule silent on `TestHelpers`' own polling primitives.
    ///   * waits outside an assertion never count. Without this the rule fired on
    ///     13 sites across the suite, and the ones inspected were teardown
    ///     (`task.Wait(5s)` in a `finally`) or a bounded-response assertion whose
    ///     budget IS the claim (`IpcTests`' concurrent-status test) — i.e. an
    ///     Error-severity rule whose fires were almost all sanctioned, which is a
    ///     rule that gets suppressed rather than obeyed.
    ///
    /// HOW A DELIBERATE SLEEP OPTS OUT: the `FSHW-WAIT-001 ok: <reason>` comment
    /// (see `OptOutMarker`) on the sleep's line or the line above. A comment, not
    /// a helper function, on purpose — the reason is the entire point (a negative
    /// assertion has to say what must NOT happen, and inside what window), and it
    /// is greppable: `rg "FSHW-WAIT-001 ok"` enumerates every sanctioned sleep in
    /// the suite. Exceptions that cannot be counted are not exceptions.
    ///
    /// DECLINED, with the measurement. `waitUntil` (unit-returning, gives up
    /// silently) belongs to the same bug class as this sleep — which is why
    /// `waitUntilTrue` now exists beside it — and the obvious second arm is "a
    /// test whose last act is `waitUntil`". Implemented and measured: it fires 3
    /// times in this suite (CoveragePluginTests ×2, PluginFrameworkTests ×1) and
    /// every one is a deliberate teardown drain — "let the check settle so the
    /// temp dir can be cleaned" — after the real assertions have already run.
    /// Zero true positives, three sanctioned fires, so it is not shipped (same
    /// standard FSHW-VERDICT-001 applies to its own declined widening). The
    /// silent-give-up that WOULD be worth catching is `waitUntil` whose condition
    /// nothing afterwards asserts, and in this suite that set is exactly the
    /// drains. Reopen on evidence: a flake traced to a `waitUntil` that a test
    /// depended on.
    ///
    /// `getLine` is 1-based and must return "" out of range; the analyzer feeds
    /// it from `context.SourceText`, keeping this function pure.
    let sleepSynchronisations (getLine: int -> string) (input: ParsedInput) : range list =
        if not (isTestSource input.FileName) then
            []
        else
            let c = collect input
            let scopes = List.ofSeq c.BindingScopes
            let polling = List.ofSeq c.PollingScopes
            let assertions = List.ofSeq c.AssertionScopes

            let budgetedWaits =
                c.EventWaits
                |> Seq.filter (fun w ->
                    assertions |> List.exists (fun a -> rangeContains a w)
                    && not (polling |> List.exists (fun p -> rangeContains p w)))
                |> List.ofSeq

            // The sleep's own line, then upward through the comment block attached
            // to it — a real reason rarely fits on one line, and an opt-out that
            // only works on the last line of its own explanation is an opt-out
            // people quietly give up on.
            let optedOut (sleep: range) =
                let rec scan (line: int) (remaining: int) =
                    if remaining <= 0 || line < 1 then
                        false
                    else
                        let text = getLine line

                        if text.Contains(OptOutMarker) then
                            true
                        elif line = sleep.StartLine || text.TrimStart().StartsWith("//") then
                            scan (line - 1) (remaining - 1)
                        else
                            false

                scan sleep.StartLine 25

            let synchronises (sleep: range) =
                scopes
                |> List.filter (fun s -> rangeContains s sleep)
                |> function
                    | [] -> false
                    | containing ->
                        let innermost = containing |> List.maxBy (fun s -> (s.StartLine, s.StartColumn))

                        budgetedWaits
                        |> List.exists (fun w ->
                            rangeContains innermost w
                            && posLeq (sleep.EndLine, sleep.EndColumn) (w.StartLine, w.StartColumn))

            c.Sleeps
            |> List.ofSeq
            |> List.filter (fun sleep -> synchronises sleep && not (optedOut sleep))

[<CliAnalyzer("RunClaimDiscardedAnalyzer",
              "A RunClaim (RunExclusive's result) must be matched, never discarded — a dropped SlotBusy is dropped work (AUTOMATION-99)")>]
let runClaimDiscardedAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            return
                Detect.discardedRunClaims context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "RunClaim discarded"
                      Message =
                        "RunExclusive's RunClaim is discarded. Match it: SlotBusy means the work was NOT started — skip it with a stated reason or queue it, never drop it silently (that is the AUTOMATION-99 bug)."
                      Code = "FSHW-CLAIM-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }

[<CliAnalyzer("LocalClockAnalyzer",
              "Forbids DateTime.Now / DateTimeOffset.Now — every daemon timestamp is UTC; local clocks corrupt elapsed arithmetic")>]
let localClockAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            return
                Detect.localClocks context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "Local clock"
                      Message =
                        "Local clock read (DateTime/DateTimeOffset.Now). Every timestamp in the daemon is UTC — use UtcNow; mixing a local reading into UTC arithmetic skews or negates elapsed times."
                      Code = "FSHW-CLOCK-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }

[<CliAnalyzer("RunVerdictFoldAnalyzer",
              "Forbids folding TestResult.isPassed over a whole run's results as the run-level verdict — a zero-match project passes having verified nothing (AUTOMATION-272)")>]
let runVerdictFoldAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            return
                Detect.runLevelPassFolds context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "Run verdict folded from isPassed"
                      Message =
                        "forall over TestResult.isPassed is not a run-level verdict. isPassed is deliberately TRUE for TestsNoMatch, so a run in which every project matched zero tests folds to green having executed no test (AUTOMATION-272). Ask verificationOf (→ RunVerification) instead — or state the run-level guard beside this fold, which is what silences this rule."
                      Code = "FSHW-VERDICT-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }

[<CliAnalyzer("SleepSynchronisationAnalyzer",
              "In test sources: forbids Thread.Sleep used to synchronise with an event assertion — a bet on FSEvents latency, not a wait")>]
let sleepSynchronisationAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            // 1-based, "" out of range — the shape `Detect.sleepSynchronisations`
            // documents. Defensive: an analyzer that throws takes the whole run's
            // findings with it, and no opt-out comment is worth that.
            let getLine (line: int) =
                try
                    if line >= 1 && line <= context.SourceText.GetLineCount() then
                        context.SourceText.GetLineString(line - 1)
                    else
                        ""
                with _ ->
                    ""

            return
                Detect.sleepSynchronisations getLine context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "Sleep used as synchronisation"
                      Message =
                        "Thread.Sleep before an event assertion is not synchronisation — it is a bet on latency. A brand-new temp dir carries 4-20s of FSEvents cold-start on macOS, so the single write behind this sleep can land before the watcher is live and never be reported, and the red then names a production bug that does not exist. Write REPEATEDLY until the event arrives: WatchedDir.withWatchedDir + WriteUntil (tests/FsHotWatch.Tests/WatchedDir.fs), or probeLoop / waitUntilTrue directly. A sleep that is deliberate (asserting a NEGATIVE inside a window) opts out with a `// FSHW-WAIT-001 ok: <reason>` comment on this line or the one above."
                      Code = "FSHW-WAIT-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }
