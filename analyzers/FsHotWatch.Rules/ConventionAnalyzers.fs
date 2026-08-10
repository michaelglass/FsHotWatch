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

        override this.WalkExpr(_path, expr) =
            match discardedClaimInExpr expr with
            | Some r -> this.DiscardedClaims.Add r
            | None -> ()

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
