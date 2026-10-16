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
/// It must drop only what the real compiler's `#nowarn` drops: warnings, including the
/// ones `--warnaserror` promotes, and never an error in its own right
/// (`Suppression.silences`).
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

/// Which warning codes compiler `flags` promote to errors, read the way fsc reads them,
/// in order: `--warnaserror[+|-]` sets the global switch, `--warnaserror[+]:N,…` adds codes
/// and `--warnaserror-:N,…` exempts them. A code is promoted when it is listed, or when
/// the global switch is on and it is not exempt. A non-numeric code (`NU1605`) is no FS code
/// and is skipped; an `FS` prefix is read off.
let promotedBy (flags: string seq) : int -> bool =
    let codes (list: string) =
        list.Split([| ','; ';' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun code ->
            let code = code.Trim()

            let digits =
                if code.StartsWith("FS", System.StringComparison.OrdinalIgnoreCase) then
                    code.Substring 2
                else
                    code

            match System.Int32.TryParse digits with
            | true, n -> Some n
            | _ -> None)
        |> Set.ofArray

    let globalOn, listed, exempt =
        flags
        |> Seq.fold
            (fun (globalOn, listed: Set<int>, exempt: Set<int>) (flag: string) ->
                match flag with
                | "--warnaserror"
                | "--warnaserror+" -> true, listed, exempt
                | "--warnaserror-" -> false, listed, exempt
                | _ when flag.StartsWith "--warnaserror-:" ->
                    let these = codes (flag.Substring "--warnaserror-:".Length)
                    globalOn, Set.difference listed these, Set.union exempt these
                | _ when flag.StartsWith "--warnaserror+:" ->
                    let these = codes (flag.Substring "--warnaserror+:".Length)
                    globalOn, Set.union listed these, Set.difference exempt these
                | _ when flag.StartsWith "--warnaserror:" ->
                    let these = codes (flag.Substring "--warnaserror:".Length)
                    globalOn, Set.union listed these, Set.difference exempt these
                | _ -> globalOn, listed, exempt)
            (false, Set.empty, Set.empty)

    fun code -> listed.Contains code || (globalOn && not (exempt.Contains code))

/// What suppresses one file's diagnostics.
[<NoComparison; NoEquality>]
type Suppression =
    {
        /// The host's configured codes (`fcsSuppressedCodes`): an operator's decision,
        /// applied at any severity.
        Configured: Set<int>
        /// The file's own `#nowarn` codes.
        Nowarn: Set<int>
        /// Whether the file's compiler flags promote a warning code to an error.
        Promoted: int -> bool
    }

module Suppression =
    /// Nothing suppressed.
    let none =
        { Configured = Set.empty
          Nowarn = Set.empty
          Promoted = fun _ -> false }

    /// The suppression for a file with `source`, checked under compiler `flags`.
    let forFile (configured: Set<int>) (flags: string seq) (source: string) =
        { Configured = configured
          Nowarn = parseNowarnCodes source
          Promoted = promotedBy flags }

    /// Does `suppression` silence a diagnostic with `code`? The real compiler's `#nowarn`
    /// silences a warning, including one its flags promote to an error, and never an error
    /// in its own right. FCS does not say which an Error-severity diagnostic is, so it is
    /// read from the flags: an Error is silenced by `#nowarn` only when the flags promote
    /// its code. Under a global `--warnaserror+` that still silences a genuine error
    /// whose own code the file names, which the real compiler would report.
    let silences (suppression: Suppression) (isError: bool) (code: int) =
        suppression.Configured.Contains code
        || (suppression.Nowarn.Contains code && (not isError || suppression.Promoted code))

/// The Error-severity diagnostics in `diagnostics` that `suppression` does not silence.
let blockingErrors (suppression: Suppression) (diagnostics: FSharpDiagnostic[]) : FSharpDiagnostic[] =
    diagnostics
    |> Array.filter (fun d ->
        d.Severity = FSharpDiagnosticSeverity.Error
        && not (Suppression.silences suppression true d.ErrorNumber))
