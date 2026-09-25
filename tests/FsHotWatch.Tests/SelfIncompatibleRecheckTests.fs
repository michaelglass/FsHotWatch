[<Xunit.Collection(FsHotWatch.Tests.TestHelpers.LogGlobalCollectionName)>]
module FsHotWatch.Tests.SelfIncompatibleRecheckTests

// A check whose answer declares a type incompatible with ITSELF is a fault in our
// checking, not a finding. These tests pin the three ways such a fault reached a
// verdict as an ordinary red: a message family the classifier did not know, a
// concurrent check that kept its answer because another file had spent the one drop
// the budget allowed, and the knock-on errors a poisoned check produces beside the
// self-incompatible one, which no message parse can recognise.

open System
open System.Threading
open Xunit
open Swensen.Unquote
open FsHotWatch
open FsHotWatch.ErrorLedger
open FsHotWatch.FcsDiagnosticFilter
open FsHotWatch.Cli
open FsHotWatch.Cli.IpcParsing
open FsHotWatch.Tests.TestHelpers

let private gs = string (char 0x1D)

let private doesNotMatch (left: string) (right: string) =
    $"The type '%s{left}' does not match the type '%s{right}'"

let private branchesMessage (left: string) (right: string) =
    $"All branches of a pattern match expression must return values implicitly convertible to the type of the first branch, which here is '%s{left}'. This branch returns a value of type '%s{right}'."

let private expectedButHas (expected: string) (actual: string) =
    $"This expression was expected to have type%s{gs}    '%s{expected}'    %s{gs}but here has type%s{gs}    '%s{actual}'    "

let private diagnostic (severity: FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity) (message: string) =
    FSharp.Compiler.Diagnostics.FSharpDiagnostic.Create(severity, message, 1, FSharp.Compiler.Text.Range.range0)

let private error =
    diagnostic FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error

// --- the two families the classifier missed ----------------------------------

[<Fact>]
let ``a type that does not match the type it IS is self-incompatible`` () =
    let message = doesNotMatch "Lib.Domain.Order" "Lib.Domain.Order"

    test <@ trySelfIncompatibleType message = Some "Lib.Domain.Order" @>
    // FCS may carry the padding as GROUP SEPARATOR (U+001D), which `\s` does not match.
    test <@ trySelfIncompatibleType (message.Replace(" '", gs + "'")) = Some "Lib.Domain.Order" @>

[<Fact>]
let ``a match whose branches disagree with a type that is the same type is self-incompatible`` () =
    let message = branchesMessage "Lib.Domain.Order" "Lib.Domain.Order"

    test <@ trySelfIncompatibleType message = Some "Lib.Domain.Order" @>
    test <@ trySelfIncompatibleType (message.Replace(". This", "." + gs + "This")) = Some "Lib.Domain.Order" @>

[<Fact>]
let ``the new families still report a genuine mismatch`` () =
    // POSITIVE CONTROL: a guard that swallows a real error is worse than the bug.
    test <@ tryRenderedTypePair (doesNotMatch "int" "string") = Some("int", "string") @>
    test <@ tryRenderedTypePair (branchesMessage "int" "string") = Some("int", "string") @>
    test <@ isSelfIncompatibleTypeMessage (doesNotMatch "int" "string") = false @>
    test <@ isSelfIncompatibleTypeMessage (branchesMessage "int" "string") = false @>

[<Fact>]
let ``a family with no constraint slot refuses a type variable on either side`` () =
    // These templates drop the constraint text the two-type rendering produces, so two
    // type variables named alike but constrained differently render identically there
    // with nothing to say they differ. Refused: the guard fails closed.
    test <@ tryRenderedTypePair (doesNotMatch "'a" "'a") = None @>
    test <@ tryRenderedTypePair (branchesMessage "Result<'a>" "Result<'a>") = None @>
    test <@ tryRenderedTypePair (doesNotMatch "^T" "^T") = None @>
    // A name that only ENDS in a prime is not a type variable.
    test <@ trySelfIncompatibleType (doesNotMatch "Foo'" "Foo'") = Some "Foo'" @>

[<Fact>]
let ``a new-family message with text after its last slot is refused`` () =
    test <@ tryRenderedTypePair (doesNotMatch "A.T" "A.T" + ". Something else") = None @>
    test <@ tryRenderedTypePair (branchesMessage "A.T" "A.T" + " More") = None @>

// --- re-checking without the budget when the generation moved on --------------

let private t0 = DateTime(2026, 9, 25, 17, 32, 34, DateTimeKind.Utc)

[<Fact>]
let ``with the budget spent, a check from a generation since dropped is re-checked in the current one`` () =
    let budget = RecheckBudget(TimeSpan.FromMinutes 5.0)
    let generation = ref 0L

    // Admin.fs's check finished first, spent the budget and dropped generation 0.
    let admin =
        budget.Decide(
            "App.fsproj",
            "Admin.fs",
            t0,
            0L,
            (fun () -> generation.Value),
            (fun () -> generation.Value <- 1L)
        )

    test <@ admin = RecheckOutcome.StateDropped @>

    // Jobs.fs started in generation 0 too, and finishes afterwards with the same fault.
    let drops = ref 0
    let logged = Collections.Concurrent.ConcurrentQueue<string>()

    let answer, outcome =
        recheckWithBudget
            budget
            "App.fsproj"
            "Jobs.fs"
            (fun () -> t0.AddSeconds 9.0)
            logged.Enqueue
            Seq.ofList
            0L
            (fun () -> generation.Value)
            (fun () -> incr drops)
            (fun () -> async { return [] })
            [ doesNotMatch "Order" "Order" ]
        |> Async.RunSynchronously

    test <@ List.isEmpty answer @>
    test <@ outcome = RecheckOutcome.RecheckedInCurrentGeneration(0L, 1L) @>
    test <@ drops.Value = 0 @>

[<Fact>]
let ``with the budget spent and the generation unmoved, the first answer is kept and the log says who spent it`` () =
    let budget = RecheckBudget(TimeSpan.FromMinutes 5.0)
    let generation = ref 1L

    // Admin.fs, in the current generation, spent the budget.
    test
        <@
            budget.Decide("App.fsproj", "Admin.fs", t0, 1L, (fun () -> generation.Value), ignore) = RecheckOutcome.StateDropped
        @>

    let logged = Collections.Concurrent.ConcurrentQueue<string>()
    let first = [ doesNotMatch "Order" "Order" ]

    let answer, outcome =
        recheckWithBudget
            budget
            "App.fsproj"
            "Jobs.fs"
            (fun () -> t0.AddSeconds 9.0)
            logged.Enqueue
            Seq.ofList
            1L
            (fun () -> generation.Value)
            (fun () -> failwith "must not drop")
            (fun () -> failwith "must not re-check")
            first
        |> Async.RunSynchronously

    test <@ answer = first @>
    test <@ outcome = RecheckOutcome.BudgetSpent("Admin.fs", t0) @>

    let line = logged |> Seq.exactlyOne

    test <@ line.Contains "Jobs.fs" && line.Contains "budget spent by Admin.fs at 17:32:34" @>
    test <@ line.Contains "first answer kept" @>

// --- a suspect check reports nothing about the code ----------------------------

let private knockOn = expectedButHas "InsertResult<OrderId>" "InsertResult<'a>"

let private suspectDiagnostics =
    [| error (doesNotMatch "Lib.Domain.Order" "Lib.Domain.Order")
       error knockOn
       error "Incomplete pattern matches on this expression." |]

/// The verdict a set of per-file ledger writes produces, through the daemon
/// transport's own projection (`IpcOutput.checkInputs`) and `CheckVerdict.verdict`.
let private verdictOf (files: (string * Daemon.FcsLedgerWrites) list) =
    let entries (plugin: string) (es: ErrorEntry list) =
        es
        |> List.map (fun e ->
            { Plugin = plugin
              Message = e.Message
              Severity = e.Severity
              Line = e.Line
              Column = e.Column
              Detail = e.Detail })

    let response: DiagnosticsResponse =
        { Count = 0
          Files =
            files
            |> List.map (fun (file, w) ->
                file,
                entries PluginActivity.FcsPluginName w.Findings
                @ entries PluginActivity.FcsInternalPluginName w.Internal)
            |> Map.ofList
          Statuses = Map.empty
          Coverage = Complete
          ProjectModel = ProjectModelFixtures.available }

    let inputs =
        IpcOutput.checkInputs false (BaselineFixtures.reportOf (FullSuite 1)) response

    let causes = IpcOutput.redCausesOf "logs/daemon.log" false response
    CheckVerdict.verdict CheckVerdict.InnerLoop inputs, causes

/// `names`, created empty in a directory of their own and passed to `body` as full
/// paths: real files, so the vanished-file classification cannot be what decides a
/// verdict here.
let private withFiles (names: string list) (body: string list -> 'a) : 'a =
    withTempDir "self-incompatible" (fun dir ->
        let paths = names |> List.map (fun name -> IO.Path.Combine(dir, name))

        for path in paths do
            IO.File.WriteAllText(path, "")

        body paths)

[<Fact>]
let ``a self-incompatible diagnostic makes every error of its file a checker fault, never a finding`` () =
    let writes = Daemon.fcsLedgerWrites Set.empty None "App.fsproj" suspectDiagnostics

    test <@ List.isEmpty writes.Findings @>
    test <@ writes.SelfIncompatible |> List.length = 1 @>

    // The knock-ons keep their severity: some of them might be real, and an `Info`
    // could let a broken file go green.
    let failing = writes.Internal |> List.filter (ErrorEntry.isFailing true)
    test <@ failing |> List.length = 2 @>

    withFiles [ "Jobs.fs" ] (fun paths ->
        let outcome, causes = verdictOf [ List.head paths, writes ]

        test <@ causes |> List.forall (fun c -> c.Kind = Verdict.CheckerFault) @>
        test <@ causes |> List.exists (fun c -> c.Source = PluginActivity.FcsPluginName) |> not @>
        // Neither red nor green: NO VERDICT, exit 3.
        test <@ outcome = CheckVerdict.CheckOutcome.StaleDaemonState 2 @>
        test <@ CheckVerdict.exitCode outcome = 3 @>)

[<Fact>]
let ``a genuine error beside a suspect file still reddens the run`` () =
    // POSITIVE CONTROL: suspicion is per FILE, and only moves that file's errors out
    // of "your code is broken"; a real error anywhere else is still a red.
    let suspect = Daemon.fcsLedgerWrites Set.empty None "App.fsproj" suspectDiagnostics

    let genuine =
        Daemon.fcsLedgerWrites Set.empty None "App.fsproj" [| error (expectedButHas "int" "string") |]

    test <@ genuine.Findings |> List.length = 1 @>
    test <@ List.isEmpty genuine.Internal @>

    withFiles [ "Jobs.fs"; "Real.fs" ] (fun paths ->
        let suspectFile, genuineFile = paths.[0], paths.[1]
        let outcome, causes = verdictOf [ suspectFile, suspect; genuineFile, genuine ]

        test <@ outcome = CheckVerdict.CheckOutcome.FailuresFound @>

        test
            <@
                causes
                |> List.filter (fun c -> c.Kind = Verdict.AboutThisTree)
                |> List.map _.File = [ genuineFile ]
            @>)

// --- N concurrent checks of one project -----------------------------------------

/// A stand-in for the checker: the answer a file gets depends only on the generation
/// of the project's checker state it is checked in, as with FCS, where a generation
/// names the cache keys every file of the project reads.
type private FakeChecker(poisoned: int64 -> bool, genuineIn: Set<string>) =
    let mutable generation = 0L
    let mutable drops = 0
    let mutable rechecks = 0

    member _.Generation = Volatile.Read(&generation)
    member _.Drops = Volatile.Read(&drops)
    member _.Rechecks = Volatile.Read(&rechecks)

    member _.Drop() =
        Interlocked.Increment(&drops) |> ignore
        Interlocked.Increment(&generation) |> ignore

    member _.Check(file: string, inGeneration: int64, isRecheck: bool) =
        async {
            if isRecheck then
                Interlocked.Increment(&rechecks) |> ignore

            return
                [| if poisoned inGeneration then
                       yield! suspectDiagnostics
                   if genuineIn.Contains file then
                       yield error (expectedButHas "int" "string") |]
        }

/// Check `files` of one project concurrently the way the pipeline does: each takes the
/// generation, gets its first answer, and only then — once EVERY file has its first
/// answer, which is the window the incident hit — goes through `recheckWithBudget`.
let private checkConcurrently (checker: FakeChecker) (files: string list) =
    let budget = RecheckBudget(TimeSpan.FromMinutes 5.0)
    let mutable waiting = List.length files

    let allAnswered =
        Tasks.TaskCompletionSource(Tasks.TaskCreationOptions.RunContinuationsAsynchronously)

    let checkOne (file: string) =
        async {
            let startedIn = checker.Generation
            let! first = checker.Check(file, startedIn, false)

            if Interlocked.Decrement(&waiting) = 0 then
                allAnswered.SetResult()

            do! Async.AwaitTask allAnswered.Task

            let! answer, outcome =
                recheckWithBudget
                    budget
                    "App.fsproj"
                    file
                    (fun () -> DateTime.UtcNow)
                    ignore
                    (Seq.map (fun (d: FSharp.Compiler.Diagnostics.FSharpDiagnostic) -> d.Message))
                    startedIn
                    (fun () -> checker.Generation)
                    checker.Drop
                    (fun () -> checker.Check(file, checker.Generation, true))
                    first

            return file, Daemon.fcsLedgerWrites Set.empty None "App.fsproj" answer, outcome
        }

    let all =
        files
        |> List.map (checkOne >> Async.StartAsTask)
        |> Array.ofList
        |> Tasks.Task.WhenAll

    test <@ all.Wait(TimeSpan.FromSeconds 60.0) @>
    List.ofArray all.Result

let private concurrentFiles = [ for i in 1..8 -> $"File%d{i}.fs" ]

[<Fact>]
let ``concurrent checks of a project whose state one of them dropped are all re-checked, and nothing reddens`` () =
    withFiles concurrentFiles (fun files ->
        withEveryPoolThreadBusy (fun () ->
            let checker = FakeChecker((fun generation -> generation = 0L), Set.empty)
            let results = checkConcurrently checker files

            // ONE drop, and every other file re-checked in the generation it started.
            test <@ checker.Drops = 1 @>
            test <@ checker.Rechecks = List.length files @>

            test
                <@
                    results
                    |> List.forall (fun (_, _, outcome) ->
                        match outcome with
                        | RecheckOutcome.StateDropped
                        | RecheckOutcome.RecheckedInCurrentGeneration(0L, 1L) -> true
                        | _ -> false)
                @>

            let outcome, causes = verdictOf [ for file, writes, _ in results -> file, writes ]

            test <@ List.isEmpty causes @>
            test <@ outcome <> CheckVerdict.CheckOutcome.FailuresFound @>))

[<Fact>]
let ``concurrent checks that stay poisoned after the drop give no verdict rather than a red or a green`` () =
    withFiles concurrentFiles (fun files ->
        let checker = FakeChecker((fun _ -> true), Set.empty)
        let results = checkConcurrently checker files
        let outcome, causes = verdictOf [ for file, writes, _ in results -> file, writes ]

        test <@ causes |> List.exists (fun c -> c.Source = PluginActivity.FcsPluginName) |> not @>

        match outcome with
        | CheckVerdict.CheckOutcome.StaleDaemonState _ -> ()
        | other -> failwith $"expected NO VERDICT, got %A{other}")

[<Fact>]
let ``a genuine type error in one of the concurrent files still reddens the run`` () =
    // POSITIVE CONTROL for the pipeline: the re-check cures the phantom, and the real
    // error the cured check still reports is a finding like any other.
    withFiles concurrentFiles (fun files ->
        let broken = List.head files
        let checker = FakeChecker((fun generation -> generation = 0L), Set.singleton broken)
        let results = checkConcurrently checker files
        let outcome, causes = verdictOf [ for file, writes, _ in results -> file, writes ]

        test <@ outcome = CheckVerdict.CheckOutcome.FailuresFound @>

        test
            <@
                causes |> List.map (fun c -> c.Source, c.File, c.Kind) = [ PluginActivity.FcsPluginName,
                                                                           broken,
                                                                           Verdict.AboutThisTree ]
            @>)
