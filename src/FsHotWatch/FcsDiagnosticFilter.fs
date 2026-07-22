/// Shared FCS diagnostic suppression helpers used by both the user-visible
/// error-reporting path (`FsHotWatch.Daemon.reportFcsDiagnostics`) and the
/// TestPrune cache-poisoning gate (`FsHotWatch.TestPrune.TestPrunePlugin.hasFcsErrors`).
///
/// Keeping these in one module guarantees the two paths agree on which codes are
/// considered "noise": an asymmetry between them trips the gate on phantom errors in
/// projects that rely on `<TreatWarningsAsErrors>` + `#nowarn` directives.
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
