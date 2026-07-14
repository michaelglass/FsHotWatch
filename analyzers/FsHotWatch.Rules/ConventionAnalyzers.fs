/// FsHotWatch's own convention rules, run by the gate itself (`.fshw.json`
/// `analyzers.paths` points at this project's bin, and `mise run ci` / the
/// GitHub workflow's `check --run-once` load it). These enforce only the
/// residue the type system cannot reach:
///
///   FSHW-CLAIM-001 — a `RunClaim` (the result of `PluginCtx.RunExclusive`)
///   must be handled, never discarded. FS0020 + TreatWarningsAsErrors force
///   the value to be acknowledged, but `|> ignore` remains a legal escape —
///   and "silently drop the refused claim" IS the AUTOMATION-99 bug.
///
///   FSHW-CLOCK-001 — no local clocks. Every timestamp in the daemon is UTC;
///   a lone `DateTime.Now` (CoveragePlugin, 2026-07) skewed the one elapsed a
///   human reads when coverage gates them, while the package CHANGELOG claimed
///   "timestamps now use UTC". Unrepresentable would beat detected (host-
///   stamped transition times), but that refactor is out of scope — so the
///   class is banned here instead.
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

    type private Collector() =
        inherit SyntaxCollectorBase()

        member val DiscardedClaims = ResizeArray<range>()
        member val LocalClocks = ResizeArray<range>()

        override this.WalkExpr(_path, expr) =
            match discardedClaimInExpr expr with
            | Some r -> this.DiscardedClaims.Add r
            | None -> ()

            match localClockInExpr expr with
            | Some r -> this.LocalClocks.Add r
            | None -> ()

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
