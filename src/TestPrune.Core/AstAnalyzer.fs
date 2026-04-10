module TestPrune.AstAnalyzer

open System
open System.IO
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.CodeAnalysis.ProjectSnapshot
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

#nowarn "57" // Experimental snapshot API

open System.Threading

/// Serializes concurrent GetProjectOptionsFromScript calls.
/// FCS has internal state corruption when script options are loaded concurrently.
let private scriptSemaphore = new SemaphoreSlim(1, 1)

/// Discriminated union for kinds of F# symbols (function, type, DU case, etc.).
type SymbolKind =
    | Function
    | Type
    | DuCase
    | Module
    | Value
    | Property
    | ExternRef

/// A symbol's fully-qualified name, kind, source file, and line span.
type SymbolInfo =
    { FullName: string
      Kind: SymbolKind
      SourceFile: string
      LineStart: int
      LineEnd: int
      ContentHash: string
      IsExtern: bool }

[<Literal>]
let ExternSourceFile = "_extern"

/// The kind of edge in a dependency graph (calls, uses type, pattern match, etc.).
type DependencyKind =
    | Calls
    | UsesType
    | PatternMatches
    | References

/// A directed dependency from one symbol to another, with its kind.
type Dependency =
    { FromSymbol: string
      ToSymbol: string
      Kind: DependencyKind }

/// Maps an HTTP route (method + URL pattern) to its handler's source file.
type RouteHandlerEntry =
    { UrlPattern: string
      HttpMethod: string
      HandlerSourceFile: string }

/// Describes a test method's fully-qualified name, project, class, and method name.
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

/// The combined output of analyzing a source file: symbols, dependencies, and test methods.
/// Diagnostic counters for observability into the analysis pipeline.
/// These track how many symbols/edges were dropped during analysis
/// to help diagnose missing dependency edges.
type AnalysisDiagnostics =
    {
        /// Symbol uses where findEnclosing returned None (edge dropped)
        DroppedEdges: int
        /// Definitions that were classified but filtered out by isTrackedSymbol
        FilteredSymbols: int
        /// Total definitions before filtering
        TotalDefinitions: int
    }

    static member Zero =
        { DroppedEdges = 0
          FilteredSymbols = 0
          TotalDefinitions = 0 }

type AnalysisResult =
    { Symbols: SymbolInfo list
      Dependencies: Dependency list
      TestMethods: TestMethodInfo list
      Diagnostics: AnalysisDiagnostics }

    /// Create an AnalysisResult with default (zero) diagnostics.
    /// Useful for tests and manual construction where diagnostics aren't relevant.
    static member Create(symbols, dependencies, testMethods) =
        { Symbols = symbols
          Dependencies = dependencies
          TestMethods = testMethods
          Diagnostics = AnalysisDiagnostics.Zero }

/// Internal classification logic that can be tested with injected symbolClassifier.
/// This separation allows test code to provide mock symbols that throw exceptions.
let private tryClassifyEntity (entity: FSharpEntity) : (SymbolKind * string) option =
    try
        let fullName = entity.FullName

        if entity.IsFSharpModule then Some(Module, fullName)
        elif entity.IsFSharpUnion then Some(Type, fullName)
        elif entity.IsFSharpRecord then Some(Type, fullName)
        elif entity.IsEnum then Some(Type, fullName)
        elif entity.IsFSharpAbbreviation then Some(Type, fullName)
        else Some(Type, fullName)
    with :? InvalidOperationException ->
        None

let private tryClassifyMemberOrFunction (mfv: FSharpMemberOrFunctionOrValue) : (SymbolKind * string) option =
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

let private tryClassifyUnionCase (uc: FSharpUnionCase) : (SymbolKind * string) option =
    try
        Some(DuCase, uc.FullName)
    with :? InvalidOperationException ->
        None

let private classifySymbol (symbol: FSharpSymbol) : (SymbolKind * string) option =
    match symbol with
    | :? FSharpEntity as entity -> tryClassifyEntity entity
    | :? FSharpMemberOrFunctionOrValue as mfv -> tryClassifyMemberOrFunction mfv
    | :? FSharpUnionCase as uc -> tryClassifyUnionCase uc
    | _ -> None

// Test helpers - expose internal classification for unit testing exception paths
[<System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage>]
module internal TestHelpers =
    let testClassifySymbol = classifySymbol
    let testTryClassifyEntity = tryClassifyEntity
    let testTryClassifyMemberOrFunction = tryClassifyMemberOrFunction
    let testTryClassifyUnionCase = tryClassifyUnionCase

let private tryGetUnionParentType (symbol: FSharpSymbol) : string option =
    match symbol with
    | :? FSharpUnionCase as uc ->
        try
            Some uc.ReturnType.TypeDefinition.FullName
        with :? InvalidOperationException ->
            None
    | _ -> None

/// Recursively extract all concrete type argument entity full names from a type.
let private extractGenericTypeArgs (fsharpType: FSharpType) : string list =
    let rec collect (t: FSharpType) =
        try
            [ if t.HasTypeDefinition && not t.TypeDefinition.IsFSharpModule then
                  yield t.TypeDefinition.FullName
              for arg in t.GenericArguments do
                  yield! collect arg ]
        with :? InvalidOperationException ->
            []

    try
        if fsharpType.IsGenericParameter then
            []
        else
            fsharpType.GenericArguments |> Seq.toList |> List.collect collect
    with :? InvalidOperationException ->
        []

/// Extract generic type argument edges from a symbol use's full type.
let private tryGetGenericTypeArgEdges (symbol: FSharpSymbol) : string list =
    match symbol with
    | :? FSharpMemberOrFunctionOrValue as mfv ->
        try
            extractGenericTypeArgs mfv.FullType
        with :? InvalidOperationException ->
            []
    | _ -> []

/// When a record field is used, extract the containing record type's full name.
let private tryGetRecordTypeFromField (symbol: FSharpSymbol) : string option =
    match symbol with
    | :? FSharpMemberOrFunctionOrValue as mfv ->
        try
            match mfv.DeclaringEntity with
            | Some entity when entity.IsFSharpRecord -> Some entity.FullName
            | _ -> None
        with :? InvalidOperationException ->
            None
    | :? FSharpField as f ->
        try
            match f.DeclaringEntity with
            | Some entity when entity.IsFSharpRecord -> Some entity.FullName
            | _ -> None
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

let private hasAttribute (predicate: string -> bool) (mfv: FSharpMemberOrFunctionOrValue) : bool =
    try
        mfv.Attributes
        |> Seq.exists (fun attr -> predicate attr.AttributeType.DisplayName)
    with :? InvalidOperationException ->
        false

let private isTestAttribute =
    hasAttribute (fun name -> knownTestAttributes |> Set.contains name)

let private isDllImport =
    hasAttribute (fun name -> name = "DllImportAttribute" || name = "DllImport")

let private extractTestClass (fullName: string) : string * string =
    match fullName.LastIndexOf('.') with
    | -1 -> ("", fullName)
    | idx -> (fullName.Substring(0, idx), fullName.Substring(idx + 1))

/// Build the CLR-style class name from an entity chain.
/// F# compiles types inside modules as CLR nested types (Module+Type).
/// FCS FullName uses '.' throughout, but xUnit v3 --filter-class needs '+'.
let private buildClrClassName (entity: FSharpEntity) : string =
    let rec collect (e: FSharpEntity) acc =
        try
            match e.DeclaringEntity with
            | Some parent when parent.IsFSharpModule -> collect parent (e.CompiledName :: acc)
            | _ -> (e.FullName :: acc) |> String.concat "+"
        with :? System.InvalidOperationException ->
            (e.FullName :: acc) |> String.concat "+"

    collect entity []

/// Extract the binding name from a SynPat head pattern.
let private extractBindingName (pat: SynPat) : string option =
    match pat with
    | SynPat.LongIdent(longDotId = synLongIdent) ->
        let ids = synLongIdent.LongIdent
        Some(ids |> List.map (fun id -> id.idText) |> String.concat ".")
    | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
    | _ -> None // INT-WILDCARD-001:ok — SynPat has many cases; we only extract names from LongIdent/Named

/// Walk all module declarations in an impl file, recursing into nested modules.
let private walkImplDecls (tree: ParsedInput) (visitDecl: SynModuleDecl -> unit) =
    let rec walk (decl: SynModuleDecl) =
        visitDecl decl

        match decl with
        | SynModuleDecl.NestedModule(decls = decls) ->
            for d in decls do
                walk d
        | _ -> () // INT-WILDCARD-001:ok — SynModuleDecl has many cases; we only recurse into NestedModule

    match tree with
    | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
        for SynModuleOrNamespace(decls = decls) in modules do
            for d in decls do
                walk d
    | ParsedInput.SigFile _ -> ()

/// Extract a binding's name and compute its full range (from attributes through body end).
/// Shared by module-level and type member range collection.
let private addBindingRange
    (extractName: SynPat -> string option)
    (results: ResizeArray<string * range>)
    (binding: SynBinding)
    =
    match binding with
    | SynBinding(attributes = attributes; headPat = headPat; expr = bodyExpr; range = bindingRange) ->
        match extractName headPat with
        | Some n ->
            // The binding's `range` only covers the pattern (e.g. `let main args =`).
            // We need a range that spans from the binding start to the end of the body
            // so that nested local `let` bindings are contained within their parent.
            // Also include preceding attributes so that attribute changes affect the hash.
            let rangeStart =
                match attributes with
                | first :: _ -> first.Range.Start
                | [] -> bindingRange.Start

            let fullRange = Range.mkRange bindingRange.FileName rangeStart bodyExpr.Range.End
            results.Add(n, fullRange)
        | None -> ()

/// Extract the member method name from a SynPat head pattern.
/// Member patterns include the self identifier (e.g. `_.methodName` or `this.methodName`),
/// so we take only the last identifier component.
let private extractMemberName (pat: SynPat) : string option =
    extractBindingName pat
    |> Option.map (fun n ->
        let i = n.LastIndexOf('.')
        if i >= 0 then n.[i + 1 ..] else n)

/// Walk the parsed AST to collect module-level binding names and their full ranges.
/// Each binding's range covers from the `let` keyword to the end of its body,
/// which correctly encompasses nested local bindings.
let collectModuleBindingRanges (tree: ParsedInput) : (string * range) list =
    let results = ResizeArray()

    walkImplDecls tree (fun decl ->
        match decl with
        | SynModuleDecl.Let(bindings = bindings) ->
            for binding in bindings do
                addBindingRange extractBindingName results binding
        | _ -> ())

    results |> Seq.toList

/// Walk the parsed AST to collect type member binding names and their full ranges.
/// This enables `findEnclosing` to attribute symbol uses inside type members
/// (e.g. class-based xUnit test methods) to the correct enclosing member.
let collectTypeMemberRanges (tree: ParsedInput) : (string * range) list =
    let results = ResizeArray()

    walkImplDecls tree (fun decl ->
        match decl with
        | SynModuleDecl.Types(typeDefns = typeDefns) ->
            for SynTypeDefn(typeRepr = typeRepr; members = extraMembers) in typeDefns do
                let walkMember (memberDefn: SynMemberDefn) =
                    match memberDefn with
                    | SynMemberDefn.Member(memberDefn = binding) -> addBindingRange extractMemberName results binding
                    | _ -> ()

                for m in extraMembers do
                    walkMember m

                match typeRepr with
                | SynTypeDefnRepr.ObjectModel(members = members) ->
                    for m in members do
                        walkMember m
                | _ -> ()
        | _ -> ())

    results |> Seq.toList

/// Walk the parsed AST to collect type definition names and their full ranges.
/// Each type's range covers from the `type` keyword through all cases/fields/members.
let collectTypeDefnRanges (tree: ParsedInput) : (string * range) list =
    let results = ResizeArray()

    walkImplDecls tree (fun decl ->
        match decl with
        | SynModuleDecl.Types(typeDefns = typeDefns) ->
            for SynTypeDefn(typeInfo = SynComponentInfo(longId = ids); range = fullRange) in typeDefns do
                let name = ids |> List.map (fun id -> id.idText) |> String.concat "."
                results.Add(name, fullRange)
        | _ -> ())

    results |> Seq.toList

/// Compiled regex for collapsing whitespace runs — cached to avoid per-call recompilation.
let private whitespaceRun =
    System.Text.RegularExpressions.Regex(@"\s{2,}", System.Text.RegularExpressions.RegexOptions.Compiled)

/// Strip F# // and (* *) comments from a flat source string.
/// Preserves newlines so callers can split by line. String literals are left intact.
let private stripComments (flat: string) : string =
    let result = System.Text.StringBuilder(flat.Length)
    let mutable depth = 0 // nesting depth for (* *) block comments
    let mutable inLineComment = false
    let mutable inString = false
    let mutable inVerbatimString = false // @"..."
    let mutable i = 0

    while i < flat.Length do
        let c = flat[i]
        let next = if i + 1 < flat.Length then flat[i + 1] else '\000'

        if inLineComment then
            if c = '\n' then
                result.Append('\n') |> ignore
                inLineComment <- false
        elif depth > 0 then
            // Inside block comment — preserve newlines for line-number stability
            if c = '(' && next = '*' then
                depth <- depth + 1
                i <- i + 1
            elif c = '*' && next = ')' then
                depth <- depth - 1
                i <- i + 1
            elif c = '\n' then
                result.Append('\n') |> ignore
        elif inVerbatimString then
            result.Append(c) |> ignore

            if c = '"' then
                if next = '"' then
                    result.Append(next) |> ignore
                    i <- i + 1
                else
                    inVerbatimString <- false
        elif inString then
            result.Append(c) |> ignore

            if c = '\\' && next <> '\000' then
                result.Append(next) |> ignore
                i <- i + 1
            elif c = '"' then
                inString <- false
        elif c = '/' && next = '/' then
            inLineComment <- true
            i <- i + 1
        elif c = '(' && next = '*' then
            depth <- depth + 1
            i <- i + 1
        elif c = '@' && next = '"' then
            inVerbatimString <- true
            result.Append(c) |> ignore
            result.Append(next) |> ignore
            i <- i + 1
        elif c = '"' then
            inString <- true
            result.Append(c) |> ignore
        else
            result.Append(c) |> ignore

        i <- i + 1

    result.ToString()

/// Hash the source lines between startLine and endLine (1-based, inclusive),
/// stripping comments and normalizing whitespace so that comment-only changes
/// and layout-only changes (e.g. reformatting to add a comment block) don't
/// affect the hash.
let private hashSourceLines (lines: string array) (startLine: int) (endLine: int) : string =
    let start = max 0 (startLine - 1)
    let end' = min lines.Length endLine
    let flat = lines[start .. end' - 1] |> String.concat "\n"
    let stripped = stripComments flat
    // Join non-empty trimmed lines with a single space, then collapse internal
    // whitespace sequences. This makes `let f x = x + 1` hash identically to
    //   let f x =
    //       x + 1
    // so that reformatting to add a comment does not trigger a change.
    let tokens =
        stripped.Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (fun l -> l.Length > 0)

    let joined = String.concat " " tokens
    let content = whitespaceRun.Replace(joined, " ")

    let bytes =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))

    System.Convert.ToHexStringLower(bytes)

/// Extract analysis results from parse and type-check results.
/// Note: Some branches (Aborted case, exception handlers in tryClassify* functions,
/// and catch-all cases in classifyDependency) are defensive against rare FCS edge cases
/// and are difficult to test without malformed symbol mocks. These paths are present
/// for robustness but rarely exercised in normal usage.
let private extractResults
    (sourceFileName: string)
    (source: string)
    (parseResults: FSharpParseFileResults)
    (checkAnswer: FSharpCheckFileAnswer)
    (projectName: string)
    : Result<AnalysisResult, string> =
    if parseResults.ParseHadErrors then
        let errors =
            parseResults.Diagnostics |> Array.map (fun d -> d.Message) |> String.concat "; "

        Error $"Parse errors: %s{errors}"
    else
        match checkAnswer with
        | FSharpCheckFileAnswer.Aborted -> Error "Type checking aborted"
        | FSharpCheckFileAnswer.Succeeded checkResults ->
            let allUses = checkResults.GetAllUsesOfAllSymbolsInFile() |> Seq.toList
            let sourceLines = source.Split('\n')

            let moduleBindingRanges = collectModuleBindingRanges parseResults.ParseTree
            let typeMemberRanges = collectTypeMemberRanges parseResults.ParseTree
            let typeDefnRanges = collectTypeDefnRanges parseResults.ParseTree

            // Module-level and type member binding ranges are used for:
            // - isTrackedSymbol: deciding which Function/Value symbols are queryable
            // - definitionsByName: mapping short names to SymbolInfo for findEnclosing
            // - Hash range selection for Function/Value content hashing
            // Type definition ranges are NOT included here to avoid shadowing
            // binding names (e.g. type Config vs let config) in the map.
            let memberBindingRanges = moduleBindingRanges @ typeMemberRanges

            // Pre-build Maps keyed by simple (unqualified) name for O(log M) range lookup
            // rather than O(M) List.tryFind scans per symbol.
            let allBindingRangeMap = memberBindingRanges |> Map.ofList
            let typeDefnRangeMap = typeDefnRanges |> Map.ofList

            // Extract the last dot-delimited component of a fully-qualified name,
            // stripping surrounding backticks so it matches AST-derived identifiers
            // (idText never includes backticks).
            let inline shortName (n: string) =
                let i = n.LastIndexOf('.')
                let s = if i >= 0 then n.[i + 1 ..] else n

                if s.Length > 4 && s.StartsWith("``") && s.EndsWith("``") then
                    s.[2 .. s.Length - 3]
                else
                    s

            let definitions =
                allUses
                |> List.choose (fun u ->
                    if u.IsFromDefinition then
                        classifySymbol u.Symbol
                        |> Option.map (fun (kind, fullName) ->
                            let isExtern =
                                match u.Symbol with
                                | :? FSharpMemberOrFunctionOrValue as mfv -> isDllImport mfv
                                | _ -> false

                            // Use AST-derived ranges for hashing when available:
                            // - Type symbols: use full SynTypeDefn range (includes all DU cases, record fields)
                            // - Function/Value symbols: use binding range (includes attributes)
                            // - Fallback: use FCS's u.Range
                            let hashStart, hashEnd =
                                let sn = shortName fullName

                                match kind with
                                | Type ->
                                    typeDefnRangeMap
                                    |> Map.tryFind sn
                                    |> Option.map (fun r -> r.StartLine, r.EndLine)
                                    |> Option.defaultValue (u.Range.StartLine, u.Range.EndLine)
                                | Function
                                | Value ->
                                    allBindingRangeMap
                                    |> Map.tryFind sn
                                    |> Option.map (fun r -> r.StartLine, r.EndLine)
                                    |> Option.defaultValue (u.Range.StartLine, u.Range.EndLine)
                                | _ -> u.Range.StartLine, u.Range.EndLine

                            { FullName = fullName
                              Kind = kind
                              SourceFile = sourceFileName
                              LineStart = u.Range.StartLine
                              LineEnd = u.Range.EndLine
                              ContentHash = hashSourceLines sourceLines hashStart hashEnd
                              IsExtern = isExtern },
                            u)
                    else
                        None)

            let isTrackedSymbol (symbolInfo: SymbolInfo) =
                // A symbol is tracked if:
                // 1. It's a Type, Module, DuCase, or Property (these are always queryable), OR
                // 2. It's a Value/Function that's a module-level binding or type member (by name).
                match symbolInfo.Kind with
                | Type
                | Module
                | DuCase
                | Property -> true
                | Function
                | Value -> allBindingRangeMap |> Map.containsKey (shortName symbolInfo.FullName)
                | ExternRef -> false

            let symbols =
                definitions
                |> List.choose (fun (symbolInfo, _) -> if isTrackedSymbol symbolInfo then Some symbolInfo else None)

            // All ranges for findEnclosing: member bindings + type definitions.
            // Type definition ranges enable edge attribution for interface implementation
            // references (e.g. `interface IFoo with`) which sit at the type level,
            // outside any member body.
            let allEnclosingRanges = (memberBindingRanges @ typeDefnRanges) |> Array.ofList

            // Pre-build a map from binding short name to SymbolInfo for O(log N) lookup.
            // Includes symbols from both binding ranges and type definition ranges
            // so findEnclosing can resolve both member-level and type-level enclosures.
            let definitionsByName =
                definitions
                |> List.choose (fun (si, _) ->
                    let sn = shortName si.FullName

                    if
                        allBindingRangeMap |> Map.containsKey sn
                        || typeDefnRangeMap |> Map.containsKey sn
                    then
                        Some(sn, si)
                    else
                        None)
                |> Map.ofList

            let findEnclosing (useRange: range) =
                let mutable best: (string * range) voption = ValueNone

                for i in 0 .. allEnclosingRanges.Length - 1 do
                    let name, bindingRange = allEnclosingRanges[i]

                    if
                        useRange.StartLine >= bindingRange.StartLine
                        && useRange.EndLine <= bindingRange.EndLine
                    then
                        match best with
                        | ValueNone -> best <- ValueSome(name, bindingRange)
                        | ValueSome(_, bestRange) ->
                            let span = bindingRange.EndLine - bindingRange.StartLine
                            let bestSpan = bestRange.EndLine - bestRange.StartLine

                            if span < bestSpan then
                                best <- ValueSome(name, bindingRange)

                best
                |> function
                    | ValueSome(name, _) -> definitionsByName |> Map.tryFind name
                    | ValueNone -> None

            let mutable droppedEdgeCount = 0

            let dependencies =
                allUses
                |> List.collect (fun u ->
                    if u.IsFromDefinition then
                        []
                    else
                        // Handle FSharpField uses (e.g. record construction { Name = "Alice" })
                        // which are not covered by classifySymbol
                        let fieldRecordEdge =
                            match u.Symbol with
                            | :? FSharpField ->
                                match tryGetRecordTypeFromField u.Symbol with
                                | Some recName ->
                                    match findEnclosing u.Range with
                                    | Some enclosingSi when enclosingSi.FullName <> recName ->
                                        [ { FromSymbol = enclosingSi.FullName
                                            ToSymbol = recName
                                            Kind = UsesType } ]
                                    | _ -> []
                                | None -> []
                            | _ -> []

                        match classifySymbol u.Symbol with
                        | None -> fieldRecordEdge
                        | Some(_, usedFullName) ->
                            match findEnclosing u.Range with
                            | None ->
                                droppedEdgeCount <- droppedEdgeCount + 1
                                fieldRecordEdge
                            | Some enclosingSi ->
                                if enclosingSi.FullName = usedFullName then
                                    fieldRecordEdge
                                else
                                    let primary =
                                        { FromSymbol = enclosingSi.FullName
                                          ToSymbol = usedFullName
                                          Kind = classifyDependency u.Symbol }

                                    let parentEdge =
                                        tryGetUnionParentType u.Symbol
                                        |> Option.bind (fun parentName ->
                                            if parentName = enclosingSi.FullName || parentName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = parentName
                                                      Kind = UsesType })

                                    let genericArgEdges =
                                        tryGetGenericTypeArgEdges u.Symbol
                                        |> List.choose (fun argName ->
                                            if argName = enclosingSi.FullName || argName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = argName
                                                      Kind = UsesType })

                                    let recordTypeEdge =
                                        tryGetRecordTypeFromField u.Symbol
                                        |> Option.bind (fun recName ->
                                            if recName = enclosingSi.FullName || recName = usedFullName then
                                                None
                                            else
                                                Some
                                                    { FromSymbol = enclosingSi.FullName
                                                      ToSymbol = recName
                                                      Kind = UsesType })

                                    primary :: (parentEdge |> Option.toList)
                                    @ genericArgEdges
                                    @ (recordTypeEdge |> Option.toList))
                |> Seq.distinct
                |> Seq.toList

            let testMethods =
                allUses
                |> List.choose (fun u ->
                    if u.IsFromDefinition then
                        match u.Symbol with
                        | :? FSharpMemberOrFunctionOrValue as mfv when isTestAttribute mfv ->
                            let fallbackClass, testMethod = extractTestClass mfv.FullName

                            let testClass =
                                try
                                    match mfv.DeclaringEntity with
                                    | Some entity -> buildClrClassName entity
                                    | None -> fallbackClass
                                with :? System.InvalidOperationException ->
                                    fallbackClass

                            Some
                                { SymbolFullName = mfv.FullName
                                  TestProject = projectName
                                  TestClass = testClass
                                  TestMethod = testMethod }
                        | _ -> None
                    else
                        None)

            // Collect extern symbols: ToSymbol names in dependencies that aren't
            // defined in this file. These are cross-assembly references that need
            // to exist in the symbols table for dependency edges to resolve.
            let localSymbolNames = symbols |> List.map (fun s -> s.FullName) |> Set.ofList

            let externSymbols =
                let seen = System.Collections.Generic.HashSet<string>()

                dependencies
                |> List.choose (fun d ->
                    let name = d.ToSymbol

                    if seen.Add(name) && not (Set.contains name localSymbolNames) then
                        Some
                            { FullName = name
                              Kind = ExternRef
                              SourceFile = ExternSourceFile
                              LineStart = 0
                              LineEnd = 0
                              ContentHash = ""
                              IsExtern = true }
                    else
                        None)

            let allSymbols = symbols @ externSymbols

            let filteredSymbolCount = definitions.Length - symbols.Length

            Ok
                { Symbols = allSymbols
                  Dependencies = dependencies
                  TestMethods = testMethods
                  Diagnostics =
                    { DroppedEdges = droppedEdgeCount
                      FilteredSymbols = filteredSymbolCount
                      TotalDefinitions = definitions.Length } }

/// Parse and analyze a single F# source string using project options.
let analyzeSource
    (checker: FSharpChecker)
    (sourceFileName: string)
    (source: string)
    (projectOptions: FSharpProjectOptions)
    (projectName: string)
    =
    async {
        let sourceText = SourceText.ofString source

        let! parseResults, checkAnswer =
            checker.ParseAndCheckFileInProject(sourceFileName, 0, sourceText, projectOptions)

        return extractResults sourceFileName source parseResults checkAnswer projectName
    }

/// Parse and analyze a single F# source file using a project snapshot.
/// FCS internally caches results for files with unchanged version strings.
let analyzeSourceWithSnapshot
    (checker: FSharpChecker)
    (sourceFileName: string)
    (source: string)
    (projectSnapshot: FSharpProjectSnapshot)
    (projectName: string)
    =
    async {
        let! parseResults, checkAnswer = checker.ParseAndCheckFileInProject(sourceFileName, projectSnapshot)

        return extractResults sourceFileName source parseResults checkAnswer projectName
    }

/// Create a project snapshot from project options, using file modification times as version keys.
/// FCS uses version strings to skip re-checking files that haven't changed.
let createProjectSnapshot (projectOptions: FSharpProjectOptions) =
    let getFileSnapshot (_opts: FSharpProjectOptions) (fileName: string) =
        async {
            let version =
                if File.Exists(fileName) then
                    File.GetLastWriteTimeUtc(fileName).Ticks |> string
                else
                    "0"

            let getSource () =
                task {
                    let text =
                        if File.Exists(fileName) then
                            File.ReadAllText(fileName)
                        else
                            ""

                    return SourceTextNew.ofString text
                }

            return FSharpFileSnapshot.Create(fileName, version, getSource)
        }

    FSharpProjectSnapshot.FromOptions(projectOptions, getFileSnapshot)

/// Detect 'open' statements in source code to find cross-file dependencies.
let private detectOpenedModules (source: string) : string list =
    source.Split('\n')
    |> Array.map (fun line ->
        let trimmed = line.Trim()

        if trimmed.StartsWith("open ") then
            trimmed.Substring(5).Trim()
        else
            "")
    |> Array.filter (fun s -> not (String.IsNullOrEmpty(s)))
    |> Array.toList

/// Find script files in the same directory that match opened modules.
/// Returns both actual files on disk and hypothetical files that match the module names.
let private findRelatedScriptFiles (currentFile: string) (openedModules: string list) : string list =
    if List.isEmpty openedModules then
        []
    else
        let dirPath = Path.GetDirectoryName(currentFile)

        try
            let availableFiles =
                if Directory.Exists(dirPath) then
                    Directory.GetFiles(dirPath, "*.fsx")
                    |> Array.filter (fun f -> f <> currentFile)
                    |> Array.toList
                else
                    []

            // Actual files found on disk
            let foundFiles =
                availableFiles
                |> List.filter (fun f ->
                    let fileName = Path.GetFileNameWithoutExtension(f)
                    openedModules |> List.exists (fun m -> m = fileName))

            // Also construct hypothetical paths for opened modules in case files don't exist yet
            // This handles test cases where files are analyzed in memory
            let hypotheticalFiles =
                openedModules
                |> List.map (fun m -> Path.Combine(dirPath, m + ".fsx"))
                |> List.filter (fun f -> f <> currentFile && not (List.contains f foundFiles))

            foundFiles @ hypotheticalFiles |> List.distinct
        with ex ->
            eprintfn $"  Warning: findRelatedScriptFiles failed: %s{ex.Message}"
            []

/// FCS GetProjectOptionsFromScript can return paths relative to the script location
/// rather than the process cwd. Normalise them so FCS can load assemblies regardless
/// of where the CLI was invoked.
let internal resolveToAbsolute (basePath: string) (path: string) =
    if String.IsNullOrEmpty(path) || Path.IsPathRooted(path) then
        path
    else
        Path.GetFullPath(Path.Combine(basePath, path))

/// -r: entries in OtherOptions are DLL reference paths that may be relative.
/// Other entries (e.g. --noframework) are compiler flags, not paths — leave them untouched.
let private resolveReferenceOptions (baseDir: string) (opts: string array) =
    opts
    |> Array.map (fun opt ->
        if opt.StartsWith("-r:", StringComparison.Ordinal) then
            let path = opt[3..]
            let resolved = resolveToAbsolute baseDir path

            if Object.ReferenceEquals(resolved, path) then
                opt
            else
                "-r:" + resolved
        else
            opt)

/// Convenience: create project options from a script source string.
/// Detects 'open' statements and includes related script files in the SourceFiles array.
let getScriptOptions (checker: FSharpChecker) (sourceFileName: string) (source: string) =
    async {
        let sourceText = SourceText.ofString source

        let! ct = Async.CancellationToken
        do! scriptSemaphore.WaitAsync(ct) |> Async.AwaitTask

        let! projOptions, _diagnostics =
            try
                checker.GetProjectOptionsFromScript(sourceFileName, sourceText, assumeDotNetFramework = false)
            finally
                scriptSemaphore.Release() |> ignore

        // Detect opened modules and find related script files
        let openedModules = detectOpenedModules source
        let relatedFiles = findRelatedScriptFiles sourceFileName openedModules

        // Include related files in the project options
        let enhancedSourceFiles =
            if List.isEmpty relatedFiles then
                projOptions.SourceFiles
            else
                (relatedFiles |> List.toArray)
                |> Array.append projOptions.SourceFiles
                |> Array.distinct

        let baseDir =
            match Path.GetDirectoryName(sourceFileName) with
            | null -> Directory.GetCurrentDirectory()
            | dir -> dir

        let enhancedOptions =
            { projOptions with
                SourceFiles = enhancedSourceFiles |> Array.map (resolveToAbsolute baseDir)
                OtherOptions = projOptions.OtherOptions |> resolveReferenceOptions baseDir }

        return enhancedOptions
    }
