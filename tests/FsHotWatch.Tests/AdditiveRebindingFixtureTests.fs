/// AUTOMATION-1900 step 1: real-compiler fixtures for the additive project-model narrowing.
///
/// The proposed rule narrows "no whole-project run under the current project model" when a
/// model change only ADDS compile items and every pre-existing file's resolved symbol uses are
/// identical to its analysis at the baseline tree T. F# resolves names in compile order, so an
/// inserted file can rebind an UNCHANGED later file with no text change there. The rule is
/// sound for a mechanism only if such a rebinding shows up as a change in the later file's
/// symbol-use set (`AdditiveModelChange.SymbolUses.ofAnalysis`), which is what each fixture
/// asks of the real compiler and the real analyser.
///
/// Every fixture checks one small project twice — before the insert and after it — and
/// asserts that both checks are free of compiler errors (a broken fixture would differ for
/// the wrong reason), that the program really resolves differently (the target the
/// unchanged file's use names), and what that does to the unchanged file's symbol-use set.
/// That each program resolves as described was confirmed by running both versions of every
/// fixture under `dotnet fsi`: each insert changes the consumer's result, except SRTP's.
module FsHotWatch.Tests.AdditiveRebindingFixtureTests

open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FsHotWatch.TestPrune.AdditiveModelChange
open FsHotWatch.Tests.TestHelpers
open TestPrune.AstAnalyzer

/// One file of a fixture project: its name and its text.
type private Source = { Name: string; Text: string }

/// The analysis of one checked file, with the compiler's errors for it.
type private Checked =
    { Uses: Set<SymbolUse>
      Errors: string list }

/// Check `sources`, in this compile order, as one project under `root`, and analyse each file
/// as the plugin does (`analyzeSourceFromResults`).
let private checkProject (root: string) (sources: Source list) : Map<string, Checked> =
    let dir = Path.Combine(root, "src", "P")
    Directory.CreateDirectory dir |> ignore

    let paths =
        sources
        |> List.map (fun source ->
            let path = Path.Combine(dir, source.Name)
            File.WriteAllText(path, source.Text)
            source.Name, path)

    let checker = sharedChecker.Value
    let first = snd (List.head paths)

    let options, _ =
        scriptProjectOptions (checker, first, FSharp.Compiler.Text.SourceText.ofString (File.ReadAllText first))
        |> Async.RunSynchronously

    let options =
        { options with
            ProjectFileName = Path.Combine(dir, "P.fsproj")
            SourceFiles = paths |> List.map snd |> List.toArray }

    paths
    |> List.map (fun (name, path) ->
        let text = File.ReadAllText path

        let parse, answer =
            checker.ParseAndCheckFileInProject(path, 0, FSharp.Compiler.Text.SourceText.ofString text, options)
            |> Async.RunSynchronously

        match answer with
        | FSharpCheckFileAnswer.Succeeded check ->
            let errors =
                check.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                |> Array.map (fun d -> $"%s{name}: FS%04d{d.ErrorNumber} %s{d.Message}")
                |> Array.toList

            match analyzeSourceFromResults path text parse check "P" with
            | Ok analysis ->
                name,
                { Uses = SymbolUses.ofAnalysis analysis
                  Errors = errors }
            | Error why -> failwith $"analysis of %s{name} failed: %s{why}"
        | FSharpCheckFileAnswer.Aborted -> failwith $"the check of %s{name} aborted")
    |> Map.ofList

/// The fixture before and after inserting `inserted` immediately before `consumer`.
let private beforeAndAfter
    (dir: string)
    (earlier: Source list)
    (inserted: Source)
    (consumer: Source)
    : Map<string, Checked> * Map<string, Checked> =
    let before = checkProject (Path.Combine(dir, "before")) (earlier @ [ consumer ])

    let after =
        checkProject (Path.Combine(dir, "after")) (earlier @ [ inserted; consumer ])

    before, after

/// Every compiler error of every checked file.
let private errorsOf (checks: Map<string, Checked>) =
    checks |> Map.toList |> List.collect (fun (_, c) -> c.Errors)

/// The targets the unchanged file's uses name from `from`.
let private targetsFrom (from: string) (uses: Set<SymbolUse>) =
    uses |> Set.filter (fun u -> u.From = from) |> Set.map _.To

[<Literal>]
let private Consumer = "Consumer.fs"

// --- Mechanism 1: an unqualified name, shadowed through a namespace already opened -------

/// A namespace is open to every file, so an inserted file can add a module to one the
/// consumer already opens. `open P.A` then `open P.B`: `Helpers.value` resolves to
/// `P.A.Helpers` until a file declaring `P.B.Helpers` comes before the consumer, and the
/// later open wins the unqualified name.
[<Fact(Timeout = 180000)>]
let ``SHADOWING: an inserted module in an already-opened namespace shadows an unqualified name, and the unchanged consumer's symbol uses change``
    ()
    =
    withTempDir "additive-shadow" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "namespace P.A\nmodule Helpers =\n    let value () = 1\n" }

        let other =
            { Name = "Other.fs"
              Text = "namespace P.B\nmodule Other =\n    let unrelated () = 0\n" }

        let inserted =
            { Name = "Shadow.fs"
              Text = "namespace P.B\nmodule Helpers =\n    let value () = 2\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nopen P.A\nopen P.B\nlet run () = Helpers.value ()\n" }

        let before, after = beforeAndAfter dir [ lib; other ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>

        test
            <@
                targetsFrom "P.Consumer.run" before[Consumer].Uses
                |> Set.contains "P.A.Helpers.value"
            @>

        test
            <@
                targetsFrom "P.Consumer.run" after[Consumer].Uses
                |> Set.contains "P.B.Helpers.value"
            @>

        test <@ before[Consumer].Uses <> after[Consumer].Uses @>)

// --- Mechanism 2: a new [<AutoOpen>] module ----------------------------------------------

/// `open P` after `open P.Lib` auto-opens every `[<AutoOpen>]` module of namespace `P`, so a
/// new one declaring `greet` shadows `P.Lib.greet` in a consumer whose text never changed.
[<Fact(Timeout = 180000)>]
let ``AUTOOPEN: an inserted AutoOpen module shadows a name the consumer opened from elsewhere, and the unchanged consumer's symbol uses change``
    ()
    =
    withTempDir "additive-autoopen" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "module P.Lib\nlet greet () = \"lib\"\n" }

        let inserted =
            { Name = "Auto.fs"
              Text = "namespace P\n[<AutoOpen>]\nmodule Auto =\n    let greet () = \"auto\"\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nopen P.Lib\nopen P\nlet run () = greet ()\n" }

        let before, after = beforeAndAfter dir [ lib ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>
        test <@ targetsFrom "P.Consumer.run" before[Consumer].Uses = set [ "P.Lib.greet" ] @>
        test <@ targetsFrom "P.Consumer.run" after[Consumer].Uses = set [ "P.Auto.greet" ] @>
        test <@ before[Consumer].Uses <> after[Consumer].Uses @>)

// --- Mechanism 3: extension members ------------------------------------------------------

/// The consumer opens the NAMESPACE `P.Extensions`, which any file can add to. A C#-style
/// extension method declared there by an inserted file is a better match for the call's
/// argument (`int` needs no coercion, `obj` does) than the one the consumer resolved before.
[<Fact(Timeout = 180000)>]
let ``EXTENSION MEMBER: an inserted extension method in an opened namespace wins the call, and the unchanged consumer's symbol uses change``
    ()
    =
    withTempDir "additive-extension" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "namespace P\ntype Counter() =\n    member _.Value = 1\n" }

        let existing =
            { Name = "ExtObj.fs"
              Text =
                "namespace P.Extensions\nopen System.Runtime.CompilerServices\n[<Extension>]\ntype ObjDescribe =\n    [<Extension>]\n    static member Describe(c: P.Counter, x: obj) = \"obj\"\n" }

        let inserted =
            { Name = "ExtInt.fs"
              Text =
                "namespace P.Extensions\nopen System.Runtime.CompilerServices\n[<Extension>]\ntype IntDescribe =\n    [<Extension>]\n    static member Describe(c: P.Counter, x: int) = \"int\"\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nopen P.Extensions\nlet run (c: P.Counter) = c.Describe 3\n" }

        let before, after = beforeAndAfter dir [ lib; existing ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>

        test
            <@
                targetsFrom "P.Consumer.run" before[Consumer].Uses
                |> Set.contains "P.Extensions.ObjDescribe.Describe"
            @>

        test
            <@
                targetsFrom "P.Consumer.run" after[Consumer].Uses
                |> Set.contains "P.Extensions.IntDescribe.Describe"
            @>

        test <@ before[Consumer].Uses <> after[Consumer].Uses @>)

/// An F# optional type extension in an inserted `[<AutoOpen>]` module adds a property the
/// consumer already reads through an older extension of the same name: the later-opened
/// extension wins the name.
[<Fact(Timeout = 180000)>]
let ``TYPE EXTENSION: an inserted F# type extension shadows an older extension property of the same name, and the unchanged consumer's symbol uses change``
    ()
    =
    withTempDir "additive-type-extension" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "namespace P\ntype Counter() =\n    member _.Value = 1\n" }

        let existing =
            { Name = "ExtA.fs"
              Text =
                "namespace P\n[<AutoOpen>]\nmodule ExtA =\n    type Counter with\n        member c.Label = \"a\"\n" }

        let inserted =
            { Name = "ExtB.fs"
              Text =
                "namespace P\n[<AutoOpen>]\nmodule ExtB =\n    type Counter with\n        member c.Label = \"b\"\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nlet run (c: P.Counter) = c.Label\n" }

        let before, after = beforeAndAfter dir [ lib; existing ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>

        test
            <@
                targetsFrom "P.Consumer.run" before[Consumer].Uses
                |> Set.contains "P.ExtA.Label"
            @>

        test <@ targetsFrom "P.Consumer.run" after[Consumer].Uses |> Set.contains "P.ExtB.Label" @>
        test <@ before[Consumer].Uses <> after[Consumer].Uses @>)

// --- Mechanism 4: overload resolution ----------------------------------------------------

/// `Printer.Show 3` resolves to the intrinsic `Show(obj)` until an inserted `[<AutoOpen>]`
/// extension adds `Show(int)`, which needs no coercion and so wins overload resolution: a
/// call to the same NAME on the same type now runs different code.
[<Fact(Timeout = 180000)>]
let ``OVERLOAD: an inserted extension overload wins overload resolution over an intrinsic one, and the unchanged consumer's symbol uses change``
    ()
    =
    withTempDir "additive-overload" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "namespace P\ntype Printer =\n    static member Show(x: obj) = \"obj\"\n" }

        let inserted =
            { Name = "Overload.fs"
              Text =
                "namespace P\n[<AutoOpen>]\nmodule PrinterInt =\n    type Printer with\n        static member Show(x: int) = \"int\"\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nlet run () = P.Printer.Show 3\n" }

        let before, after = beforeAndAfter dir [ lib ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>
        // The extension is named by the module that declares it, not by the type it extends,
        // so the rebound call names a different symbol even though the member name is one.
        test
            <@
                targetsFrom "P.Consumer.run" before[Consumer].Uses
                |> Set.contains "P.Printer.Show"
            @>

        test
            <@
                targetsFrom "P.Consumer.run" after[Consumer].Uses
                |> Set.contains "P.PrinterInt.Show"
            @>

        test
            <@
                not (
                    targetsFrom "P.Consumer.run" after[Consumer].Uses
                    |> Set.contains "P.Printer.Show"
                )
            @>

        test <@ before[Consumer].Uses <> after[Consumer].Uses @>)

// --- Mechanism 5: SRTP -------------------------------------------------------------------

/// An inline function's member constraint is solved at the call site. An inserted
/// `[<AutoOpen>]` extension of the same member name does not take part in that solution
/// (extension members do not satisfy member constraints), so the call keeps the intrinsic
/// member: nothing the insert could rebind, and nothing that must change.
[<Fact(Timeout = 180000)>]
let ``SRTP: an inserted extension of the constrained member does not rebind the call site, and the unchanged consumer's symbol uses stay identical``
    ()
    =
    withTempDir "additive-srtp" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text =
                "namespace P\ntype Counter() =\n    member _.Describe() = \"intrinsic\"\nmodule Srtp =\n    let inline describe (x: ^T) = (^T: (member Describe: unit -> string) x)\n" }

        let inserted =
            { Name = "SrtpExt.fs"
              Text =
                "namespace P\n[<AutoOpen>]\nmodule CounterExt =\n    type Counter with\n        member c.Describe() = \"extension\"\n" }

        let consumer =
            { Name = Consumer
              Text = "module P.Consumer\nlet run (c: P.Counter) = P.Srtp.describe c\n" }

        let before, after = beforeAndAfter dir [ lib ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>
        test <@ before[Consumer].Uses = after[Consumer].Uses @>)

// --- Control: an insert that rebinds nothing ---------------------------------------------

/// A new file whose names collide with nothing leaves every pre-existing file's symbol-use
/// set exactly as it was, so the proposed rule could narrow for it.
[<Fact(Timeout = 180000)>]
let ``CONTROL: an inserted file whose names collide with nothing leaves every pre-existing file's symbol uses identical``
    ()
    =
    withTempDir "additive-control" (fun dir ->
        let lib =
            { Name = "Lib.fs"
              Text = "namespace P\nmodule Helpers =\n    let value () = 1\ntype Counter() =\n    member _.Value = 1\n" }

        let auto =
            { Name = "Auto.fs"
              Text = "namespace P\n[<AutoOpen>]\nmodule Existing =\n    let greet () = \"existing\"\n" }

        let inserted =
            { Name = "Fresh.fs"
              Text =
                "namespace P.Fresh\n[<AutoOpen>]\nmodule Unrelated =\n    let brandNew () = 42\n    type P.Counter with\n        member c.Unused = 0\n" }

        let consumer =
            { Name = Consumer
              Text =
                "module P.Consumer\nopen P\nlet run (c: P.Counter) = Helpers.value () + c.Value + String.length (greet ())\n" }

        let before, after = beforeAndAfter dir [ lib; auto ] inserted consumer

        test <@ List.isEmpty (errorsOf before) && List.isEmpty (errorsOf after) @>

        for name in [ "Lib.fs"; "Auto.fs"; Consumer ] do
            test <@ before[name].Uses = after[name].Uses @>

        test <@ not (Set.isEmpty before[Consumer].Uses) @>)
