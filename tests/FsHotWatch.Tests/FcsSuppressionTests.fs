/// `FcsSuppression` must drop exactly what the real compiler's `#nowarn` suppresses: a
/// warning, including one `--warnaserror` promotes, and never a genuine error. FCS does
/// not say whether an Error-severity diagnostic was promoted, so the promotion is read
/// from the file's compiler flags. Every case checks a real source with the warm checker
/// and asserts through the analysis evidence a receipt is graded from, so what is pinned
/// is what reaches a verdict.
module FsHotWatch.Tests.FcsSuppressionTests

open System.IO
open Xunit
open FsHotWatch
open FsHotWatch.Events
open FsHotWatch.CheckPipeline
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer
open FsHotWatch.FcsSuppression

/// `source` checked as a script with `flags` appended to its compiler options, stamped
/// as captured against model generation 1.
let private checkedWith (flags: string list) (source: string) =
    withTempDir "fcs-suppression" (fun root ->
        let file = Path.Combine(root, "Lib.fsx")
        File.WriteAllText(file, source)
        let checker = sharedChecker.Value
        let pipeline = CheckPipeline(checker)
        let options = getScriptOptions checker file source |> Async.RunSynchronously

        pipeline.RegisterProject(
            file,
            { options with
                OtherOptions = Array.append options.OtherOptions (Array.ofList flags) }
        )

        let result =
            pipeline.CheckFile(AbsFilePath.create file)
            |> Async.RunSynchronously
            |> Option.defaultWith (fun () -> failwith "FCS returned no check result")

        { result with
            ModelGeneration = Some 1L })

let private errorCodes (result: FileCheckResult) =
    match result.CheckResults with
    | FullCheck checkedResult ->
        checkedResult.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
        |> Array.map (fun d -> d.ErrorNumber)
        |> Set.ofArray
    | ParseOnly -> failwith "the fixture requires an actual completed type check"

/// What the receipt refuses the file for.
let private refusals (result: FileCheckResult) =
    (AnalysisEvidence.fromCompleted
        (Some 1L)
        (Set.singleton result.File)
        (Map.ofList [ result.File, AnalysisFileEvidence.fromResult Set.empty result (Ok()) ])
     |> Option.get)
        .FailureReasons

/// A genuine FS0001 type mismatch.
let private mismatch = "let x : int = \"not-an-int\"\n"

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a genuine error beside #nowarn for a different code still blocks`` () =
    let result = checkedWith [] ("#nowarn \"57\"\nmodule Lib\n" + mismatch)
    // Positive control: FCS reports the mismatch at Error severity.
    Assert.Contains(1, errorCodes result)
    Assert.NotEmpty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``#nowarn naming a genuine error's own code does not suppress it`` () =
    // The real compiler fails this file: #nowarn silences warnings, and FS0001 here is an
    // error, since nothing promotes it.
    let result = checkedWith [] ("#nowarn \"1\"\nmodule Lib\n" + mismatch)
    Assert.Contains(1, errorCodes result)
    Assert.NotEmpty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a promotion list that leaves a code out does not make #nowarn suppress it`` () =
    let result =
        checkedWith [ "--warnaserror+:25" ] ("#nowarn \"1\"\nmodule Lib\n" + mismatch)

    Assert.Contains(1, errorCodes result)
    Assert.NotEmpty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a code excluded from a global promotion is not suppressed by #nowarn`` () =
    let result =
        checkedWith [ "--warnaserror+"; "--warnaserror-:1" ] ("#nowarn \"1\"\nmodule Lib\n" + mismatch)

    Assert.Contains(1, errorCodes result)
    Assert.NotEmpty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a promoted warning the file silences with #nowarn does not block`` () =
    // FS0025 (incomplete match) is a warning that --warnaserror+ makes an Error. Whether
    // FCS honours the #nowarn itself varies by checker path (dotnet/fsharp#9796); either
    // way the receipt must not refuse it.
    let result =
        checkedWith [ "--warnaserror+" ] "#nowarn \"25\"\nmodule Lib\nlet choose value = match value with | true -> 1\n"

    Assert.Empty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``KNOWN GAP: under a global promotion, #nowarn naming a genuine error's code still suppresses it`` () =
    // FCS cannot say whether an Error was promoted, so under a global --warnaserror+ a
    // genuine FS0001 and a promoted warning are the same diagnostic. Closing this means
    // re-checking the file without promotion before dropping anything, which was deferred.
    // A change here is a decision, not a side effect: update the CHANGELOG with it.
    let result =
        checkedWith [ "--warnaserror+" ] ("#nowarn \"1\"\nmodule Lib\n" + mismatch)

    Assert.Contains(1, errorCodes result)
    Assert.Empty(refusals result)

[<Fact(Timeout = RealCheckTestCapMs)>]
let ``a warning nothing promotes passes through the filter as no refusal`` () =
    let result =
        checkedWith [] "module Lib\nlet choose value = match value with | true -> 1\n"

    // Positive control: FCS reports the incomplete match, at Warning severity.
    match result.CheckResults with
    | FullCheck checkedResult ->
        Assert.Contains(
            checkedResult.Diagnostics,
            fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning
        )
    | ParseOnly -> Assert.Fail "the fixture requires an actual completed type check"

    Assert.Empty(errorCodes result)
    Assert.Empty(refusals result)

// The flag reading, without a compiler: each form fsc accepts, in order.

[<Fact>]
let ``a listed code is promoted whether or not it carries the FS prefix, and a non-FS code is skipped`` () =
    let promoted = promotedBy [ "--warnaserror+:FS0057,25;NU1605" ]
    Assert.True(promoted 57)
    Assert.True(promoted 25)
    Assert.False(promoted 1605)
    // Listing codes does not switch the global promotion on.
    Assert.False(promoted 1)

[<Fact>]
let ``the bare and colon forms promote, and a later global switch-off wins`` () =
    Assert.True(promotedBy [ "--warnaserror" ] 1)
    Assert.True(promotedBy [ "--warnaserror:25" ] 25)
    Assert.False(promotedBy [ "--warnaserror+"; "--warnaserror-" ] 1)
    // A code listed again after its exemption is promoted again.
    Assert.True(promotedBy [ "--warnaserror+"; "--warnaserror-:25"; "--warnaserror+:25" ] 25)
    Assert.False(promotedBy [] 1)

[<Fact>]
let ``#nowarn silences a warning whatever the flags, and an error only when its code is promoted`` () =
    let nowarn1 =
        { Suppression.none with
            Nowarn = Set.singleton 1 }

    Assert.True(Suppression.silences nowarn1 false 1)
    Assert.False(Suppression.silences nowarn1 true 1)
    Assert.False(Suppression.silences nowarn1 false 2)

    Assert.True(Suppression.silences { nowarn1 with Promoted = (=) 1 } true 1)

    // A configured code is silenced at any severity, with no #nowarn.
    Assert.True(
        Suppression.silences
            { Suppression.none with
                Configured = Set.singleton 3 }
            true
            3
    )
