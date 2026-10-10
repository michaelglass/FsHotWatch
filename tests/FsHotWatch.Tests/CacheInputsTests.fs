/// The cache-key inputs that live outside the judged file: the config a
/// tool discovers by walking up from it, and the shape of what its check resolved upstream.
module FsHotWatch.Tests.CacheInputsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FsHotWatch.Events
open FsHotWatch.CacheInputs
open FsHotWatch.Tests.TestHelpers

// =============================================================================
// Config discovered by walking up from the file
// =============================================================================

[<Fact>]
let ``editorConfigInputs collects every editorconfig from the repo root down to the file`` () =
    withTempDir "fantomas-editorconfig" (fun dir ->
        let sub = Path.Combine(dir, "src", "deep")
        Directory.CreateDirectory sub |> ignore
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(dir, "src", ".editorconfig"), "[*.fs]\nmax_line_length = 80\n")
        let file = Path.Combine(sub, "A.fs")
        File.WriteAllText(file, "module A\n")

        let inputs = editorConfigInputs dir [ file ]

        // the LABEL is repo-relative, so the same `.editorconfig` is
        // the same cache-key input in every checkout of the repository.
        test
            <@
                inputs = [ "editorconfig:repo:.editorconfig", "root = true\n"
                           "editorconfig:repo:src/.editorconfig", "[*.fs]\nmax_line_length = 80\n" ]
            @>

        // A file that does not exist yet still resolves its directories' configs.
        test <@ editorConfigInputs dir [ Path.Combine(dir, "src", "B.fs") ] |> List.length = 2 @>

        // No config files, no inputs.
        withTempDir "fantomas-noeditorconfig" (fun bare ->
            test <@ List.isEmpty (editorConfigInputs bare [ Path.Combine(bare, "A.fs") ]) @>)

        // A file OUTSIDE the root walks to the filesystem root and stops there, and
        // never reports this repository's configs.
        withTempDir "fantomas-outside" (fun elsewhere ->
            let outside = editorConfigInputs dir [ Path.Combine(elsewhere, "B.fs") ]
            test <@ outside |> List.forall (fun (label, _) -> not (label.Contains dir)) @>))

[<Fact>]
let ``configChainInputs finds any walked-up config file under its own label`` () =
    withTempDir "chain" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        File.WriteAllText(Path.Combine(dir, "fsharplint.json"), "{}")
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")

        let inputs =
            configChainInputs dir "fsharplint.json" "fsharplint" [ Path.Combine(dir, "src", "A.fs") ]

        test <@ inputs = [ "fsharplint:repo:fsharplint.json", "{}" ] @>)

[<Fact>]
let ``configChainInputs stops at the repository root`` () =
    // A config ABOVE the repository belongs to no checkout of it: keying it would split
    // two checkouts that sit under different parents.
    withTempDir "chain-parent" (fun parent ->
        let repo = Path.Combine(parent, "repo")
        Directory.CreateDirectory(Path.Combine(repo, "src")) |> ignore
        File.WriteAllText(Path.Combine(parent, ".editorconfig"), "root = true\n")
        File.WriteAllText(Path.Combine(repo, ".editorconfig"), "[*.fs]\n")

        let inputs = editorConfigInputs repo [ Path.Combine(repo, "src", "A.fs") ]

        test <@ inputs = [ "editorconfig:repo:.editorconfig", "[*.fs]\n" ] @>)

[<Fact>]
let ``configChainInputs reports a config shared by several files once`` () =
    withTempDir "chain-shared" (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src", "a")) |> ignore
        Directory.CreateDirectory(Path.Combine(dir, "src", "b")) |> ignore
        File.WriteAllText(Path.Combine(dir, "fsharplint.json"), "{}")
        File.WriteAllText(Path.Combine(dir, "src", "b", "fsharplint.json"), "[]")

        let inputs =
            configChainInputs
                dir
                "fsharplint.json"
                "fsharplint"
                [ Path.Combine(dir, "src", "a", "A.fs"); Path.Combine(dir, "src", "b", "B.fs") ]

        test
            <@
                inputs = [ "fsharplint:repo:fsharplint.json", "{}"
                           "fsharplint:repo:src/b/fsharplint.json", "[]" ]
            @>)

[<Fact>]
let ``configChainInputs with no files has no inputs`` () =
    withTempDir "chain-nofiles" (fun dir ->
        File.WriteAllText(Path.Combine(dir, ".editorconfig"), "root = true\n")
        test <@ List.isEmpty (editorConfigInputs dir []) @>)

[<Fact>]
let ``an unreadable config throws rather than being keyed as absent`` () =
    withTempDir "chain-unreadable" (fun dir ->
        let config = Path.Combine(dir, ".editorconfig")
        File.WriteAllText(config, "root = true\n")

        let canSimulate =
            not (OperatingSystem.IsWindows())
            && (File.SetUnixFileMode(config, UnixFileMode.None)

                try
                    File.ReadAllText config |> ignore
                    false // running as root: permissions are not enforced
                with _ ->
                    true)

        try
            if canSimulate then
                let raised =
                    try
                        editorConfigInputs dir [ Path.Combine(dir, "A.fs") ] |> ignore
                        false
                    with
                    | :? UnauthorizedAccessException
                    | :? IOException -> true

                test <@ raised @>
        finally
            if not (OperatingSystem.IsWindows()) then
                File.SetUnixFileMode(config, UnixFileMode.UserRead ||| UnixFileMode.UserWrite))

// =============================================================================
// The shape of what a file's check resolved outside it
// =============================================================================

[<Fact>]
let ``a file without typed results has a fixed used-signatures marker`` () =
    test <@ usedSignaturesHash None Unchecked.defaultof<_> ParseOnly = Some "parse-only" @>

    test
        <@ usedSignaturesHash None Unchecked.defaultof<_> (FullCheck Unchecked.defaultof<_>) = Some "full-check-null" @>

[<Fact>]
let ``a check whose symbol uses cannot be read has no used signatures`` () =
    // An FSharpCheckFileResults with no state throws from every query, as FCS can from a
    // symbol property on partially-checked code.
    let broken =
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof<FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults>
        )
        :?> FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults

    test <@ usedSignaturesHash None Unchecked.defaultof<_> (FullCheck broken) = None @>

/// An upstream file exercising every kind of declaration the shape renders.
let private upstream =
    String.concat
        "\n"
        [ "module Up"
          "open System"
          "type Shape = | Circle of radius: float | Square of side: float"
          "type Point = { X: int; mutable Y: int }"
          "type Box<'a> = { Value: 'a }"
          "type Id = int"
          "type Deep = { D: int }"
          "type Holder = { Inner: Deep }"
          "let holder : Holder = Unchecked.defaultof<_>"
          "type Colour = Red = 1 | Green = 2"
          "exception Boom of string"
          "[<Measure>] type m"
          "[<Measure>] type s"
          "type internal Hidden = { H: int }"
          "let internal hidden : Hidden = { H = 0 }"
          "type IGreeter = abstract Greet: string -> string"
          "type Greeter(prefix: string) ="
          "    member _.Prefix = prefix"
          "    member _.Hello(name: string) = prefix + name"
          "    interface IGreeter with"
          "        member _.Greet name = prefix + name"
          "[<Literal>]"
          "let Answer = 42"
          "let mutable counter = 0"
          "[<Obsolete(\"use area\")>]"
          "let size (s: Shape) = match s with Circle r -> r | Square d -> d"
          "let (|Even|_|) (n: int) = if n % 2 = 0 then Some n else None"
          "let combine (a: int) (b: string) : {| A: int; B: string |} * struct (int * string) = {| A = a; B = b |}, struct (a, b)"
          "let distance : float<m> = 1.0<m>"
          "let speed : float<m/s> = 1.0<m/s>"
          "" ]

/// Uses each declaration in `upstream` once.
let private downstream =
    String.concat
        "\n"
        [ "module Down"
          "#nowarn \"44\""
          "let shape = Up.Circle 1.0"
          "let side = match Up.Square 2.0 with Up.Square s -> s | _ -> 0.0"
          "let p : Up.Point = { X = 1; Y = 2 }"
          "let x = p.X"
          "let b : Up.Box<string> = { Value = \"v\" }"
          "let i : Up.Id = Unchecked.defaultof<_>"
          "let inner = Up.holder.Inner"
          "let c = Up.Colour.Green"
          "let isBoom (e: exn) = match e with Up.Boom _ -> true | _ -> false"
          "let g = (Up.Greeter \"hi \" :> Up.IGreeter).Greet \"you\""
          "let prefix = (Up.Greeter \"hi \").Prefix"
          "let a = Up.Answer"
          "let n = Up.counter"
          "let s = Up.size shape"
          "let even = match 4 with Up.Even _ -> 1 | _ -> 0"
          "let combined = Up.combine 1 \"one\""
          "let d = Up.distance"
          "let v = Up.speed"
          "let h = Up.hidden.H"
          "let hello = (Up.Greeter \"hi \").Hello(name = \"you\")"
          "let o : obj = null"
          "let key = match System.Collections.Generic.KeyValuePair(1, 2) with KeyValue(k, _) -> k"
          "" ]

let private shapesOf (root: string) (layout: UpstreamLayout) (up: string) =
    let result, check = checkDownstream root layout up downstream

    let errors =
        check.Diagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    test <@ Array.isEmpty errors @>
    usedShapes (Some root) result.ProjectOptions check

/// `upstream` with `before` replaced by `after`; the fragment must occur exactly once.
let private edit (before: string, after: string) =
    let at = upstream.IndexOf(before, StringComparison.Ordinal)
    test <@ at >= 0 && upstream.IndexOf(before, at + 1, StringComparison.Ordinal) < 0 @>
    upstream.Substring(0, at) + after + upstream.Substring(at + before.Length)

/// The shapes of `downstream` against `upstream` and against each edit of it.
let private shapesAcross (edits: (string * string) list) =
    withTempDir "shapes" (fun root ->
        let baseline = shapesOf root EarlierFile upstream
        baseline, [ for e in edits -> e, shapesOf root EarlierFile (edit e) ])

[<Fact(Timeout = 120000)>]
let ``every upstream shape change a downstream file can observe moves its used shapes`` () =
    let baseline, edited =
        shapesAcross
            [ "| Square of side: float", "| Square of side: float | Triangle of float"
              "mutable Y: int", "Y: int"
              "type Id = int", "type Id = string"
              "type Deep = { D: int }", "type Deep = | D of int"
              "Green = 2", "Green = 3"
              "exception Boom of string", "exception Boom of string * int"
              "member _.Prefix = prefix", "member _.Prefix = prefix.Length"
              "let Answer = 42", "let Answer = 43"
              "use area", "use perimeter"
              "let mutable counter = 0", "let counter = 0"
              "then Some n else", "then Some(string n) else"
              "{| A: int; B: string |} * struct (int * string) = {| A = a; B = b |}",
              "{| A: int; B: string; C: bool |} * struct (int * string) = {| A = a; B = b; C = true |}"
              "let distance : float<m> = 1.0<m>", "[<Measure>] type s\nlet distance : float<s> = 1.0<s>" ]

    let unchanged =
        edited |> List.filter (fun (_, shapes) -> shapes = baseline) |> List.map fst

    test <@ List.isEmpty unchanged @>

[<Fact(Timeout = 120000)>]
let ``upstream edits a downstream file cannot observe leave its used shapes alone`` () =
    let baseline, edited =
        shapesAcross
            [ "module Up\n", "module Up\n// every later declaration moves down a line\n"
              "Circle r -> r |", "Circle r -> r * 1.0 |"
              "member _.Greet name = prefix + name", "member _.Greet name = name + prefix"
              "let mutable counter = 0", "let mutable counter = 1"
              "= {| A = a; B = b |}", "= {| A = a + 0; B = b |}"
              "let distance", "let private unused = 0\nlet distance"
              "member _.Prefix = prefix", "member _.Prefix = prefix\n    member _.Unused = 0" ]

    let changed =
        edited |> List.filter (fun (_, shapes) -> shapes <> baseline) |> List.map fst

    test <@ List.isEmpty changed @>

[<Fact(Timeout = 120000)>]
let ``an assembly is described in full when it is a referenced project or under the root`` () =
    withTempDir "shapes-assemblies" (fun root ->
        let result, check = checkDownstream root EarlierFile upstream downstream
        let isExt (line: string) = line.StartsWith "ext FSharp.Core"
        let lines (text: string) = text.Split '\n'

        let external = usedShapes (Some root) result.ProjectOptions check
        test <@ lines external |> Array.exists isExt @>

        // A referenced project named like the assembly, of any kind, claims it. A
        // project reached twice (a diamond) is walked once.
        let lib =
            FSharpReferencedProject.FSharpReference(Path.Combine(root, "Lib.dll"), result.ProjectOptions)

        let claimed =
            { result.ProjectOptions with
                ReferencedProjects =
                    [| lib
                       lib
                       FSharpReferencedProject.ILModuleReference(
                           Path.Combine(root, "FSharp.Core.dll"),
                           (fun () -> DateTime.MinValue),
                           (fun () -> failwith "never read")
                       ) |] }

        test <@ not (lines (usedShapes (Some root) claimed check) |> Array.exists isExt) @>

        // An assembly under the root is the repository's: here every one is.
        let everything =
            usedShapes (Some(Path.GetPathRoot root)) result.ProjectOptions check

        test <@ not (lines everything |> Array.exists (fun l -> l.StartsWith "ext ")) @>
        test <@ everything.Contains "private" @>
        // Without a root only the project and its references are.
        test <@ usedShapes None result.ProjectOptions check = external @>)
