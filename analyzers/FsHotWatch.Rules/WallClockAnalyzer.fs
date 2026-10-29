/// FSHW-WALL-001 — in TEST sources: no wall-clock time as a test's synchronisation or its
/// claim. The gate runs on a box at load 50–140, and a test that sleeps, times itself, or
/// asserts a fixed-millisecond wait measures that box rather than the code. 21+ tickets and
/// three sweeps (docs/wall-clock-bounds.md) each fixed some, and each was followed by more:
/// this rule is what stops the next one being written.
///
/// WHAT IS FLAGGED, in test sources only (`ConventionAnalyzers.Detect.isTestSource`):
///   1. A sleep: a call to `Thread.Sleep`, `Task.Delay` or `Async.Sleep`, at any
///      qualification. Whatever it waits for, it waits by guessing how long that takes.
///      Not flagged: a sleep of `Timeout.Infinite`, which is a stand-in for hung work that
///      only cancellation ends.
///   2. A stopwatch assertion: an assertion (`Assert.*` or Unquote's `test`) holding an
///      UPPER bound (`<`, `<=`, or `>`/`>=` reversed) whose small side reads `Elapsed`,
///      `ElapsedMilliseconds`, `ElapsedTicks`, `GetElapsedTime` or a `Stopwatch`. Its claim
///      is that something was quick, which load decides. Not flagged: a lower bound
///      (`elapsed >= X`, which load can only satisfy) and an equality (a record's `Elapsed`
///      field read back).
///   3. A fixed-millisecond timeout asserted as behaviour: inside an assertion, a
///      `Wait` / `WaitOne` / `WaitAll` / `WaitAny` / `WaitForExit` whose budget is a
///      numeric literal (or `TimeSpan.FromMilliseconds/FromSeconds/FromMinutes` of one)
///      below 30 s. That is the class that went red: 5 s and 10 s waits on in-process
///      work in loaded full runs. Not flagged: `Wait(0)`, a non-blocking read; a NAMED
///      budget (`HangWaitMs`, `HangWait`, `RealCheckWaitMs`, …), the documented hang-cap
///      form, sized in tests/FsHotWatch.Tests/TestHelpers.fs against the work it waits
///      on; a literal of 30 s or more, already sized as a hang cap; and a NEGATED wait
///      (`not (x.Wait 200)`, `Assert.False(…)`), a negative window that load can only
///      make miss a defect, never fail a correct run.
///
/// THE FIX is an injected clock or a fake host, not a bigger number. Production reads time
/// through `HostClock.Clock` (or a `unit -> DateTime` / `unit -> TimeSpan` cut from one),
/// and tests move `FsHotWatch.Tests.FakeClock.ManualClock` by hand. Process behaviour is
/// injected through the existing OS seams (`ProcessHelper.EscalationOps`,
/// `ProcessRegistry.IOwnedChild`, the CLI's `StopOps` / `ProcessOps`) — see
/// docs/wall-clock-bounds.md.
///
/// THE ALLOWLIST is an attribute, `[<WallClock("reason")>]`
/// (tests/FsHotWatch.Tests/WallClock.fs), on the test, member, type or module whose real
/// time is the point: a genuine integration test of a real host, a negative window, a
/// polling primitive. The reason is REQUIRED — an empty or missing one does not allow and
/// is itself a finding — so `rg "WallClock\("` lists every exception with its
/// justification. An attribute, not a comment, so the exception is part of the test's
/// declaration and reflection can count it.
module FsHotWatch.Rules.WallClockAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.ASTCollecting
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Pure syntactic detection, testable from a bare `ParsedInput`.
module Detect =

    /// The allowlist attribute's name, with and without the `Attribute` suffix.
    [<Literal>]
    let AttributeName = "WallClock"

    [<RequireQualifiedAccess>]
    type Kind =
        | Sleep
        | StopwatchAssertion
        | FixedTimeoutAssertion
        /// A `WallClock` attribute with no stated reason.
        | ReasonlessAllowance

    type Finding = { Kind: Kind; Range: range }

    let rec private unwrapParen (e: SynExpr) =
        match e with
        | SynExpr.Paren(expr = inner) -> unwrapParen inner
        | _ -> e

    /// The dotted name an identifier-shaped expression spells, or [] for anything else.
    let rec private identChain (e: SynExpr) : string list =
        match unwrapParen e with
        | SynExpr.Ident id -> [ id.idText ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> ids |> List.map (fun i -> i.idText)
        | SynExpr.DotGet(expr = receiver; longDotId = SynLongIdent(id = ids)) ->
            identChain receiver @ (ids |> List.map (fun i -> i.idText))
        | SynExpr.TypeApp(expr = inner) -> identChain inner
        | _ -> []

    let private endsWith (suffix: string list) (chain: string list) =
        let n = List.length suffix
        List.length chain >= n && List.skip (List.length chain - n) chain = suffix

    let private isSleepChain (chain: string list) =
        endsWith [ "Thread"; "Sleep" ] chain
        || endsWith [ "Task"; "Delay" ] chain
        || endsWith [ "Async"; "Sleep" ] chain

    /// `Timeout.Infinite`, as the whole argument or the first of a tuple. A sleep that never
    /// ends on time is a stand-in for hung work that only cancellation ends, not a bet on
    /// how long anything takes.
    let private isInfinite (arg: SynExpr) =
        let first =
            match unwrapParen arg with
            | SynExpr.Tuple(exprs = e :: _) -> e
            | a -> a

        endsWith [ "Timeout"; "Infinite" ] (identChain first)

    /// A sleep CALL. A bare `Async.Sleep` passed as a value is not a call.
    let private sleepCall (e: SynExpr) : range option =
        match e with
        | SynExpr.App(funcExpr = f; argExpr = arg) when isSleepChain (identChain f) && not (isInfinite arg) ->
            Some e.Range
        | _ -> None

    let private elapsedNames =
        set
            [ "Elapsed"
              "ElapsedMilliseconds"
              "ElapsedTicks"
              "GetElapsedTime"
              "Stopwatch" ]

    /// A read of a stopwatch or its elapsed time, at any position in a dotted chain
    /// (`sw.Elapsed.TotalSeconds`, `Stopwatch.GetElapsedTime t0`).
    let private elapsedRead (e: SynExpr) : range option =
        match e with
        | SynExpr.LongIdent _
        | SynExpr.DotGet _
        | SynExpr.Ident _ when identChain e |> List.exists elapsedNames.Contains -> Some e.Range
        | _ -> None

    /// An UPPER bound, `small < big` in any of its four spellings, as (whole, small side).
    /// Only the small side can be the measured duration of an upper bound: `elapsed < X`,
    /// `elapsed <= X`, `X > elapsed`, `X >= elapsed`. A lower bound (`elapsed >= X`) or an
    /// equality (`verdict.Elapsed = 12.5 s`, a record field read back) cannot go red on load.
    let private upperBound (e: SynExpr) : (range * range) option =
        match e with
        | SynExpr.App(funcExpr = SynExpr.App(isInfix = true; funcExpr = op; argExpr = lhs); argExpr = rhs) ->
            match identChain op with
            | [ "op_LessThan" ]
            | [ "op_LessThanOrEqual" ] -> Some(e.Range, lhs.Range)
            | [ "op_GreaterThan" ]
            | [ "op_GreaterThanOrEqual" ] -> Some(e.Range, rhs.Range)
            | _ -> None
        | _ -> None

    let private waitNames =
        set [ "Wait"; "WaitOne"; "WaitAll"; "WaitAny"; "WaitForExit" ]

    /// The size of a hang-only wait (`HangWaitMs` in tests/FsHotWatch.Tests/TestHelpers.fs).
    /// A literal budget at or above it is already sized as a hang cap; below it, a loaded
    /// box reaches it on load (5 s and 10 s waits went red in loaded full runs).
    [<Literal>]
    let HangBudgetMs = 30_000.0

    /// The milliseconds a literal budget spells: a numeric constant (milliseconds), or
    /// `TimeSpan.FromMilliseconds/FromSeconds/FromMinutes` of one. `None` for anything else.
    let rec private literalBudgetMs (scale: float) (e: SynExpr) : float option =
        match unwrapParen e with
        | SynExpr.Const(SynConst.Int32 n, _) -> Some(float n * scale)
        | SynExpr.Const(SynConst.Int64 n, _) -> Some(float n * scale)
        | SynExpr.Const(SynConst.Double d, _) -> Some(d * scale)
        | SynExpr.App(funcExpr = f; argExpr = a) ->
            let chain = identChain f

            if endsWith [ "TimeSpan"; "FromMilliseconds" ] chain then
                literalBudgetMs 1.0 a
            elif endsWith [ "TimeSpan"; "FromSeconds" ] chain then
                literalBudgetMs 1000.0 a
            elif endsWith [ "TimeSpan"; "FromMinutes" ] chain then
                literalBudgetMs 60_000.0 a
            else
                None
        | _ -> None

    /// A literal budget a loaded box can reach: positive (`Wait(0)` is a non-blocking read)
    /// and below `HangBudgetMs`.
    let private isLiteralBudget (e: SynExpr) : bool =
        match literalBudgetMs 1.0 e with
        | Some ms -> ms > 0.0 && ms < HangBudgetMs
        | None -> false

    /// A wait whose budget is a literal: `signal.Wait(5000)`, `p.WaitForExit 5000`,
    /// `WaitHandle.WaitAll(handles, 500)`.
    let private literalWait (e: SynExpr) : range option =
        match e with
        | SynExpr.App(funcExpr = f; argExpr = arg) ->
            match List.tryLast (identChain f) with
            | Some name when waitNames.Contains name ->
                let args =
                    match unwrapParen arg with
                    | SynExpr.Tuple(exprs = exprs) -> exprs
                    | a -> [ a ]

                if args |> List.exists isLiteralBudget then
                    Some e.Range
                else
                    None
            | _ -> None
        | _ -> None

    /// An assertion: any `Assert.*` call, or Unquote's `test`.
    let rec private isAssertionCall (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) -> isAssertionCall f
        | SynExpr.TypeApp(expr = inner) -> isAssertionCall inner
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = first :: _ :: _)) -> first.idText = "Assert"
        | SynExpr.Ident id -> id.idText = "test"
        | _ -> false

    /// A NEGATED context: `not (…)`, or `Assert.False(…)`. A wait asserted NOT to finish
    /// inside its window is a negative window: load only delays events, so it can hide a
    /// defect on a slow box but cannot fail a correct one (docs/wall-clock-bounds.md, rule
    /// 3). It is the recommended form of a negative assertion, so it is not flagged.
    let rec private isNegation (e: SynExpr) : bool =
        match unwrapParen e with
        | SynExpr.App(funcExpr = f) ->
            match identChain f with
            | [ "not" ] -> true
            | [ "Assert"; "False" ] -> true
            | _ -> isNegation f
        | _ -> false

    let private isAllowAttribute (a: SynAttribute) =
        match List.tryLast a.TypeName.LongIdent with
        | Some id -> id.idText = AttributeName || id.idText = AttributeName + "Attribute"
        | None -> false

    /// The attribute's stated reason: its one positional string literal, when not blank.
    let private reasonOf (a: SynAttribute) : string option =
        match unwrapParen a.ArgExpr with
        | SynExpr.Const(SynConst.String(text = text), _) when not (System.String.IsNullOrWhiteSpace text) -> Some text
        | _ -> None

    let private posLeq (aLine: int, aCol: int) (bLine: int, bCol: int) =
        aLine < bLine || (aLine = bLine && aCol <= bCol)

    let private rangeContains (outer: range) (inner: range) =
        posLeq (outer.StartLine, outer.StartColumn) (inner.StartLine, inner.StartColumn)
        && posLeq (inner.EndLine, inner.EndColumn) (outer.EndLine, outer.EndColumn)

    type private Collector() =
        inherit SyntaxCollectorBase()

        member val Sleeps = ResizeArray<range>()
        member val ElapsedReads = ResizeArray<range>()
        member val UpperBounds = ResizeArray<range * range>()
        member val LiteralWaits = ResizeArray<range>()
        member val Assertions = ResizeArray<range>()
        member val Negations = ResizeArray<range>()
        /// Declarations a reasoned `WallClock` attribute allows.
        member val Allowed = ResizeArray<range>()
        member val Reasonless = ResizeArray<range>()

        member this.Consider (attributes: SynAttributes) (scope: range) =
            for a in attributes |> List.collect (fun l -> l.Attributes) do
                if isAllowAttribute a then
                    match reasonOf a with
                    | Some _ -> this.Allowed.Add scope
                    | None -> this.Reasonless.Add a.Range

        override this.WalkExpr(_path, expr) =
            sleepCall expr |> Option.iter this.Sleeps.Add
            elapsedRead expr |> Option.iter this.ElapsedReads.Add
            upperBound expr |> Option.iter this.UpperBounds.Add
            literalWait expr |> Option.iter this.LiteralWaits.Add

            match expr with
            | SynExpr.App _ when isAssertionCall expr -> this.Assertions.Add expr.Range
            | _ -> ()

            match expr with
            | SynExpr.App _ when isNegation expr -> this.Negations.Add expr.Range
            | _ -> ()

        override this.WalkBinding(_path, binding) =
            match binding with
            | SynBinding(attributes = attributes) -> this.Consider attributes binding.RangeOfBindingWithRhs

        override this.WalkTypeDefn(_path, typeDefn) =
            match typeDefn with
            | SynTypeDefn(typeInfo = SynComponentInfo(attributes = attributes); range = r) -> this.Consider attributes r

        override this.WalkSynModuleDecl(_path, decl) =
            match decl with
            | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(attributes = attributes); range = r) ->
                this.Consider attributes r
            | _ -> ()

        override this.WalkSynModuleOrNamespace(_path, m) =
            match m with
            | SynModuleOrNamespace(attribs = attributes; range = r) -> this.Consider attributes r

    /// Every FSHW-WALL-001 finding in `input`, or [] when it is not a test source.
    let findings (input: ParsedInput) : Finding list =
        if not (ConventionAnalyzers.Detect.isTestSource input.FileName) then
            []
        else
            let c = Collector()
            walkAst c input
            let allowed = List.ofSeq c.Allowed
            let assertions = List.ofSeq c.Assertions

            let isAllowed (r: range) =
                allowed |> List.exists (fun s -> rangeContains s r)

            /// The assertions that contain one of `reads`, each once.
            let assertionsOver (reads: ResizeArray<range>) =
                assertions
                |> List.filter (fun a -> reads |> Seq.exists (rangeContains a))
                // An assertion nested in another reports once, at the outer one.
                |> fun hits ->
                    hits
                    |> List.filter (fun a -> not (hits |> List.exists (fun o -> o <> a && rangeContains o a)))

            let positiveWaits =
                c.LiteralWaits
                |> Seq.filter (fun w -> not (c.Negations |> Seq.exists (fun n -> rangeContains n w)))
                |> ResizeArray

            // An upper bound whose small side reads a stopwatch or an elapsed time.
            let measuredUpperBounds =
                c.UpperBounds
                |> Seq.filter (fun (_, small) -> c.ElapsedReads |> Seq.exists (rangeContains small))
                |> Seq.map fst
                |> ResizeArray

            let flagged =
                [ for r in c.Sleeps -> { Kind = Kind.Sleep; Range = r }
                  for r in assertionsOver measuredUpperBounds ->
                      { Kind = Kind.StopwatchAssertion
                        Range = r }
                  for r in assertionsOver positiveWaits ->
                      { Kind = Kind.FixedTimeoutAssertion
                        Range = r } ]
                |> List.filter (fun f -> not (isAllowed f.Range))

            [ for r in c.Reasonless ->
                  { Kind = Kind.ReasonlessAllowance
                    Range = r } ]
            @ flagged
            |> List.sortBy (fun f -> f.Range.StartLine, f.Range.StartColumn)

    let private remedy =
        "Drive time from an injected clock (HostClock.Clock in production; FsHotWatch.Tests.FakeClock.ManualClock in tests) \
         or a fake host (ProcessHelper.EscalationOps, ProcessRegistry.IOwnedChild, StopOps), and assert on what happened — an \
         outcome, a count, a log line, a handshake — never on how long it took. A test whose real time IS the point (a genuine \
         integration test of a real host, a negative window, a polling primitive) says so with `[<WallClock(\"<reason>\")>]` \
         on the test, type or module. See docs/wall-clock-bounds.md."

    let message (kind: Kind) : string =
        match kind with
        | Kind.Sleep ->
            "A sleep in a test (Thread.Sleep / Task.Delay / Async.Sleep) waits by guessing how long something takes, and on \
             a loaded box the guess is wrong. "
            + remedy
        | Kind.StopwatchAssertion ->
            "This assertion reads a stopwatch or an elapsed time, so its claim is a duration, and box load decides it. "
            + remedy
        | Kind.FixedTimeoutAssertion ->
            "This assertion waits on a literal millisecond budget, so a loaded box fails it on load, not on behaviour. A wait \
             that only ends a hang uses a named budget from TestHelpers (HangWaitMs, SyncHangBackstopMs, …) sized against the \
             work it waits on. "
            + remedy
        | Kind.ReasonlessAllowance ->
            "[<WallClock>] needs its reason: `[<WallClock(\"why real time is the point here\")>]`. An exception without a \
             reason allows nothing — the exceptions have to be countable AND justified."

[<CliAnalyzer("WallClockAnalyzer",
              "In test sources: forbids sleeps, stopwatch assertions and literal-millisecond asserted waits — use an injected clock or fake host, or allowlist with [<WallClock(reason)>]")>]
let wallClockAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            return
                Detect.findings context.ParseFileResults.ParseTree
                |> List.map (fun f ->
                    { Type = "Wall-clock time in a test"
                      Message = Detect.message f.Kind
                      Code = "FSHW-WALL-001"
                      Severity = Severity.Error
                      Range = f.Range
                      Fixes = [] })
        }
