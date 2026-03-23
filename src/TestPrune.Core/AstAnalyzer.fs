module TestPrune.AstAnalyzer

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

type SymbolKind =
    | Function
    | Type
    | DuCase
    | Module
    | Value
    | Property

type SymbolInfo =
    { FullName: string
      Kind: SymbolKind
      SourceFile: string
      LineStart: int
      LineEnd: int }

type DependencyKind =
    | Calls
    | UsesType
    | PatternMatches
    | References

type Dependency =
    { FromSymbol: string
      ToSymbol: string
      Kind: DependencyKind }

type RouteHandlerEntry =
    { UrlPattern: string
      HttpMethod: string
      HandlerSourceFile: string }

type TestMethodInfo =
    { SymbolFullName: string
      TestProject: string
      TestClass: string
      TestMethod: string }

/// Normalize symbol source paths from absolute to repo-relative.
let normalizeSymbolPaths (repoRoot: string) (symbols: SymbolInfo list) =
    symbols
    |> List.map (fun s ->
        { s with
            SourceFile = System.IO.Path.GetRelativePath(repoRoot, s.SourceFile) })

type AnalysisResult =
    { Symbols: SymbolInfo list
      Dependencies: Dependency list
      TestMethods: TestMethodInfo list }

let private classifySymbol (symbol: FSharpSymbol) : (SymbolKind * string) option =
    match symbol with
    | :? FSharpEntity as entity ->
        try
            let fullName = entity.FullName

            if entity.IsFSharpModule then
                Some(Module, fullName)
            elif entity.IsFSharpUnion then
                Some(Type, fullName)
            elif entity.IsFSharpRecord then
                Some(Type, fullName)
            elif entity.IsEnum then
                Some(Type, fullName)
            elif entity.IsFSharpAbbreviation then
                Some(Type, fullName)
            elif entity.IsClass || entity.IsValueType || entity.IsInterface then
                Some(Type, fullName)
            else
                Some(Type, fullName)
        with :? InvalidOperationException ->
            None
    | :? FSharpMemberOrFunctionOrValue as mfv ->
        try
            let fullName = mfv.FullName

            if mfv.IsProperty || mfv.IsPropertyGetterMethod || mfv.IsPropertySetterMethod then
                Some(Property, fullName)
            elif mfv.IsUnionCaseTester then
                // These are the auto-generated Is* properties on DU cases — skip them
                None
            elif
                mfv.FullType.IsFunctionType
                || mfv.IsFunction
                || mfv.CurriedParameterGroups.Count > 0
            then
                Some(Function, fullName)
            else
                Some(Value, fullName)
        with :? InvalidOperationException ->
            None
    | :? FSharpUnionCase as uc ->
        try
            Some(DuCase, uc.FullName)
        with :? InvalidOperationException ->
            None
    | _ -> None

let private classifyDependency (symbol: FSharpSymbol) : DependencyKind =
    match symbol with
    | :? FSharpEntity -> UsesType
    | :? FSharpUnionCase -> PatternMatches
    | :? FSharpMemberOrFunctionOrValue -> Calls
    | _ -> References

let private knownTestAttributes =
    set
        [
          // xUnit
          "FactAttribute"
          "TheoryAttribute"
          "Fact"
          "Theory"
          // NUnit
          "TestAttribute"
          "TestCaseAttribute"
          "TestCaseSourceAttribute"
          "Test"
          "TestCase"
          "TestCaseSource"
          // MSTest
          "TestMethodAttribute"
          "DataTestMethodAttribute"
          "TestMethod"
          "DataTestMethod" ]

let private isTestAttribute (mfv: FSharpMemberOrFunctionOrValue) : bool =
    try
        mfv.Attributes
        |> Seq.exists (fun attr ->
            let name = attr.AttributeType.DisplayName
            knownTestAttributes |> Set.contains name)
    with :? InvalidOperationException ->
        false

let private extractTestClass (fullName: string) : string * string =
    match fullName.LastIndexOf('.') with
    | -1 -> ("", fullName)
    | idx -> (fullName.Substring(0, idx), fullName.Substring(idx + 1))

/// Extract the binding name from a SynPat head pattern.
let private extractBindingName (pat: SynPat) : string option =
    match pat with
    | SynPat.LongIdent(longDotId = synLongIdent) ->
        let ids = synLongIdent.LongIdent
        Some(ids |> List.map (fun id -> id.idText) |> String.concat ".")
    | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
    | _ -> None // INT-WILDCARD-001:ok — SynPat has many cases; we only extract names from LongIdent/Named

/// Walk the parsed AST to collect module-level binding names and their full ranges.
/// Each binding's range covers from the `let` keyword to the end of its body,
/// which correctly encompasses nested local bindings.
let collectModuleBindingRanges (tree: ParsedInput) : (string * range) list =
    let results = ResizeArray()

    let walkBinding (binding: SynBinding) =
        match binding with
        | SynBinding(headPat = headPat; expr = bodyExpr; range = bindingRange) ->
            match extractBindingName headPat with
            | Some n ->
                // The binding's `range` only covers the pattern (e.g. `let main args =`).
                // We need a range that spans from the binding start to the end of the body
                // so that nested local `let` bindings are contained within their parent.
                let fullRange =
                    Range.mkRange bindingRange.FileName bindingRange.Start bodyExpr.Range.End

                results.Add(n, fullRange)
            | None -> ()

    let rec walkDecl (decl: SynModuleDecl) =
        match decl with
        | SynModuleDecl.Let(bindings = bindings) ->
            for binding in bindings do
                walkBinding binding
        | SynModuleDecl.NestedModule(decls = decls) ->
            for d in decls do
                walkDecl d
        | _ -> () // INT-WILDCARD-001:ok — SynModuleDecl has many cases; we only walk Let and NestedModule

    match tree with
    | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
        for moduleOrNs in modules do
            match moduleOrNs with
            | SynModuleOrNamespace(decls = decls) ->
                for d in decls do
                    walkDecl d
    | ParsedInput.SigFile _ -> ()

    results |> Seq.toList

/// Parse and analyze a single F# source string using script-style project options.
let analyzeSource
    (checker: FSharpChecker)
    (sourceFileName: string)
    (source: string)
    (projectOptions: FSharpProjectOptions)
    =
    async {
        let sourceText = SourceText.ofString source

        let! parseResults, checkAnswer =
            checker.ParseAndCheckFileInProject(sourceFileName, 0, sourceText, projectOptions)

        if parseResults.ParseHadErrors then
            let errors =
                parseResults.Diagnostics |> Array.map (fun d -> d.Message) |> String.concat "; "

            return Error $"Parse errors: %s{errors}"
        else
            match checkAnswer with
            | FSharpCheckFileAnswer.Aborted -> return Error "Type checking aborted"
            | FSharpCheckFileAnswer.Succeeded checkResults ->
                let allUses = checkResults.GetAllUsesOfAllSymbolsInFile() |> Seq.toList

                // Collect definitions
                let definitions =
                    allUses
                    |> List.choose (fun u ->
                        if u.IsFromDefinition then
                            classifySymbol u.Symbol
                            |> Option.map (fun (kind, fullName) ->
                                { FullName = fullName
                                  Kind = kind
                                  SourceFile = sourceFileName
                                  LineStart = u.Range.StartLine
                                  LineEnd = u.Range.EndLine },
                                u)
                        else
                            None)

                let symbols = definitions |> List.map fst

                // Use the parsed AST to build proper scope ranges for module-level bindings.
                // Each SynBinding's range covers from `let` to end of body, so nested
                // local `let` bindings are contained within their parent's range.
                let moduleBindingRanges = collectModuleBindingRanges parseResults.ParseTree

                let findEnclosing (useRange: range) =
                    // Find the innermost module-level binding whose range contains this use
                    let candidates =
                        moduleBindingRanges
                        |> List.filter (fun (_, bindingRange) ->
                            useRange.StartLine >= bindingRange.StartLine
                            && useRange.EndLine <= bindingRange.EndLine)
                        |> List.sortByDescending (fun (_, r) -> r.StartLine) // innermost = largest start line

                    match candidates |> List.tryHead with
                    | None -> None
                    | Some(name, _) ->
                        // Find the corresponding symbol definition
                        definitions
                        |> List.tryFind (fun (si, _) -> si.FullName.EndsWith(name, StringComparison.Ordinal))
                        |> Option.map fst

                // For each non-definition use, find the enclosing definition
                let dependencies =
                    allUses
                    |> List.choose (fun u ->
                        if u.IsFromDefinition then
                            None
                        else
                            match classifySymbol u.Symbol with
                            | None -> None
                            | Some(_, usedFullName) ->
                                match findEnclosing u.Range with
                                | None -> None
                                | Some enclosingSi ->
                                    if enclosingSi.FullName = usedFullName then
                                        None // self-reference
                                    else
                                        Some
                                            { FromSymbol = enclosingSi.FullName
                                              ToSymbol = usedFullName
                                              Kind = classifyDependency u.Symbol })
                    |> List.distinct

                // Detect test methods
                let testMethods =
                    allUses
                    |> List.choose (fun u ->
                        if u.IsFromDefinition then
                            match u.Symbol with
                            | :? FSharpMemberOrFunctionOrValue as mfv when isTestAttribute mfv ->
                                let testClass, testMethod = extractTestClass mfv.FullName

                                Some
                                    { SymbolFullName = mfv.FullName
                                      TestProject = ""
                                      TestClass = testClass
                                      TestMethod = testMethod }
                            | _ -> None
                        else
                            None)

                return
                    Ok
                        { Symbols = symbols
                          Dependencies = dependencies
                          TestMethods = testMethods }
    }

/// Convenience: create project options from a script source string.
let getScriptOptions (checker: FSharpChecker) (sourceFileName: string) (source: string) =
    async {
        let sourceText = SourceText.ofString source

        let! projOptions, _diagnostics =
            checker.GetProjectOptionsFromScript(sourceFileName, sourceText, assumeDotNetFramework = false)

        return projOptions
    }
