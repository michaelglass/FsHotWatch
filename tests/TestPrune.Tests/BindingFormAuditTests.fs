/// An audit of which F# binding forms actually reach the symbol graph.
///
/// A symbol is only tracked when the name FCS reports (`FullName`,
/// last segment) agrees with the name the AST collector records (`Ident.idText`).
/// Where an identifier has special syntax the two disagree, the lookup in
/// `allBindingRangeMap` misses, and the binding is dropped WITHOUT A DIAGNOSTIC.
/// That is under-selection: a change to the dropped binding selects no tests, and a
/// green run that never executed the relevant test is indistinguishable from one
/// that did. Over-selection only costs time; this ships bugs.
///
/// So every binding form gets a fixture here, and each fixture asserts the EXACT
/// qualified symbol name produced — not `EndsWith`, which would pass on a name that
/// lost its qualification and became a repo-wide hub. Forms that are deliberately
/// not tracked are asserted as exclusions with a stated reason AND with the
/// compensating coverage demonstrated, so an accidental drop can never masquerade
/// as an intentional one.
///
/// A name and an edge are not enough on their own: a symbol whose hash covers only the
/// line naming it is indexed, edged and still unselectable, because an edit to its body
/// changes no stored hash. So every fixture also carries an edit INSIDE the form, and the
/// audit asserts that the edit is reported as a change to that symbol, while an
/// edit outside the form is not.
module TestPrune.Tests.BindingFormAuditTests

open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open TestPrune.AstAnalyzer

/// Own checker instance: this file is its own xunit collection, so it runs in
/// parallel with other files but its cases are serialized against each other.
let private checker = FSharpChecker.Create()

let private analyze source =
    let fileName = "/tmp/BindingFormAuditTests.fsx"

    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    match
        analyzeSource checker fileName source options "TestProject"
        |> Async.RunSynchronously
    with
    | Ok r -> r
    | Error msg -> failwith $"Analysis failed: %s{msg}"

/// One F# binding form under audit.
type private Form =
    {
        /// Table key, mirrored by the theory's InlineData.
        Name: string
        /// The exact qualified name the symbol must be indexed under.
        Symbol: string
        Kind: SymbolKind
        /// The symbol the consumer edge must originate from, or None when the form
        /// admits no direct consumer (with `Note` saying why).
        Consumer: string option
        Note: string
        /// A relevant edit INSIDE the form, as (text, replacement). The text occurs
        /// exactly once in `Source`, and the edited source still type-checks.
        Edit: string * string
        Source: string
    }

/// Every fixture declares `consume`, so the consumer edge is always `M.consume`
/// unless the form makes a direct consumer impossible.
let private consumer = Some "M.consume"

let private formTable: Form list =
    [ { Name = "plain let"
        Symbol = "M.plain"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("x + 1", "x + 2")
        Source =
          """
module M
let plain x = x + 1
let consume () = plain 1
"""   }

      { Name = "private let"
        Symbol = "M.secret"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("x + 1", "x + 2")
        Source =
          """
module M
let private secret x = x + 1
let consume () = secret 1
"""   }

      { Name = "inline let"
        Symbol = "M.addInline"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("x + 1", "x + 2")
        Source =
          """
module M
let inline addInline x = x + 1
let consume () = addInline 1
"""   }

      { Name = "mutable let"
        Symbol = "M.counter"
        Kind = Value
        Consumer = consumer
        Note = ""
        Edit = ("counter = 0", "counter = 1")
        Source =
          """
module M
let mutable counter = 0
let consume () = counter <- counter + 1
"""   }

      { Name = "rec let"
        Symbol = "M.fact"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("fact (n - 1)", "fact (n - 2)")
        Source =
          """
module M
let rec fact n = if n <= 1 then 1 else n * fact (n - 1)
let consume () = fact 5
"""   }

      { Name = "nested module let"
        Symbol = "M.Inner.nested"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("nested x = x + 1", "nested x = x + 2")
        Source =
          """
module M
module Inner =
    let nested x = x + 1
let consume () = Inner.nested 1
"""   }

      { Name = "backticked name"
        Symbol = "M.``a name with spaces``"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("= x + 1", "= x + 2")
        Source =
          """
module M
let ``a name with spaces`` (x: int) = x + 1
let consume () = ``a name with spaces`` 1
"""   }

      { Name = "literal"
        Symbol = "M.Lit"
        Kind = Value
        Consumer = consumer
        Note = ""
        Edit = ("Lit = 42", "Lit = 43")
        Source =
          """
module M
[<Literal>]
let Lit = 42
let consume () = Lit + 1
"""   }

      // --- active patterns -------------------------------------------------
      // FCS names the group `M.(|Even|Odd|)` (WITH parens); the AST records
      // `idText` = `|Even|Odd|` (WITHOUT). Consumers reference the group through
      // an `FSharpActivePatternCase`, which classification must handle explicitly.
      { Name = "active pattern single-case"
        Symbol = "M.(|Wrapped|)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("= x + 1", "= x + 2")
        Source =
          """
module M
let (|Wrapped|) (x: int) = x + 1
let consume (n: int) =
    match n with
    | Wrapped v -> v
"""   }

      { Name = "active pattern multi-case"
        Symbol = "M.(|Even|Odd|)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("n % 2 = 0", "n % 2 = 1")
        Source =
          """
module M
let (|Even|Odd|) (n: int) = if n % 2 = 0 then Even else Odd
let consume (n: int) =
    match n with
    | Even -> "even"
    | Odd -> "odd"
"""   }

      { Name = "active pattern partial"
        Symbol = "M.(|Positive|_|)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("n > 0", "n > 1")
        Source =
          """
module M
let (|Positive|_|) (n: int) = if n > 0 then Some n else None
let consume (n: int) =
    match n with
    | Positive v -> v
    | _ -> 0
"""   }

      { Name = "active pattern parameterised"
        Symbol = "M.(|DivisibleBy|_|)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("n % d = 0", "n % d = 1")
        Source =
          """
module M
let (|DivisibleBy|_|) (d: int) (n: int) = if n % d = 0 then Some n else None
let consume (n: int) =
    match n with
    | DivisibleBy 3 v -> v
    | _ -> 0
"""   }

      // --- operators -------------------------------------------------------
      // FCS names these in DISPLAY form (`M.(+.)`); the AST records the COMPILED
      // form (`op_PlusDot`). `M.(+.)` also breaks the naive `LastIndexOf('.')`
      // split, which returns `)`.
      { Name = "operator infix"
        Symbol = "M.(+.)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("a + b + 1", "a + b + 2")
        Source =
          """
module M
let (+.) (a: int) (b: int) = a + b + 1
let consume () = 1 +. 2
"""   }

      { Name = "operator prefix"
        Symbol = "M.(~-.)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("= -a", "= -a - 1")
        Source =
          """
module M
let (~-.) (a: int) = -a
let consume () = -. 3
"""   }

      { Name = "operator symbolic"
        Symbol = "M.(=~=)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("a.ToLowerInvariant()", "a.ToUpperInvariant()")
        Source =
          """
module M
let (=~=) (a: string) (b: string) = a.ToLowerInvariant() = b.ToLowerInvariant()
let consume () = "a" =~= "A"
"""   }

      // An operator whose name STARTS AND ENDS with `|` has the same outer shape
      // as an active pattern. It must still be compiled as an operator, not
      // passed through verbatim.
      { Name = "operator bar-delimited"
        Symbol = "M.(|||)"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("= a + b", "= a + b + 1")
        Source =
          """
module M
let (|||) (a: int) (b: int) = a + b
let consume () = 1 ||| 2
"""   }

      // --- type members ----------------------------------------------------
      { Name = "type member instance"
        Symbol = "M.T.Instance"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("Instance(x: int) = x + 1", "Instance(x: int) = x + 2")
        Source =
          """
module M
type T() =
    member _.Instance(x: int) = x + 1
let consume () = T().Instance 1
"""   }

      { Name = "type member static"
        Symbol = "M.T.Stat"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("Stat(x: int) = x + 1", "Stat(x: int) = x + 2")
        Source =
          """
module M
type T() =
    static member Stat(x: int) = x + 1
let consume () = T.Stat 1
"""   }

      { Name = "extension member"
        Symbol = "M.Shout"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("this.ToUpperInvariant()", "this.ToLowerInvariant()")
        Source =
          """
module M
type System.String with
    member this.Shout() = this.ToUpperInvariant()
let consume () = "a".Shout()
"""   }

      { Name = "abstract member with default"
        Symbol = "M.Base.Describe"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("\"base\"", "\"BASE\"")
        Source =
          """
module M
[<AbstractClass>]
type Base() =
    abstract member Describe: unit -> string
    default _.Describe() = "base"
let consume (b: Base) = b.Describe()
"""   }

      // No `default`, so the ONLY syntax carrying the name is the abstract slot —
      // `collectTypeMemberRanges` has to walk it.
      { Name = "abstract member without default"
        Symbol = "M.Shape.Area"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("Area: unit -> float", "Area: unit -> double")
        Source =
          """
module M
[<AbstractClass>]
type Shape() =
    abstract member Area: unit -> float
type Sq() =
    inherit Shape()
    override _.Area() = 1.0
let consume (s: Shape) = s.Area()
"""   }

      { Name = "override member"
        Symbol = "M.Derived.Describe"
        Kind = Function
        Consumer = None
        Note =
          "A virtual call is reported by FCS against the slot's declaring type, so \
               `M.consume` depends on `M.Base.Describe`, not on the override. Only the \
               override's own tracking is asserted here."
        Edit = ("\"derived\"", "\"DERIVED\"")
        Source =
          """
module M
[<AbstractClass>]
type Base() =
    abstract member Describe: unit -> string
type Derived() =
    inherit Base()
    override _.Describe() = "derived"
let consume () = (Derived() :> Base).Describe()
"""   }

      { Name = "interface member declaration"
        Symbol = "M.IThing.Do"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("Do: int -> int", "Do: int -> System.Int32")
        Source =
          """
module M
type IThing =
    abstract member Do: int -> int
type Thing() =
    interface IThing with
        member _.Do x = x + 1
let consume () = (Thing() :> IThing).Do 1
"""   }

      // Nothing in this file implements `IThing`, so the abstract slot is the ONLY
      // syntax carrying the name `Do` — the fixture above masks that, because its
      // `interface IThing with member _.Do` supplies the same name from elsewhere.
      { Name = "interface member declared alone"
        Symbol = "M.IThing.Do"
        Kind = Function
        Consumer = consumer
        Note = ""
        Edit = ("Do: int -> int", "Do: int -> System.Int32")
        Source =
          """
module M
type IThing =
    abstract member Do: int -> int
let consume (t: IThing) = t.Do 1
"""   }

      { Name = "interface implementation"
        Symbol = "M.Thing.Do"
        Kind = Function
        Consumer = None
        Note =
          "F# explicit interface implementations are only reachable through the \
               interface, so no consumer can name `M.Thing.Do` directly. Only the \
               implementation's own tracking is asserted here."
        Edit = ("_.Do x = x + 1", "_.Do x = x + 2")
        Source =
          """
module M
type IThing =
    abstract member Do: int -> int
type Thing() =
    interface IThing with
        member _.Do x = x + 1
let consume () = (Thing() :> IThing).Do 1
"""   }

      { Name = "property getter/setter"
        Symbol = "M.T.Prop"
        Kind = Property
        Consumer = consumer
        Note = ""
        Edit = ("with get () = v", "with get () = v + 1")
        Source =
          """
module M
type T() =
    let mutable v = 0
    member _.Prop
        with get () = v
        and set (value: int) = v <- value
let consume () =
    let t = T()
    t.Prop <- 1
    t.Prop
"""   }

      { Name = "auto property"
        Symbol = "M.T.Auto"
        Kind = Property
        Consumer = consumer
        Note = ""
        Edit = ("Auto = 0", "Auto = 1")
        Source =
          """
module M
type T() =
    member val Auto = 0 with get, set
let consume () =
    let t = T()
    t.Auto <- 1
    t.Auto
"""   }

      // The two forms above edit the line that names the property, which a hash of
      // that line alone would catch. These edit a body line BELOW it: `with get`/`and
      // set` parses as `SynMemberDefn.GetSetMember` and `member val` as
      // `SynMemberDefn.AutoProperty`, neither of which is a `SynMemberDefn.Member`.
      { Name = "property getter multiline body"
        Symbol = "M.T.Prop"
        Kind = Property
        Consumer = consumer
        Note = ""
        Edit = ("id v", "id (v + 1)")
        Source =
          """
module M
type T() =
    let mutable v = 0
    member _.Prop
        with get () =
            id v
        and set (value: int) = v <- value
let consume () =
    let t = T()
    t.Prop <- 1
    t.Prop
"""   }

      { Name = "auto property multiline initializer"
        Symbol = "M.T.Auto"
        Kind = Property
        Consumer = consumer
        Note = ""
        Edit = ("40 + 2", "40 + 3")
        Source =
          """
module M
type T() =
    member val Auto =
        40 + 2 with get, set
let consume () =
    let t = T()
    t.Auto <- 1
    t.Auto
"""   }

      { Name = "DU case"
        Symbol = "M.D.B"
        Kind = DuCase
        Consumer = consumer
        Note = ""
        Edit = ("B of int", "B of value: int")
        Source =
          """
module M
type D =
    | A
    | B of int
let consume () = B 1
"""   }

      // --- computation expressions -----------------------------------------
      // Tracked, but with NO consumer edge: see
      // `computation-expression builder members have no consumer edge` below,
      // which pins both the absence and the compensating coverage.
      { Name = "computation-expression Bind"
        Symbol = "M.MyBuilder.Bind"
        Kind = Function
        Consumer = None
        Note =
          "FCS reports no symbol use at `let!`, so no consumer edge exists. \
               Compensating coverage is asserted separately."
        Edit = ("Option.bind f x", "Option.bind f (Option.map id x)")
        Source =
          """
module M
type MyBuilder() =
    member _.Bind(x: int option, f: int -> int option) = Option.bind f x
    member _.Return(x: int) = Some x
let my = MyBuilder()
let consume () =
    my {
        let! a = Some 1
        return a + 1
    }
"""   }

      { Name = "computation-expression Return"
        Symbol = "M.MyBuilder.Return"
        Kind = Function
        Consumer = None
        Note =
          "FCS reports no symbol use at `return`, so no consumer edge exists. \
               Compensating coverage is asserted separately."
        Edit = ("Return(x: int) = Some x", "Return(x: int) = Some(x + 0)")
        Source =
          """
module M
type MyBuilder() =
    member _.Bind(x: int option, f: int -> int option) = Option.bind f x
    member _.Return(x: int) = Some x
let my = MyBuilder()
let consume () =
    my {
        let! a = Some 1
        return a + 1
    }
"""   } ]

/// Asserts that `result` indexes `form` under its exact qualified name, as a REAL indexed
/// definition, with its consumer edge where the form admits one. `label` names the
/// source variant in failure messages.
let private assertIndexed (label: string) (form: Form) (result: AnalysisResult) =
    let matching = result.Symbols |> List.filter (fun s -> s.FullName = form.Symbol)

    let allNames =
        result.Symbols
        |> List.map (fun s -> $"%A{s.Kind} %s{s.FullName}")
        |> String.concat "\n  "

    Assert.True(
        not matching.IsEmpty,
        $"'%s{label}': expected a symbol named '%s{form.Symbol}'. Indexed instead:\n  %s{allNames}"
    )

    // A name mismatch does not erase the name — it demotes it. The extern pass
    // re-synthesizes any edge target it cannot find locally as an `ExternRef` with
    // `SourceFile = \"_extern\"` and an EMPTY content hash, so the symbol looks
    // present while being permanently unchangeable: no edit to it ever selects a
    // test. Asserting the kind and the source file is what separates the two.
    let real =
        matching
        |> List.filter (fun s -> s.Kind = form.Kind && s.SourceFile <> ExternSourceFile)

    Assert.True(
        not real.IsEmpty,
        $"'%s{label}': '%s{form.Symbol}' is present but not as an indexed %A{form.Kind} definition — %A{matching |> List.map (fun s -> s.Kind, s.SourceFile, s.ContentHash)}"
    )

    // An unqualified `full_name` is a repo-wide hub: `symbols.full_name` is UNIQUE and
    // every same-named thing UPSERTs onto the one row.
    Assert.True(
        form.Symbol.Contains '.',
        $"'%s{label}': '%s{form.Symbol}' is unqualified and would merge with every other symbol of that name"
    )

    match form.Consumer with
    | None -> ()
    | Some from ->
        let edges =
            result.Dependencies
            |> List.filter (fun d -> d.FromSymbol = from && d.ToSymbol = form.Symbol)

        let allEdges =
            result.Dependencies
            |> List.map (fun d -> $"%s{d.FromSymbol} -> %s{d.ToSymbol}")
            |> String.concat "\n  "

        Assert.True(
            not edges.IsEmpty,
            $"'%s{label}': expected a dependency edge '%s{from}' -> '%s{form.Symbol}'. Edges present:\n  %s{allEdges}"
        )

/// `form.Source` with `form.Edit` applied. The edited text must occur exactly once, so
/// the edit cannot silently land outside the form or not at all.
let private editedSource (form: Form) =
    let text, replacement = form.Edit
    let first = form.Source.IndexOf(text, System.StringComparison.Ordinal)

    Assert.True(
        first >= 0
        && form.Source.IndexOf(text, first + 1, System.StringComparison.Ordinal) < 0,
        $"'%s{form.Name}': the edit text '%s{text}' must occur exactly once in the fixture"
    )

    form.Source.Replace(text, replacement)

/// The changes `SymbolDiff.detectChanges` reports for `current` against `stored`: the
/// exact signal `ImpactAnalysis.selectTests` seeds the affected-test walk with.
/// Asserting on it, not on raw hash bytes, keeps the audit honest about duplicate
/// names — both stores keep ONE occurrence per name and file, and so does this.
let private changesBetween (stored: AnalysisResult) (current: AnalysisResult) =
    TestPrune.SymbolDiff.detectChanges current.Symbols stored.Symbols |> fst

/// The audit proper. Each case asserts the exact qualified name, that the symbol is
/// a REAL indexed definition (not the hash-less `ExternRef` placeholder the extern
/// pass synthesizes for unresolved edge targets — the shape a name mismatch takes),
/// and the consumer edge where the form admits one.
///
/// It then edits the form's body and asserts that the edit is reported as a change to
/// the symbol (and that name and edge survive the edit), and that an edit outside the
/// form is not. A form passing only the first half is indexed but unselectable.
[<Theory>]
[<InlineData "plain let">]
[<InlineData "private let">]
[<InlineData "inline let">]
[<InlineData "mutable let">]
[<InlineData "rec let">]
[<InlineData "nested module let">]
[<InlineData "backticked name">]
[<InlineData "literal">]
[<InlineData "active pattern single-case">]
[<InlineData "active pattern multi-case">]
[<InlineData "active pattern partial">]
[<InlineData "active pattern parameterised">]
[<InlineData "operator infix">]
[<InlineData "operator prefix">]
[<InlineData "operator symbolic">]
[<InlineData "operator bar-delimited">]
[<InlineData "type member instance">]
[<InlineData "type member static">]
[<InlineData "extension member">]
[<InlineData "abstract member with default">]
[<InlineData "abstract member without default">]
[<InlineData "override member">]
[<InlineData "interface member declaration">]
[<InlineData "interface member declared alone">]
[<InlineData "interface implementation">]
[<InlineData "property getter/setter">]
[<InlineData "auto property">]
[<InlineData "property getter multiline body">]
[<InlineData "auto property multiline initializer">]
[<InlineData "DU case">]
[<InlineData "computation-expression Bind">]
[<InlineData "computation-expression Return">]
let ``binding form is indexed under its exact qualified name`` (formName: string) =
    let form =
        formTable
        |> List.tryFind (fun f -> f.Name = formName)
        |> Option.defaultWith (fun () -> failwith $"no fixture named '%s{formName}'")

    let result = analyze form.Source
    assertIndexed formName form result

    // Inside the form: the edit must reach the stored hash of exactly this name, or a
    // change to the form selects no test.
    let edited = analyze (editedSource form)
    assertIndexed $"%s{formName} (edited)" form edited

    let insideChanges = changesBetween result edited

    Assert.True(
        insideChanges |> List.contains (TestPrune.SymbolDiff.Modified form.Symbol),
        $"'%s{formName}': editing %A{form.Edit} inside the form did not change the stored hash of '%s{form.Symbol}'. Changes reported: %A{insideChanges}"
    )

    // Outside the form: a hash covering more than the form (the whole file, say) would
    // pass the half above while selecting every test on every edit.
    let outside = analyze (form.Source + "\nlet unrelatedTail () = 0\n")
    let outsideChanges = changesBetween result outside

    Assert.False(
        outsideChanges |> List.contains (TestPrune.SymbolDiff.Modified form.Symbol),
        $"'%s{formName}': an edit outside the form changed the stored hash of '%s{form.Symbol}'. Changes reported: %A{outsideChanges}"
    )

/// Guards the table against the InlineData list drifting away from it: a form added
/// to `forms` but not to the theory would silently never be audited.
[<Fact>]
let ``every form in the table has an audit case`` () =
    // The theory's InlineData is the source of truth for what actually runs;
    // mirror it here so a missing entry fails loudly rather than silently.
    let audited =
        set
            [ "plain let"
              "private let"
              "inline let"
              "mutable let"
              "rec let"
              "nested module let"
              "backticked name"
              "literal"
              "active pattern single-case"
              "active pattern multi-case"
              "active pattern partial"
              "active pattern parameterised"
              "operator infix"
              "operator prefix"
              "operator symbolic"
              "operator bar-delimited"
              "type member instance"
              "type member static"
              "extension member"
              "abstract member with default"
              "abstract member without default"
              "override member"
              "interface member declaration"
              "interface member declared alone"
              "interface implementation"
              "property getter/setter"
              "auto property"
              "property getter multiline body"
              "auto property multiline initializer"
              "DU case"
              "computation-expression Bind"
              "computation-expression Return" ]

    let declared = formTable |> List.map (fun f -> f.Name) |> Set.ofList
    test <@ Set.difference declared audited = Set.empty @>
    test <@ Set.difference audited declared = Set.empty @>

// ---------------------------------------------------------------------------
// Documented exclusions.
//
// Each of these asserts BOTH halves: that the form is not tracked in its own
// right, AND that the coverage it would have provided arrives another way. The
// second half is what makes the exclusion safe rather than merely asserted — it
// fails if the compensating path ever stops working.
// ---------------------------------------------------------------------------

let private hashOf (result: AnalysisResult) (name: string) =
    result.Symbols
    |> List.tryFind (fun s -> s.FullName = name)
    |> Option.map (fun s -> s.ContentHash)

[<Fact>]
let ``record fields are excluded: the declaring type carries their content`` () =
    let source (fieldType: string) =
        $"""
module M
type R = {{ Field: %s{fieldType} }}
let consume () = {{ Field = Unchecked.defaultof<%s{fieldType}> }}
"""

    let before = analyze (source "int")
    let after = analyze (source "string")

    // Excluded: no per-field symbol. A field is an `FSharpField`, which `classifySymbol`
    // maps to no symbol kind; a use of one becomes an edge to its record type instead.
    test <@ before.Symbols |> List.forall (fun s -> s.FullName <> "M.R.Field") @>

    // Reason it is safe: the record type's hash range spans the whole `SynTypeDefn`,
    // so a field edit changes `M.R`, and consumers depend on `M.R`.
    test <@ (hashOf before "M.R").IsSome @>
    test <@ hashOf before "M.R" <> hashOf after "M.R" @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.consume" && d.ToSymbol = "M.R")
        @>

[<Fact>]
let ``constructors are excluded: the declaring type carries their content`` () =
    let source (body: string) =
        $"""
module M
type T(x: int) =
    let scaled = %s{body}
    member _.X = scaled
let consume () = T(1)
"""

    let before = analyze (source "x * 2")
    let after = analyze (source "x * 3")

    // Excluded: FCS names a constructor `M.T.``.ctor``` while the AST records `new`
    // (explicit) or nothing at all (implicit), so it is never tracked in its own right.
    test
        <@
            before.Symbols
            |> List.forall (fun s -> not (s.FullName.EndsWith(".ctor``", System.StringComparison.Ordinal)))
        @>

    // Reason it is safe: `T(1)` is reported by FCS against the TYPE, whose hash range
    // spans the constructor, so the consumer edge and the invalidation both land there.
    test <@ hashOf before "M.T" <> hashOf after "M.T" @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.consume" && d.ToSymbol = "M.T")
        @>

[<Fact>]
let ``computation-expression builder members have no consumer edge`` () =
    let source (bindBody: string) =
        $"""
module M
type MyBuilder() =
    member _.Bind(x: int option, f: int -> int option) = %s{bindBody}
    member _.Return(x: int) = Some x
let my = MyBuilder()
let consume () =
    my {{
        let! a = Some 1
        return a + 1
    }}
"""

    let before = analyze (source "Option.bind f x")
    let after = analyze (source "Option.bind f (Option.map id x)")

    // Excluded: FCS emits no symbol use at `let!`/`return`, so there is nothing to
    // build an edge from. This is an FCS limitation, not a naming mismatch — the
    // members themselves ARE tracked (audited above).
    test
        <@
            before.Dependencies
            |> List.forall (fun d ->
                d.FromSymbol <> "M.consume"
                || not (d.ToSymbol.StartsWith("M.MyBuilder.", System.StringComparison.Ordinal)))
        @>

    // Reason it is safe: the edited member's own hash changes, the member is linked to
    // the builder TYPE, the walk lifts a changed member to its type, and the consumer
    // reaches the type through the builder value.
    test <@ hashOf before "M.MyBuilder.Bind" <> hashOf after "M.MyBuilder.Bind" @>

    test
        <@
            after.ParentLinks
            |> List.contains
                { Child = "M.MyBuilder.Bind"
                  Parent = "M.MyBuilder" }
        @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.consume" && d.ToSymbol = "M.my")
        @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.my" && d.ToSymbol = "M.MyBuilder")
        @>

[<Fact>]
let ``class let bindings are excluded: the declaring type carries their content`` () =
    let source (staticValue: string) (helperBody: string) =
        $"""
module M
type T() =
    static let offset = %s{staticValue}
    let helper y = %s{helperBody}
    member _.Use(x: int) = helper x + offset
let consume () = T().Use 1
"""

    let before = analyze (source "1" "y + 1")
    let instanceEdited = analyze (source "1" "y + 2")
    let staticEdited = analyze (source "2" "y + 1")

    // Excluded: a class `let` (instance or static) is not a member — FCS reports it as
    // neither a module value nor a member, and `collectTypeMemberRanges` records no
    // name for `SynMemberDefn.LetBindings` — so it is never a symbol in its own right.
    // A member's call to a class `let` function still leaves an edge to a hash-less
    // extern placeholder (`M.T.helper`, `IsExtern = true`); no edit can change that
    // placeholder, so it is not a definition and selects nothing on its own.
    test
        <@
            before.Symbols
            |> List.filter (fun s -> not s.IsExtern)
            |> List.forall (fun s ->
                not (s.FullName.EndsWith(".helper", System.StringComparison.Ordinal))
                && not (s.FullName.EndsWith(".offset", System.StringComparison.Ordinal)))
        @>

    test
        <@
            before.Symbols
            |> List.filter (fun s -> s.FullName.EndsWith(".helper", System.StringComparison.Ordinal))
            |> List.forall (fun s -> s.IsExtern && s.ContentHash = "")
        @>

    // Reason it is safe: no member's hash covers a class `let`, so it stays in the type's
    // header. Editing either binding changes `M.T`; a changed type selects its consumers
    // and, through its members, theirs.
    test <@ hashOf before "M.T" <> hashOf instanceEdited "M.T" @>
    test <@ hashOf before "M.T" <> hashOf staticEdited "M.T" @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.consume" && d.ToSymbol = "M.T")
        @>

[<Fact>]
let ``val fields are excluded: the declaring type carries their content`` () =
    let source (fieldType: string) =
        $"""
module M
type T() =
    [<DefaultValue>]
    val mutable Count: %s{fieldType}
    member this.Read() = this.Count
let consume () = T().Read()
"""

    let before = analyze (source "int")
    let after = analyze (source "int64")

    // Excluded: a `val` field is an `FSharpField`, which `classifySymbol` does not map to
    // a symbol kind — the same class as a record field, excluded above.
    test <@ before.Symbols |> List.forall (fun s -> s.FullName <> "M.T.Count") @>

    // Reason it is safe: a field is the type's layout, so it stays in the type's header.
    test <@ hashOf before "M.T" <> hashOf after "M.T" @>

    test
        <@
            before.Dependencies
            |> List.exists (fun d -> d.FromSymbol = "M.consume" && d.ToSymbol = "M.T")
        @>

[<Fact>]
let ``nested types are excluded: F# rejects them, so no indexed file contains one`` () =
    // `SynMemberDefn.NestedType` exists in the syntax tree, but F# does not compile a
    // type nested in a type. A file holding one fails to build, so the analyzer never
    // indexes a working program containing it, and there is nothing to track.
    let fileName = "/tmp/BindingFormAuditNestedType.fsx"

    let source =
        """
module M
type Outer() =
    type Inner = int
    member _.X = 1
"""

    let options = getScriptOptions checker fileName source |> Async.RunSynchronously

    let parseResults, checkAnswer =
        checker.ParseAndCheckFileInProject(fileName, 0, FSharp.Compiler.Text.SourceText.ofString source, options)
        |> Async.RunSynchronously

    let checkDiagnostics =
        match checkAnswer with
        | FSharpCheckFileAnswer.Succeeded checkResults -> checkResults.Diagnostics
        | FSharpCheckFileAnswer.Aborted -> [||]

    let errors =
        Array.append parseResults.Diagnostics checkDiagnostics
        |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)

    test <@ errors.Length > 0 @>

// ---------------------------------------------------------------------------
// From a changed hash to a selected test.
//
// The audit above proves an edit inside a property changes the stored hash. These
// prove the change is ACTIONABLE: it reaches the affected-test walk and selects the
// test that reads the property. The control pins the property's hash back to its old
// value and requires the selection to go away — so the selection is caused by the
// hash, not by some other change the edit happened to make (the type's header, say).
// ---------------------------------------------------------------------------

/// A test file whose only test reads one property, with that property's body as given.
let private propertyTestSource (form: string) (body: string) =
    let property =
        match form with
        | "getter" ->
            $"""
type T() =
    let mutable v = 0
    member _.Prop
        with get () =
            %s{body}
        and set (value: int) = v <- value
"""
        | "auto" ->
            $"""
type T() =
    member val Prop =
        %s{body} with get, set
"""
        | other -> failwith $"no property form '%s{other}'"

    $"""
module M
type FactAttribute() =
    inherit System.Attribute()
%s{property}
[<Fact>]
let readsProp () = T().Prop |> ignore
"""

[<Theory>]
[<InlineData("getter", "id v", "id (v + 1)")>]
[<InlineData("auto", "40 + 2", "40 + 3")>]
let ``a property body edit selects the property's tests`` (form: string) (body: string) (editedBody: string) =
    let stored = analyze (propertyTestSource form body)
    let current = analyze (propertyTestSource form editedBody)
    let store = TestPrune.InMemoryStore.fromAnalysisResults [ stored ]

    let selectedFor (symbols: SymbolInfo list) =
        let changed =
            TestPrune.SymbolDiff.detectChanges symbols stored.Symbols
            |> fst
            |> TestPrune.SymbolDiff.changedSymbolNames

        changed, (store.QueryAffectedTests changed |> List.map _.SymbolFullName)

    test <@ stored.TestMethods |> List.exists (fun t -> t.SymbolFullName = "M.readsProp") @>

    let changed, selected = selectedFor current.Symbols
    test <@ changed |> List.contains "M.T.Prop" @>
    test <@ selected |> List.contains "M.readsProp" @>

    // Detector-positive control: the same edit with the property's hash held at its old
    // value selects nothing. If it still selected the test, the assertion above would
    // pass whether or not the property's hash moved.
    let storedHash =
        stored.Symbols
        |> List.filter (fun s -> s.FullName = "M.T.Prop")
        |> List.map _.ContentHash
        |> List.last

    let pinned =
        current.Symbols
        |> List.map (fun s ->
            if s.FullName = "M.T.Prop" then
                { s with ContentHash = storedHash }
            else
                s)

    let pinnedChanged, pinnedSelected = selectedFor pinned
    test <@ not (pinnedChanged |> List.contains "M.T.Prop") @>
    test <@ pinnedSelected |> List.isEmpty @>
