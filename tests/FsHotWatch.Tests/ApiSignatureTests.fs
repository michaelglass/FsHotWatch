/// The API signature of a file: what the files after it, and the projects that reference
/// its project, can type-check against.
module FsHotWatch.Tests.ApiSignatureTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Events
open FsHotWatch.Tests.TestHelpers

/// A file exercising every kind of declaration a later file can observe.
let private api =
    String.concat
        "\n"
        [ "module Api"
          "open System"
          "type Shape = | Circle of radius: float | Square of side: float"
          "type Point = { X: int; mutable Y: int }"
          "type Box<'a> = { Value: 'a }"
          "type Id = int"
          "type Colour = Red = 1 | Green = 2"
          "exception Boom of string"
          "type internal Hidden = { H: int }"
          "let internal hidden : Hidden = { H = 0 }"
          "type IGreeter = abstract Greet: string -> string"
          "type Greeter(prefix: string) ="
          "    member _.Prefix = prefix"
          "    member internal _.Count = prefix.Length"
          "    member _.Hello(name: string, ?times: int) = prefix + name"
          "    interface IGreeter with"
          "        member _.Greet name = prefix + name"
          "[<Literal>]"
          "let Answer = 42"
          "let mutable counter = 0"
          "[<Obsolete(\"use area\")>]"
          "let size (s: Shape) = match s with Circle r -> r | Square d -> d"
          "let (|Even|_|) (n: int) = if n % 2 = 0 then Some n else None"
          "let equal (a: 'a) (b: 'a) = a = b"
          "let anon : {| A: int; B: string |} = {| A = 1; B = \"b\" |}"
          "let pair : struct (int * string) = struct (1, \"a\")"
          "let dispose (x: 'T when 'T :> System.IDisposable) = x.Dispose()"
          "let asEnum<'T when 'T : enum<int>> (x: 'T) = x"
          "let asDelegate<'T when 'T : delegate<obj * System.EventArgs, unit>> (x: 'T) = x"
          "let describe (n: int) ="
          "    let local = n * 2"
          "    string local"
          "type System.String with"
          "    member s.Shout = s.ToUpperInvariant()"
          "" ]

let private signatureOf (root: string) (source: string) =
    let check = checkAlone root "Api.fs" source

    let errors =
        check.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    test <@ Array.isEmpty errors @>
    FsHotWatch.ApiSignature.ofCheck source (FullCheck check)

/// `api` with `before` replaced by `after`; the fragment must occur exactly once.
let private edit (source: string) (before: string, after: string) =
    let at = source.IndexOf(before, StringComparison.Ordinal)
    test <@ at >= 0 && source.IndexOf(before, at + 1, StringComparison.Ordinal) < 0 @>
    source.Substring(0, at) + after + source.Substring(at + before.Length)

let private signaturesAcross (source: string) (edits: (string * string) list) =
    withTempDir "api-signature" (fun root ->
        let baseline = signatureOf root source
        test <@ baseline.IsSome @>
        baseline, [ for e in edits -> e, signatureOf root (edit source e) ])

// One real check per edit plus the baseline, each under its own `RealCheckWaitMs`.
[<Fact(Timeout = 21 * RealCheckWaitMs)>]
let ``every declaration change a later file can observe moves the signature`` () =
    let baseline, edited =
        signaturesAcross
            api
            [ "| Square of side: float", "| Square of side: float | Triangle of float"
              "mutable Y: int", "Y: int"
              "type Id = int", "type Id = string"
              "Green = 2", "Green = 3"
              "exception Boom of string", "exception Boom of string * int"
              "type internal Hidden = { H: int }", "type internal Hidden = { H: int64 }\nlet _unused = 0"
              "member internal _.Count = prefix.Length", "member internal _.Count = prefix"
              "?times: int", "?times: int64"
              "let Answer = 42", "let Answer = 43"
              "use area", "use perimeter"
              "let mutable counter = 0", "let counter = 0"
              "then Some n else", "then Some(string n) else"
              "let equal (a: 'a) (b: 'a) = a = b", "let equal (a: 'a) (b: 'a) = obj.ReferenceEquals(a, b)"
              "member s.Shout = s.ToUpperInvariant()", "member s.Shout = s.Length"
              "    member _.Prefix = prefix\n", "    member _.Prefix = prefix\n    member _.Added = 0\n"
              "B: string |} = {| A = 1; B =", "C: string |} = {| A = 1; C ="
              ":> System.IDisposable)", ":> System.IDisposable and 'T : equality)"
              "enum<int>", "enum<int64>"
              "struct (int * string) = struct (1, \"a\")", "int * string = 1, \"a\""
              "System.EventArgs, unit>", "System.EventArgs, int>" ]

    let unchanged =
        edited
        |> List.filter (fun (_, signature) -> signature = baseline)
        |> List.map fst

    test <@ List.isEmpty unchanged @>

// One real check per edit plus the baseline, each under its own `RealCheckWaitMs`.
[<Fact(Timeout = 8 * RealCheckWaitMs)>]
let ``comments, whitespace and bodies leave the signature alone`` () =
    let baseline, edited =
        signaturesAcross
            api
            [ "module Api\n", "module Api\n// every later declaration moves down a line\n\n"
              "Circle r -> r |", "Circle r -> r * 1.0 |"
              "member _.Greet name = prefix + name", "member _.Greet name = name + prefix"
              "let mutable counter = 0", "let mutable counter = 1"
              "    let local = n * 2\n    string local",
              "    let local = n * 3 + 1\n    let other = local\n    string other"
              "let size", "  \nlet size"
              "member _.Hello(name: string, ?times: int) = prefix + name",
              "member _.Hello(name: string, ?times: int) =  name + prefix" ]

    let changed =
        edited
        |> List.filter (fun (_, signature) -> signature <> baseline)
        |> List.map fst

    test <@ List.isEmpty changed @>

[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a file with an inline member has no signature of its own, so its content stands in`` () =
    // An inline member's body is inlined into its callers: a body edit can change what a
    // later file compiles to, so no declaration-only hash may stand for it.
    withTempDir "api-signature-inline" (fun root ->
        let inlined = "module Api\nlet inline twice (x: int) = x + x\n"
        test <@ signatureOf root inlined = None @>
        test <@ signatureOf root (edit inlined ("x + x", "x * 2")) = None @>)

[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a file with errors, or without typed results, has no signature`` () =
    withTempDir "api-signature-errors" (fun root ->
        let broken = "module Api\nlet broken : int = \"text\"\n"
        let check = checkAlone root "Api.fs" broken
        test <@ FsHotWatch.ApiSignature.ofCheck broken (FullCheck check) = None @>
        test <@ FsHotWatch.ApiSignature.ofCheck broken ParseOnly = None @>
        test <@ FsHotWatch.ApiSignature.ofCheck broken (FullCheck Unchecked.defaultof<_>) = None @>

        // An FSharpCheckFileResults with no state throws from every query, as FCS can from
        // a symbol property on partially-checked code.
        let stateless =
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
            )
            :?> FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults

        test <@ FsHotWatch.ApiSignature.ofCheck "module Api\n" (FullCheck stateless) = None @>)

[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a namespace file has a signature, moved by its types`` () =
    withTempDir "api-signature-namespace" (fun root ->
        let source = "namespace Api.Space\ntype T = { V: int }\n"
        let baseline = signatureOf root source
        test <@ baseline.IsSome @>
        test <@ signatureOf root (edit source ("{ V: int }", "{ V: int64 }")) <> baseline @>)

[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a file with an assembly attribute has no signature of its own`` () =
    // `InternalsVisibleTo` decides what a referencing project may see, and FCS does not
    // report an assembly attribute as a declaration of the file.
    withTempDir "api-signature-assembly" (fun root ->
        let source =
            "namespace Api\n[<assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"A\")>]\ndo ()\ntype T = { V: int }\n"

        test <@ signatureOf root source = None @>)

/// A downstream file using `api`'s declarations, the way a test project uses its subject.
let private consumer =
    String.concat
        "\n"
        [ "module Consumer"
          "#nowarn \"44\""
          "open Api"
          "let shape = Api.Circle 1.0"
          "let p : Api.Point = { X = 1; Y = 2 }"
          "let b : Api.Box<string> = { Value = \"v\" }"
          "let g = (Api.Greeter \"hi \" :> Api.IGreeter).Greet \"you\""
          "let hello = (Api.Greeter \"hi \").Hello(\"you\")"
          "let a = Api.Answer"
          "let s = Api.size shape"
          "let even = match 4 with Api.Even _ -> 1 | _ -> 0"
          "let same = Api.equal 1 2"
          "let described = Api.describe 3"
          "let shout = \"x\".Shout"
          "" ]

// Two real checks, each under its own `RealCheckWaitMs`.
[<Fact(Timeout = 4 * RealCheckWaitMs)>]
let ``a later file's result standing across an upstream line shift analyses as a fresh one does`` () =
    // An edit that keeps every declaration moves them down the file, and a later file whose
    // check is not repeated keeps a typed tree naming the OLD lines. TestPrune reads those
    // positions (its generic-edge memo keys on them). Its analysis of the standing result
    // must select what its analysis of a fresh check selects, or the cut is unsound for it.
    withTempDir "api-signature-shift" (fun root ->
        let shifted =
            "module Api\n// one\n// two\n\n" + api.Substring("module Api\n".Length)

        let analyse (upSource: string) =
            let result, check = checkDownstream root EarlierFile upSource consumer

            let errors =
                check.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

            test <@ Array.isEmpty errors @>

            match
                TestPrune.AstAnalyzer.analyzeSourceFromResults "Down.fs" consumer result.ParseResults check "App"
            with
            | Ok analysis -> analysis
            | Error e -> failwith e

        let standing = analyse api
        let fresh = analyse shifted

        // The edit is one the cut applies to.
        test
            <@
                FsHotWatch.ApiSignature.ofCheck shifted (FullCheck(checkAlone root "Api.fs" shifted)) = FsHotWatch.ApiSignature.ofCheck
                    api
                    (FullCheck(checkAlone root "Api.fs" api))
            @>

        test <@ not standing.Dependencies.IsEmpty @>
        test <@ standing.Symbols = fresh.Symbols @>
        test <@ standing.Dependencies = fresh.Dependencies @>
        test <@ standing.TestMethods = fresh.TestMethods @>
        test <@ standing.Attributes = fresh.Attributes @>
        test <@ standing.ParentLinks = fresh.ParentLinks @>)
