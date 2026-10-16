/// Which FCS diagnostics a file's check holds against it once suppression is applied.
///
/// The ONE place that decides it. Every reader of a check's diagnostics — the ledger
/// (`Daemon.reportFcsDiagnostics`), TestPrune's cache-poisoning gate (`hasFcsErrors`) and
/// the analysis evidence a receipt is graded from (`AnalysisFileEvidence.fromResult`) —
/// goes through here, so no two of them can disagree about a code the source silenced.
/// They did once: the receipt read raw Error severity, and refused a green over a
/// `#nowarn` file whenever FCS reported the suppressed code, which it does on a change
/// batch's re-check and not on a cold scan.
///
/// Compiled ahead of `Events` so the evidence there can use it.
module FsHotWatch.FcsSuppression

open FSharp.Compiler.Diagnostics

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

/// The Error-severity diagnostics in `diagnostics` that neither `configured` nor the
/// `#nowarn` directives in `source` suppress.
let blockingErrors (configured: Set<int>) (source: string) (diagnostics: FSharpDiagnostic[]) : FSharpDiagnostic[] =
    let suppressed = allSuppressedCodes configured source

    diagnostics
    |> Array.filter (fun d ->
        d.Severity = FSharpDiagnosticSeverity.Error
        && not (suppressed.Contains d.ErrorNumber))
