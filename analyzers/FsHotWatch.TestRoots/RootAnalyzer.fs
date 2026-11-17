/// FSHW-ROOT-001 — a test may only touch the filesystem under a root it alone owns.
///
/// WHY (AUTOMATION-1775). Three flakes in one week had one shape: a test touched a place
/// on disk that another test, or the build, was changing at the same time. A repo-wide
/// audit walk raced TestPrune.Trace.Tests' scratch directories inside the repository
/// (AUTOMATION-1671); a daemon left from an earlier run rebuilt bin/ under the next run's
/// tests (AUTOMATION-1746); leftover build output of a deleted project was counted as a
/// test project (AUTOMATION-1717). Each fix was local, and nothing stopped the next test
/// doing the same thing. This rule is that stop.
///
/// WHAT IS FLAGGED, in test sources only (`TestSource.isTestSource`): every expression
/// that resolves a place on disk the test does not own —
///   - the shared temp directory: `Path.GetTempPath`, `Path.GetTempFileName`,
///     `Directory.CreateTempSubdirectory`;
///   - the repository and its build output: `__SOURCE_DIRECTORY__` and
///     `AppContext.BaseDirectory`, directories a test can walk up from or write into. NOT
///     `<assembly>.Location`: that names one file this process has already loaded, so a
///     test reading it cannot see it change (on main d5a441ae its 28 hits were all `-r:`
///     references and copies of a test dependency: false positives);
///   - the user's machine: `Environment.GetFolderPath`, and `GetEnvironmentVariable` of
///     `HOME`, `USERPROFILE`, `TMPDIR`, `TMP` or `TEMP`;
///   - the process working directory: `Directory.GetCurrentDirectory`,
///     `Directory.SetCurrentDirectory`, `Environment.CurrentDirectory`.
///
/// THE FIX is `TestRoot` (tests/FsHotWatch.Tests/TestRoot.fs): a fresh directory the test owns and
/// disposes, and `RepositoryTree`, the read-only view of the real repository. Those are
/// the only places that resolve the locations above, and they carry the opt-out.
///
/// NOT SEEN: a string literal (`"/tmp/x"`) that does touch disk, and a path built from an
/// owned root that escapes it with `..`. `TestRoot` refuses the second at runtime.
///
/// SUPPRESS: `// FSHW-ROOT-001 ok: <reason>` on the flagged line or in the contiguous
/// comment block directly above it. The reason is required — a bare marker does not
/// suppress — so `rg "FSHW-ROOT-001 ok"` lists the allow-list WITH its justifications.
///
/// THE ALLOW-LIST (nine files, 2026-11-17; a new marker belongs on this list too):
///   1. tests/FsHotWatch.Tests/TestRoot.fs — the helper itself: the one place that reads the
///      temp directory, the repository root, the test binary's directory and the NuGet cache.
///   2. tests/FsHotWatch.Tests/UserHomeIsolation.fs — names the REAL fshw homes only to
///      assert that no test resolves them.
///   3. tests/FsHotWatch.Tests/TestHelpers.fs — the process working directory seam
///      (`withProcessWorkingDirectory` and friends): moving and reading it IS its job, and its
///      callers run serialized (LogGlobal).
///   4. tests/FsHotWatch.Tests/EventTests.fs and
///   5. tests/FsHotWatch.Tests/InProcessEvaluationTests.fs — the process working directory is
///      the subject under test (serialized, LogGlobal).
///   6. tests/FsHotWatch.Tests/ProgramTests.fs — names the real home only to assert the fish
///      completions write leaves it untouched, or falls back to it.
///   7. tests/FsHotWatch.Tests/RepositoryIdentityTests.fs — compares the product's state home
///      against the real home's, read-only.
///   8. tests/FsHotWatch.IntegrationTests/IntegrationTests.fs — builds this repository's
///      analyzer projects in place; a copy would lose Directory.Packages.props, the build
///      props and global.json.
///   9. tests/TestPrune.Trace.Tests/Fixtures.fs (`inRepositoryScratch`) — fixture copies must
///      sit under the traced repository root (ADR-077).
module FsHotWatch.Rules.RootAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.ASTCollecting
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Pure syntactic detection, testable from a bare `ParsedInput`.
module Detect =

    [<Literal>]
    let OptOutMarker = "FSHW-ROOT-001 ok:"

    /// The environment variables that name a place on the user's machine.
    let private machineVariables =
        set [ "HOME"; "USERPROFILE"; "TMPDIR"; "TMP"; "TEMP" ]

    let private texts (ids: Ident list) = ids |> List.map (fun i -> i.idText)

    /// The dotted name an identifier-shaped expression spells, or [] for anything else.
    let rec private identChain (e: SynExpr) : string list =
        match e with
        | SynExpr.Ident id -> [ id.idText ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> texts ids
        | SynExpr.DotGet(expr = receiver; longDotId = SynLongIdent(id = ids)) -> identChain receiver @ texts ids
        | SynExpr.Paren(expr = inner) -> identChain inner
        | _ -> []

    /// Whether a dotted name resolves a shared location. Matched on its tail, so every
    /// qualification (`Path.GetTempPath`, `System.IO.Path.GetTempPath`) is the same name.
    let private sharedName (chain: string list) : bool =
        match List.rev chain with
        | "GetTempPath" :: "Path" :: _
        | "GetTempFileName" :: "Path" :: _
        | "CreateTempSubdirectory" :: "Directory" :: _
        | "GetFolderPath" :: "Environment" :: _
        | "BaseDirectory" :: "AppContext" :: _
        | "GetCurrentDirectory" :: "Directory" :: _
        | "SetCurrentDirectory" :: "Directory" :: _
        | "CurrentDirectory" :: "Environment" :: _ -> true
        | _ -> false

    let rec private stringArgument (e: SynExpr) : string option =
        match e with
        | SynExpr.Const(SynConst.String(text = s), _) -> Some s
        | SynExpr.Paren(expr = inner) -> stringArgument inner
        | _ -> None

    /// `Environment.GetEnvironmentVariable "HOME"` and friends.
    let private machineVariableRead (e: SynExpr) : bool =
        match e with
        | SynExpr.App(funcExpr = f; argExpr = arg) ->
            match List.rev (identChain f), stringArgument arg with
            | "GetEnvironmentVariable" :: _, Some name -> machineVariables.Contains name
            | _ -> false
        | _ -> false

    type private Collector() =
        inherit SyntaxCollectorBase()

        member val Hits = ResizeArray<range>()

        override this.WalkExpr(_path, expr) =
            match expr with
            | SynExpr.Const(SynConst.SourceIdentifier(constant = "__SOURCE_DIRECTORY__"), r) -> this.Hits.Add r
            | SynExpr.LongIdent _
            | SynExpr.DotGet _ when sharedName (identChain expr) -> this.Hits.Add expr.Range
            | SynExpr.App _ when machineVariableRead expr -> this.Hits.Add expr.Range
            | _ -> ()

    let private posLeq (aLine: int, aCol: int) (bLine: int, bCol: int) =
        aLine < bLine || (aLine = bLine && aCol <= bCol)

    let private contains (outer: range) (inner: range) =
        posLeq (outer.StartLine, outer.StartColumn) (inner.StartLine, inner.StartColumn)
        && posLeq (inner.EndLine, inner.EndColumn) (outer.EndLine, outer.EndColumn)

    /// Every place in `input` that resolves a shared location, before suppression. A hit
    /// nested inside another is reported once, at the outer range.
    let sharedLocations (input: ParsedInput) : range list =
        if not (TestSource.isTestSource input.FileName) then
            []
        else
            let c = Collector()
            walkAst c input
            let hits = List.ofSeq c.Hits |> List.distinct

            hits
            |> List.filter (fun r -> not (hits |> List.exists (fun o -> o <> r && contains o r)))
            |> List.sortBy (fun r -> r.StartLine, r.StartColumn)

    /// True when the line carries the marker AND a reason after it.
    let private reasoned (text: string) =
        match text.IndexOf(OptOutMarker, System.StringComparison.Ordinal) with
        | -1 -> false
        | at -> text.Substring(at + OptOutMarker.Length).Trim().Length > 0

    /// `sharedLocations` minus the ones opted out with a stated reason: on the hit's own
    /// line, or anywhere in the contiguous comment block directly above it. `getLine` is
    /// 1-based and returns "" out of range.
    let findings (getLine: int -> string) (input: ParsedInput) : range list =
        let optedOut (hit: range) =
            let rec scan (line: int) =
                if line < 1 then
                    false
                else
                    let text = getLine line

                    if line = hit.StartLine || text.TrimStart().StartsWith("//") then
                        reasoned text || scan (line - 1)
                    else
                        false

            scan hit.StartLine

        sharedLocations input |> List.filter (optedOut >> not)

[<CliAnalyzer("UnownedRootAnalyzer",
              "In test sources: forbids resolving the shared temp directory, the repository, the user's home or the process working directory outside TestRoot/RepositoryTree — a test may only touch disk under a root it alone owns")>]
let unownedRootAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            let getLine (line: int) =
                try
                    if line >= 1 && line <= context.SourceText.GetLineCount() then
                        context.SourceText.GetLineString(line - 1)
                    else
                        ""
                with _ ->
                    ""

            return
                Detect.findings getLine context.ParseFileResults.ParseTree
                |> List.map (fun range ->
                    { Type = "Test touches a root it does not own"
                      Message =
                        "A test may only touch disk under a root it alone owns (AUTOMATION-1775). This resolves a SHARED location — the \
                         temp directory, the repository or its build output, the user's home, or the process working directory — that \
                         other tests, other test runs and the build change concurrently: a repo walk raced another project's scratch \
                         dirs (AUTOMATION-1671), a leftover daemon rebuilt bin/ under a run (AUTOMATION-1746). Use TestRoot \
                         (`TestRoot.create`, `TestRoot.withRoot`, `withTempDir`) for scratch space, and RepositoryTree for read-only \
                         access to the real repository. A path that only needs to be absolute (never touched) can be a literal such as \
                         \"/r/repo\". A genuine exception states its reason: `// FSHW-ROOT-001 ok: <reason>` on this line or the comment \
                         block above."
                      Code = "FSHW-ROOT-001"
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] })
        }
