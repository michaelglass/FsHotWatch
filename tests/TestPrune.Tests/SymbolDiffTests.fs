module TestPrune.Tests.SymbolDiffTests

open Xunit
open Swensen.Unquote
open TestPrune.AstAnalyzer
open TestPrune.Domain
open TestPrune.SymbolDiff

let private mkSymbol name kind lineStart lineEnd =
    { FullName = name
      Kind = kind
      SourceFile = "src/Test.fs"
      LineStart = lineStart
      LineEnd = lineEnd
      ContentHash = $"%s{name}:%d{lineStart}-%d{lineEnd}"
      IsExtern = false }

let private mkSymbolWithHash name kind lineStart lineEnd hash =
    { FullName = name
      Kind = kind
      SourceFile = "src/Test.fs"
      LineStart = lineStart
      LineEnd = lineEnd
      ContentHash = hash
      IsExtern = false }

module ``No changes`` =

    [<Fact>]
    let ``identical symbols produce empty changes`` () =
        let symbols =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ]

        let changes, _events = detectChanges symbols symbols
        test <@ changes |> List.isEmpty @>

module ``Function body changed`` =

    [<Fact>]
    let ``line range differs produces Modified`` () =
        let current = [ mkSymbol "Mod.funcA" Function 1 8 ]
        let stored = [ mkSymbol "Mod.funcA" Function 1 5 ]

        let changes, _events = detectChanges current stored
        test <@ changes = [ Modified "Mod.funcA" ] @>

module ``New function added`` =

    [<Fact>]
    let ``symbol only in current produces Added`` () =
        let current =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ]

        let stored = [ mkSymbol "Mod.funcA" Function 1 5 ]

        let changes, _events = detectChanges current stored
        test <@ changes = [ Added "Mod.funcB" ] @>

module ``Function removed`` =

    [<Fact>]
    let ``symbol only in stored produces Removed`` () =
        let current = [ mkSymbol "Mod.funcA" Function 1 5 ]

        let stored =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ]

        let changes, _events = detectChanges current stored
        test <@ changes = [ Removed "Mod.funcB" ] @>

module ``DU case added to existing type`` =

    [<Fact>]
    let ``both DU type and new case show as changed`` () =
        let current =
            [ mkSymbol "Domain.MyDU" Type 1 5
              mkSymbol "Domain.MyDU.CaseA" DuCase 2 2
              mkSymbol "Domain.MyDU.CaseB" DuCase 3 3 ]

        let stored =
            [ mkSymbol "Domain.MyDU" Type 1 3; mkSymbol "Domain.MyDU.CaseA" DuCase 2 2 ]

        let changes, _events = detectChanges current stored
        test <@ changes |> List.length = 2 @>

        test <@ changes |> List.contains (Modified "Domain.MyDU") @>

        test <@ changes |> List.contains (Added "Domain.MyDU.CaseB") @>

module ``Multiple changes in one file`` =

    [<Fact>]
    let ``all changes detected`` () =
        let current =
            [ mkSymbol "Mod.funcA" Function 1 8 // modified
              mkSymbol "Mod.funcC" Function 10 15 ] // added

        let stored =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ] // removed

        let changes, _events = detectChanges current stored
        test <@ changes |> List.length = 3 @>

        test <@ changes |> List.contains (Modified "Mod.funcA") @>

        test <@ changes |> List.contains (Added "Mod.funcC") @>

        test <@ changes |> List.contains (Removed "Mod.funcB") @>

module ``Only whitespace changes`` =

    [<Fact>]
    let ``same lines produce empty changes`` () =
        let current =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ]

        let stored =
            [ mkSymbol "Mod.funcA" Function 1 5; mkSymbol "Mod.funcB" Function 7 12 ]

        let changes, _events = detectChanges current stored
        test <@ changes |> List.isEmpty @>

module ``Comment shift does not produce false Modified`` =

    [<Fact>]
    let ``same content hash with different line ranges is not Modified`` () =
        let current = [ mkSymbolWithHash "Mod.funcA" Function 5 10 "hash-a" ]
        let stored = [ mkSymbolWithHash "Mod.funcA" Function 1 6 "hash-a" ]

        let changes, _events = detectChanges current stored
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``different content hash is Modified even with same line ranges`` () =
        let current = [ mkSymbolWithHash "Mod.funcA" Function 1 5 "hash-new" ]
        let stored = [ mkSymbolWithHash "Mod.funcA" Function 1 5 "hash-old" ]

        let changes, _events = detectChanges current stored
        test <@ changes = [ Modified "Mod.funcA" ] @>

module ``Extern filtering`` =

    /// Regression: previously, callers had to pre-filter externs out of the `current`
    /// argument before calling `detectChanges`, or they'd see phantom diffs equal to the
    /// file's extern count (because stored never holds externs but current did). The
    /// invariant "externs are never compared" now lives inside detectChanges itself, so
    /// no caller can violate it by forgetting.
    [<Fact>]
    let ``externs in current are filtered out (no phantom diffs vs stored without externs)`` () =
        let externSym =
            { mkSymbol "Lib.externedFn" Function 1 3 with
                IsExtern = true }

        let regularSym = mkSymbol "Lib.regularFn" Function 5 10

        // current has externs+regular (as a fresh AnalysisResult would); stored only
        // has the regular symbol (because indexing previously dropped externs).
        let current = [ externSym; regularSym ]
        let stored = [ regularSym ]

        let changes, _events = detectChanges current stored
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``externs in stored are filtered out (no phantom Removed diffs)`` () =
        // Symmetric guard: if a stored DB ever held an extern (e.g. legacy data), it
        // shouldn't surface as a Removed change once the indexer stops persisting it.
        let externSym =
            { mkSymbol "Lib.legacyExtern" Function 1 3 with
                IsExtern = true }

        let regularSym = mkSymbol "Lib.regularFn" Function 5 10

        let current = [ regularSym ]
        let stored = [ externSym; regularSym ]

        let changes, _events = detectChanges current stored
        test <@ changes |> List.isEmpty @>

    [<Fact>]
    let ``non-extern diffs are still detected normally when externs are present`` () =
        // Sanity: filtering externs must not swallow real diffs in the same call.
        let externSym =
            { mkSymbol "Lib.externedFn" Function 1 3 with
                IsExtern = true }

        let storedFn = mkSymbolWithHash "Lib.regularFn" Function 5 10 "old"
        let currentFn = mkSymbolWithHash "Lib.regularFn" Function 5 10 "new"

        let current = [ externSym; currentFn ]
        let stored = [ storedFn ]

        let changes, _events = detectChanges current stored
        test <@ changes = [ Modified "Lib.regularFn" ] @>

module ``changedSymbolNames extracts names`` =

    [<Fact>]
    let ``extracts names from all change types`` () =
        let changes = [ Modified "Mod.funcA"; Added "Mod.funcB"; Removed "Mod.funcC" ]

        let names = changedSymbolNames changes
        test <@ names = [ "Mod.funcA"; "Mod.funcB"; "Mod.funcC" ] @>

module ``Event emission`` =

    [<Fact>]
    let ``modified symbol emits SymbolChangeDetectedEvent`` () =
        let stored =
            [ { FullName = "Lib.func"
                Kind = Function
                SourceFile = "src/Lib.fs"
                LineStart = 1
                LineEnd = 5
                ContentHash = "old"
                IsExtern = false } ]

        let current =
            [ { FullName = "Lib.func"
                Kind = Function
                SourceFile = "src/Lib.fs"
                LineStart = 1
                LineEnd = 5
                ContentHash = "new"
                IsExtern = false } ]

        let changes, events = detectChanges current stored
        test <@ changes.Length = 1 @>

        let changeEvents =
            events
            |> List.choose (fun e ->
                match e with
                | SymbolChangeDetectedEvent _ -> Some e
                | _ -> None)

        test <@ changeEvents.Length = 1 @>
