/// No test makes a wait floored at `HangWaitMs` (30 s) under an xUnit cap at or below it.
///
/// `waitUntil` and every helper built on it never give up before `HangWaitMs`
/// (AUTOMATION-1364), so a 15 or 20 s cap over one of them is rule 2 of
/// `docs/wall-clock-bounds.md` broken: on a loaded box the cap fires first, and xUnit
/// reports an anonymous cancel where the wait would have named what never happened
/// (AUTOMATION-1494, AUTOMATION-1499). This scan finds every such test across the suites,
/// following helpers that wait through any number of calls.
module FsHotWatch.Tests.WaitFloorCapTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// One top-level binding of a test file: a helper, or a test with its cap.
type internal Binding =
    {
        File: string
        Line: int
        Name: string
        /// Visible only within its file: anything outside a `TestHelpers.fs`, or private.
        Private: bool
        /// A `let`; a member is called qualified, so its bare name says nothing.
        IsLet: bool
        IsTest: bool
        /// The cap expression as written, for a test with a `Timeout`.
        Cap: string option
        /// Every identifier its body uses, comments left out.
        Uses: Set<string>
    }

/// The waits that never give up before `HangWaitMs`.
let internal flooredWaits =
    set [ "waitUntil"; "waitUntilOr"; "waitUntilOrAtLeast"; "HangWait"; "HangWaitMs" ]

let private identifier = Regex(@"[A-Za-z_][A-Za-z0-9_']*")

let private testAttribute = Regex(@"^\s*\[<.*\b(Fact|Theory)\b")

let private timeout = Regex(@"\bTimeout\s*=\s*([^,)]+)")

let private header =
    Regex(
        @"^(?:static\s+)?(?:let|member|override)\s+(?:(?:private|internal|inline|rec|mutable|public)\s+)*(?:\w+\.)?(``[^`]+``|[A-Za-z_][A-Za-z0-9_']*)"
    )

/// The line without its `//` comment. A `//` after an odd number of quotes is in a string.
let internal codeOf (line: string) =
    let rec find (from: int) =
        match line.IndexOf("//", from, StringComparison.Ordinal) with
        | -1 -> line
        | i when (line.Substring(0, i) |> Seq.filter ((=) '"') |> Seq.length) % 2 = 0 -> line.Substring(0, i)
        | i -> find (i + 2)

    find 0

/// The top-level bindings of one file. A binding starts at a `let` or `member` in column 0,
/// or indented four spaces directly inside a nested `module … =` or `type … =` (a nested
/// module's or a test class's own), and runs to the next such line, attribute, type or
/// module. Test attributes give the binding after them its cap.
let internal bindingsOf (file: string) (lines: string[]) : Binding list =
    let bindings = ResizeArray<Binding>()
    let mutable current: Binding option = None
    let mutable pendingTest = false
    let mutable pendingCap: string option = None

    let close () =
        current |> Option.iter bindings.Add
        current <- None

    let uses (text: string) =
        identifier.Matches(codeOf text) |> Seq.map _.Value |> Set.ofSeq

    // Inside a triple-quoted string: source text a test embeds is body, not bindings.
    let mutable inVerbatim = false
    // Inside a nested module or type opened in column 0, whose bindings sit at indent 4.
    let mutable nested = false

    for i in 0 .. lines.Length - 1 do
        let line = lines[i]
        let trimmed = line.TrimStart()
        let indent = line.Length - trimmed.Length
        let code = not inVerbatim && trimmed.Length > 0 && not (trimmed.StartsWith "//")

        if code && indent = 0 then
            nested <-
                (trimmed.StartsWith "type "
                 || trimmed.StartsWith "module "
                 || trimmed.StartsWith "and ")
                && trimmed.TrimEnd().EndsWith "="

        let topLevel = code && (indent = 0 || (nested && indent = 4))

        if (Regex.Matches(line, "\"\"\"").Count % 2) = 1 then
            inVerbatim <- not inVerbatim

        if topLevel && trimmed.StartsWith "[<" then
            close ()

            if testAttribute.IsMatch line then
                pendingTest <- true
                let m = timeout.Match line

                pendingCap <-
                    if m.Success then
                        Some(m.Groups[1].Value.Trim())
                    else
                        pendingCap
        elif topLevel && header.IsMatch trimmed then
            close ()
            let m = header.Match trimmed

            current <-
                Some
                    { File = file
                      Line = i + 1
                      Name = m.Groups[1].Value
                      Private =
                        Regex.IsMatch(trimmed, @"^(?:let|member)\s+private\b")
                        || Path.GetFileName file <> "TestHelpers.fs"
                      IsLet = trimmed.StartsWith "let "
                      IsTest = pendingTest
                      Cap = pendingCap
                      Uses = uses (trimmed.Substring m.Length) }

            pendingTest <- false
            pendingCap <- None
        elif
            topLevel
            && (trimmed.StartsWith "type "
                || trimmed.StartsWith "module "
                || trimmed.StartsWith "and ")
        then
            close ()
            pendingTest <- false
            pendingCap <- None
        else
            match current with
            | Some binding when not (trimmed.StartsWith "//") ->
                current <-
                    Some
                        { binding with
                            Uses = Set.union binding.Uses (uses line) }
            | _ -> ()

    close ()
    List.ofSeq bindings

/// Every helper that waits on a floored wait, through any number of calls. A public helper
/// in a `TestHelpers.fs` is known by name across the suites; every other helper only within
/// its own file, where a same-named local elsewhere cannot be mistaken for it.
let internal waitingHelpers (bindings: Binding list) : Set<string> * Map<string, Set<string>> =
    let helpers = bindings |> List.filter (fun b -> not b.IsTest && b.IsLet)

    let rec grow (shared: Set<string>) (byFile: Map<string, Set<string>>) =
        let visible (b: Binding) =
            Set.union shared (Map.tryFind b.File byFile |> Option.defaultValue Set.empty)

        let fresh =
            helpers
            |> List.filter (fun b ->
                let known = visible b
                not (known.Contains b.Name) && not (Set.isEmpty (Set.intersect b.Uses known)))

        if List.isEmpty fresh then
            shared, byFile
        else
            let shared' =
                fresh
                |> List.filter (fun b -> not b.Private)
                |> List.fold (fun s b -> Set.add b.Name s) shared

            let byFile' =
                fresh
                |> List.filter _.Private
                |> List.fold
                    (fun m b ->
                        let own = Map.tryFind b.File m |> Option.defaultValue Set.empty
                        Map.add b.File (Set.add b.Name own) m)
                    byFile

            grow shared' byFile'

    grow flooredWaits Map.empty

/// A cap expression's value in ms: a literal, a constant (qualified or not), or a product
/// or sum of those. `None` when it cannot be read.
let rec internal capValue (constants: Map<string, int>) (expression: string) : int option =
    let expression = expression.Trim().Trim('(', ')').Trim()

    let split (op: char) =
        match expression.IndexOf op with
        | -1 -> None
        | i -> Some(expression.Substring(0, i), expression.Substring(i + 1))

    match split '+' with
    | Some(a, b) -> Option.map2 (+) (capValue constants a) (capValue constants b)
    | None ->
        match split '*' with
        | Some(a, b) -> Option.map2 (*) (capValue constants a) (capValue constants b)
        | None ->
            match Int32.TryParse(expression.Replace("_", "")) with
            | true, ms -> Some ms
            | _ -> Map.tryFind (expression.Split('.') |> Array.last) constants

let private constant =
    Regex(@"^\s*let\s+(?:(?:private|internal)\s+)?([A-Za-z_]\w*)\s*=\s*(.+?)\s*$")

/// `known` extended by every constant `lines` binds whose value can be read from `known`
/// and from each other, in any order: `let private RunsTestCapMs = 3 * SyncHangBackstopMs`.
let internal constantsOf (known: Map<string, int>) (lines: string seq) : Map<string, int> =
    let bound =
        lines
        |> Seq.choose (fun line ->
            let m = constant.Match line

            if m.Success then
                Some(m.Groups[1].Value, m.Groups[2].Value)
            else
                None)
        |> List.ofSeq

    let rec resolve (constants: Map<string, int>) =
        let fresh =
            bound
            |> List.choose (fun (name, expression) ->
                if constants.ContainsKey name then
                    None
                else
                    capValue constants expression |> Option.map (fun ms -> name, ms))

        match fresh with
        | [] -> constants
        | _ -> resolve (fresh |> List.fold (fun m (name, ms) -> Map.add name ms m) constants)

    resolve known

/// Each test that makes a floored wait under a cap at or below `HangWaitMs`, or under a
/// cap the scan cannot read, as `file:line: Timeout = cap`. `constantsFor` gives the
/// constants a cap in a file may name.
let internal capsUnderWaits (constantsFor: string -> Map<string, int>) (bindings: Binding list) : string list =
    let shared, byFile = waitingHelpers bindings

    bindings
    |> List.choose (fun b ->
        match b.IsTest, b.Cap with
        | true, Some cap ->
            let known =
                Set.union shared (Map.tryFind b.File byFile |> Option.defaultValue Set.empty)

            let waits = not (Set.isEmpty (Set.intersect b.Uses known))

            match capValue (constantsFor b.File) cap with
            | Some ms when waits && ms <= FsHotWatch.Tests.TestHelpers.HangWaitMs ->
                Some $"%s{b.File}:%d{b.Line}: Timeout = %s{cap}"
            | None when waits -> Some $"%s{b.File}:%d{b.Line}: Timeout = %s{cap} (unreadable cap)"
            | _ -> None
        | _ -> None)

[<Fact>]
let ``no test makes a wait floored at HangWaitMs under a cap at or below it`` () =
    let root = repoRoot ()

    let files =
        FsHotWatch.Tests.TestHelpers.filesUnder
            FsHotWatch.SafeWalk.SourceExcludedDirs
            "*.fs"
            (Path.Combine(root, "tests"))
        |> List.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
        |> List.filter (fun (relative, _) -> not (relative.Contains "/obj/" || relative.Contains "/bin/"))

    let text =
        files |> List.map (fun (relative, path) -> relative, File.ReadAllLines path)

    let shared =
        text
        |> List.filter (fun (relative, _) -> Path.GetFileName relative = "TestHelpers.fs")
        |> Seq.collect snd
        |> constantsOf Map.empty

    let byFile =
        text
        |> List.map (fun (relative, lines) -> relative, constantsOf shared lines)
        |> Map.ofList

    let under =
        text
        |> List.collect (fun (relative, lines) -> bindingsOf relative lines)
        |> capsUnderWaits (fun file -> Map.tryFind file byFile |> Option.defaultValue shared)

    if not (List.isEmpty under) then
        Assert.Fail(
            $"%d{under.Length} test(s) wait at least HangWaitMs (30 s) under a cap that does not exceed it, so a \
              loaded box cancels them before the wait can report (docs/wall-clock-bounds.md, rule 2). Use \
              SyncHangBackstopMs, or the named cap for the work's own bound:\n"
            + String.Join("\n", under)
        )

let private sample =
    [| "let private settle host ="
       "    waitUntil (fun () -> true) 5000"
       ""
       "let viaSettle host = settle host"
       ""
       "[<Fact(Timeout = 20000)>]"
       "let ``short cap over a helper that waits`` () ="
       "    viaSettle ()"
       ""
       "[<Fact(Timeout = SyncHangBackstopMs)>]"
       "let ``named cap above the floor`` () ="
       "    viaSettle ()"
       ""
       "[<Fact(Timeout = 15000)>]"
       "let ``short cap, no wait`` () ="
       "    // waitUntil only in a comment"
       "    ignore ()"
       ""
       "type Cases() ="
       "    [<Fact(Timeout = 2 * HangWaitMs / 4)>]"
       "    member _.``unreadable cap over a wait`` () = settle ()" |]

[<Fact>]
let ``a test is flagged through helpers that wait, and only when its cap does not exceed the floor`` () =
    let constants = Map.ofList [ "SyncHangBackstopMs", 60_000; "HangWaitMs", 30_000 ]
    let found = capsUnderWaits (fun _ -> constants) (bindingsOf "S.fs" sample)

    test
        <@
            found = [ "S.fs:7: Timeout = 20000"
                      "S.fs:21: Timeout = 2 * HangWaitMs / 4 (unreadable cap)" ]
        @>

[<Fact>]
let ``a private helper that waits is visible only in its own file`` () =
    let other =
        bindingsOf
            "T.fs"
            [| "[<Fact(Timeout = 20000)>]"
               "let ``calls a same-named helper`` () ="
               "    settle ()" |]

    let found = capsUnderWaits (fun _ -> Map.empty) (bindingsOf "S.fs" sample @ other)
    test <@ not (found |> List.exists (fun f -> f.StartsWith "T.fs")) @>

[<Theory>]
[<InlineData("20000", 20000)>]
[<InlineData("TestHelpers.SyncHangBackstopMs", 60000)>]
[<InlineData("2 * HangWaitMs", 60000)>]
[<InlineData("HangWaitMs + 1_000", 31000)>]
let ``a cap expression is read`` (expression: string) (expected: int) =
    let constants = Map.ofList [ "SyncHangBackstopMs", 60_000; "HangWaitMs", 30_000 ]
    test <@ capValue constants expression = Some expected @>

[<Fact>]
let ``a comment after code is not code, and a // in a string is`` () =
    test <@ codeOf "    foo () // waitUntil" = "    foo () " @>
    test <@ codeOf "    run \"http://x\" // waitUntil" = "    run \"http://x\" " @>

[<Fact>]
let ``a public helper in a TestHelpers file is known across files, and a public helper elsewhere is not`` () =
    let helpers =
        bindingsOf "T/TestHelpers.fs" [| "let awaitIt () ="; "    waitUntil (fun () -> true) 1" |]

    let elsewhere =
        bindingsOf "S.fs" [| "let awaitToo () ="; "    waitUntil (fun () -> true) 1" |]

    let uses =
        bindingsOf
            "U.fs"
            [| "[<Fact(Timeout = 15000)>]"
               "let ``via TestHelpers`` () ="
               "    awaitIt ()"
               "[<Fact(Timeout = 15000)>]"
               "let ``via another test file`` () ="
               "    awaitToo ()" |]

    test <@ capsUnderWaits (fun _ -> Map.empty) (helpers @ elsewhere @ uses) = [ "U.fs:2: Timeout = 15000" ] @>

[<Fact>]
let ``a file's own constants are read, in any order, over the shared ones`` () =
    let shared = Map.ofList [ "SyncHangBackstopMs", 60_000 ]

    let own =
        constantsOf
            shared
            [| "let private RunsTestCapMs = 3 * BaseMs"
               "let private BaseMs = 20_000"
               "let private Label = \"not a number\"" |]

    test <@ own = Map.ofList [ "SyncHangBackstopMs", 60_000; "BaseMs", 20_000; "RunsTestCapMs", 60_000 ] @>
