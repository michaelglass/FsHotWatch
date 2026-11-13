/// Prose-shaped string literals shared by a test and the production symbol that emits
/// them: which literals count, the synthetic node that joins the two, and the parse-tree
/// walk that finds them. `AstAnalyzer` emits the edges.
module internal TestPrune.SharedLiterals

open System
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Prefix for the synthetic join node used when a test and a production symbol contain
/// the same prose-shaped string literal. The literal itself is never persisted.
[<Literal>]
let SyntheticLiteralPrefix = "TestPrune.__Literal__."

[<Literal>]
let private MinSharedLiteralLength = 24

[<Literal>]
let private MinSharedLiteralWords = 4

let private isSharedLiteral (text: string) =
    text.Length >= MinSharedLiteralLength
    && text.Split([| ' '; '\t'; '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries).Length
       >= MinSharedLiteralWords

let sharedLiteralNodeName (text: string) =
    let bytes =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))

    SyntheticLiteralPrefix + System.Convert.ToHexStringLower(bytes)

/// Collect decoded ordinary string constants from the parsed tree.
///
/// This deliberately reads `SynConst.String`: FCS has already decoded escapes and all
/// supported ordinary/verbatim/triple-quoted spellings to their runtime value, so two
/// source spellings of the same message share one node. Interpolated strings are a
/// different AST case and are skipped as a whole; their static fragments are not values
/// that a test can assert independently of the fills.
let collectSharedLiterals (tree: ParsedInput) : (string * range) list =
    let found = ResizeArray<string * range>()

    let add text expressionRange =
        if isSharedLiteral text then
            found.Add(text, expressionRange)

    // FCS's full-tree fold intentionally treats attribute applications as leaves and
    // skips exception declarations. Supplement those exact branches with a typed
    // expression walk rather than falling back to object reflection.
    let rec walkSupplementaryExpression expression =
        match expression with
        | SynExpr.Const(SynConst.String(text = text), expressionRange) -> add text expressionRange
        | SynExpr.Const _
        | SynExpr.Typar _
        | SynExpr.Ident _
        | SynExpr.LongIdent _
        | SynExpr.Null _
        | SynExpr.ImplicitZero _
        | SynExpr.ArbitraryAfterError _ -> ()
        | SynExpr.InterpolatedString _ -> ()
        | SynExpr.Quote(operator = operator; quotedExpr = quotedExpression) ->
            walkSupplementaryExpression operator
            walkSupplementaryExpression quotedExpression
        | SynExpr.Paren(expr = nested)
        | SynExpr.Typed(expr = nested)
        | SynExpr.TypeApp(expr = nested)
        | SynExpr.ArrayOrListComputed(expr = nested)
        | SynExpr.ComputationExpr(expr = nested)
        | SynExpr.LongIdentSet(expr = nested)
        | SynExpr.Do(expr = nested)
        | SynExpr.DoBang(expr = nested)
        | SynExpr.Assert(expr = nested)
        | SynExpr.Fixed(expr = nested)
        | SynExpr.DebugPoint(innerExpr = nested)
        | SynExpr.AddressOf(expr = nested)
        | SynExpr.TraitCall(argExpr = nested)
        | SynExpr.Lazy(expr = nested)
        | SynExpr.InferredUpcast(expr = nested)
        | SynExpr.InferredDowncast(expr = nested)
        | SynExpr.YieldOrReturn(expr = nested)
        | SynExpr.YieldOrReturnFrom(expr = nested)
        | SynExpr.FromParseError(expr = nested)
        | SynExpr.DiscardAfterMissingQualificationAfterDot(expr = nested)
        | SynExpr.IndexFromEnd(expr = nested)
        | SynExpr.DotGet(expr = nested)
        | SynExpr.DotLambda(expr = nested)
        | SynExpr.New(expr = nested)
        | SynExpr.TypeTest(expr = nested)
        | SynExpr.Upcast(expr = nested)
        | SynExpr.Downcast(expr = nested)
        | SynExpr.LibraryOnlyUnionCaseFieldGet(expr = nested) -> walkSupplementaryExpression nested
        | SynExpr.Tuple(exprs = expressions)
        | SynExpr.ArrayOrList(exprs = expressions) -> List.iter walkSupplementaryExpression expressions
        | SynExpr.App(funcExpr = functionExpression; argExpr = argumentExpression)
        | SynExpr.Sequential(expr1 = functionExpression; expr2 = argumentExpression)
        | SynExpr.Set(targetExpr = functionExpression; rhsExpr = argumentExpression)
        | SynExpr.DotSet(targetExpr = functionExpression; rhsExpr = argumentExpression)
        | SynExpr.TryFinally(tryExpr = functionExpression; finallyExpr = argumentExpression)
        | SynExpr.While(whileExpr = functionExpression; doExpr = argumentExpression)
        | SynExpr.WhileBang(whileExpr = functionExpression; doExpr = argumentExpression)
        | SynExpr.DotIndexedGet(objectExpr = functionExpression; indexArgs = argumentExpression)
        | SynExpr.JoinIn(lhsExpr = functionExpression; rhsExpr = argumentExpression)
        | SynExpr.NamedIndexedPropertySet(expr1 = functionExpression; expr2 = argumentExpression) ->
            walkSupplementaryExpression functionExpression
            walkSupplementaryExpression argumentExpression
        | SynExpr.Dynamic(funcExpr = functionExpression; argExpr = argumentExpression)
        | SynExpr.LibraryOnlyStaticOptimization(expr = functionExpression; optimizedExpr = argumentExpression)
        | SynExpr.LibraryOnlyUnionCaseFieldSet(expr = functionExpression; rhsExpr = argumentExpression) ->
            walkSupplementaryExpression functionExpression
            walkSupplementaryExpression argumentExpression
        | SynExpr.SequentialOrImplicitYield(expr1 = first; expr2 = second; ifNotStmt = third) ->
            walkSupplementaryExpression first
            walkSupplementaryExpression second
            walkSupplementaryExpression third
        | SynExpr.For(identBody = first; toBody = second; doBody = third)
        | SynExpr.DotIndexedSet(objectExpr = first; indexArgs = second; valueExpr = third)
        | SynExpr.DotNamedIndexedPropertySet(targetExpr = first; argExpr = second; rhsExpr = third) ->
            walkSupplementaryExpression first
            walkSupplementaryExpression second
            walkSupplementaryExpression third
        | SynExpr.IfThenElse(ifExpr = condition; thenExpr = ifTrue; elseExpr = ifFalse) ->
            walkSupplementaryExpression condition
            walkSupplementaryExpression ifTrue
            Option.iter walkSupplementaryExpression ifFalse
        | SynExpr.IndexRange(expr1 = first; expr2 = second) ->
            Option.iter walkSupplementaryExpression first
            Option.iter walkSupplementaryExpression second
        | SynExpr.Lambda(body = body) -> walkSupplementaryExpression body
        | SynExpr.LetOrUse(letOrUse) ->
            List.iter walkSupplementaryBinding letOrUse.Bindings
            walkSupplementaryExpression letOrUse.Body
        | SynExpr.Match(expr = input; clauses = clauses)
        | SynExpr.MatchBang(expr = input; clauses = clauses)
        | SynExpr.TryWith(tryExpr = input; withCases = clauses) ->
            walkSupplementaryExpression input
            List.iter walkSupplementaryClause clauses
        | SynExpr.MatchLambda(matchClauses = clauses) -> List.iter walkSupplementaryClause clauses
        | SynExpr.ForEach(enumExpr = input; bodyExpr = body) ->
            walkSupplementaryExpression input
            walkSupplementaryExpression body
        | SynExpr.AnonRecd(copyInfo = copyInfo; recordFields = fields) ->
            copyInfo |> Option.iter (fst >> walkSupplementaryExpression)

            // FCS 43.13 (F# 11 record spreads): a field is a Field or a `...spread`.
            for field in fields do
                match field with
                | SynExprAnonRecordFieldOrSpread.Field(SynExprAnonRecordField(expr = fieldExpression), _)
                | SynExprAnonRecordFieldOrSpread.Spread(SynExprSpread(expr = fieldExpression), _) ->
                    walkSupplementaryExpression fieldExpression
        | SynExpr.Record(baseInfo = baseInfo; copyInfo = copyInfo; recordFields = fields) ->
            baseInfo
            |> Option.iter (fun (_, expression, _, _, _) -> walkSupplementaryExpression expression)

            copyInfo |> Option.iter (fst >> walkSupplementaryExpression)

            for field in fields do
                match field with
                | SynExprRecordFieldOrSpread.Field(SynExprRecordField(expr = fieldExpression), _) ->
                    Option.iter walkSupplementaryExpression fieldExpression
                | SynExprRecordFieldOrSpread.Spread(SynExprSpread(expr = spreadExpression), _) ->
                    walkSupplementaryExpression spreadExpression
        | SynExpr.ObjExpr(argOptions = baseCall; bindings = bindings; members = members; extraImpls = interfaces) ->
            baseCall |> Option.iter (fst >> walkSupplementaryExpression)
            List.iter walkSupplementaryBinding bindings
            List.iter walkSupplementaryMember members

            for SynInterfaceImpl(bindings = interfaceBindings; members = interfaceMembers) in interfaces do
                List.iter walkSupplementaryBinding interfaceBindings
                List.iter walkSupplementaryMember interfaceMembers
        | SynExpr.LibraryOnlyILAssembly(args = arguments) -> List.iter walkSupplementaryExpression arguments

    and walkSupplementaryClause (SynMatchClause(whenExpr = guard; resultExpr = body)) =
        Option.iter walkSupplementaryExpression guard
        walkSupplementaryExpression body

    and walkSupplementaryBinding (SynBinding(attributes = attributes; expr = expression; returnInfo = returnInfo)) =
        walkAttributeLists attributes

        match returnInfo with
        | Some(SynBindingReturnInfo(attributes = returnAttributes)) -> walkAttributeLists returnAttributes
        | None -> ()

        walkSupplementaryExpression expression

    and walkSupplementaryMember memberDefinition =
        let (SyntaxNode.Attributes attributes) = SyntaxNode.SynMemberDefn memberDefinition

        walkAttributeLists attributes

        match memberDefinition with
        | SynMemberDefn.Member(memberDefn = binding) -> walkSupplementaryBinding binding
        | SynMemberDefn.GetSetMember(memberDefnForGet = getter; memberDefnForSet = setter) ->
            Option.iter walkSupplementaryBinding getter
            Option.iter walkSupplementaryBinding setter
        | SynMemberDefn.AutoProperty(synExpr = expression) -> walkSupplementaryExpression expression
        | SynMemberDefn.LetBindings(bindings = bindings) -> List.iter walkSupplementaryBinding bindings
        | SynMemberDefn.ImplicitInherit(inheritArgs = expression) -> walkSupplementaryExpression expression
        | SynMemberDefn.Interface(members = Some members) -> List.iter walkSupplementaryMember members
        | SynMemberDefn.ValField(fieldInfo = SynField(attributes = fieldAttributes)) ->
            walkAttributeLists fieldAttributes
        | SynMemberDefn.NestedType(typeDefn = typeDefinition) -> walkSupplementaryTypeDefinition typeDefinition
        | SynMemberDefn.Open _
        | SynMemberDefn.ImplicitCtor _
        | SynMemberDefn.AbstractSlot _
        | SynMemberDefn.Interface(members = None)
        | SynMemberDefn.Inherit _ -> ()

    and walkAttributeLists (attributeLists: SynAttributes) =
        for attributeList in attributeLists do
            for attribute in attributeList.Attributes do
                walkSupplementaryExpression attribute.ArgExpr

    and walkSupplementaryTypeDefinition
        (SynTypeDefn(
            typeInfo = SynComponentInfo(attributes = attributes)
            typeRepr = representation
            members = members
            implicitConstructor = implicitConstructor) as typeDefinition)
        =
        let (SyntaxNode.Attributes nodeAttributes) = SyntaxNode.SynTypeDefn typeDefinition

        walkAttributeLists nodeAttributes
        walkAttributeLists attributes

        match representation with
        | SynTypeDefnRepr.ObjectModel(members = representationMembers) ->
            List.iter walkSupplementaryMember representationMembers
        | SynTypeDefnRepr.Simple(simpleRepr = simpleRepresentation) ->
            match simpleRepresentation with
            | SynTypeDefnSimpleRepr.Record(recordFieldsAndSpreads = fields) ->
                for field in fields do
                    match field with
                    | SynFieldOrSpread.Field(SynField(attributes = fieldAttributes)) -> walkAttributeLists fieldAttributes
                    | SynFieldOrSpread.Spread _ -> ()
            | SynTypeDefnSimpleRepr.Union(unionCases = cases) -> List.iter walkUnionCaseAttributes cases
            | SynTypeDefnSimpleRepr.Enum(cases = cases) ->
                for SynEnumCase(attributes = caseAttributes; valueExpr = valueExpression) in cases do
                    walkAttributeLists caseAttributes
                    walkSupplementaryExpression valueExpression
            | SynTypeDefnSimpleRepr.Exception exceptionRepresentation ->
                walkExceptionRepresentation exceptionRepresentation
            | SynTypeDefnSimpleRepr.General(fields = fields) ->
                for SynField(attributes = fieldAttributes) in fields do
                    walkAttributeLists fieldAttributes
            | SynTypeDefnSimpleRepr.TypeAbbrev _
            | SynTypeDefnSimpleRepr.LibraryOnlyILAssembly _
            | SynTypeDefnSimpleRepr.None _ -> ()
        | SynTypeDefnRepr.Exception exceptionRepresentation -> walkExceptionRepresentation exceptionRepresentation

        List.iter walkSupplementaryMember members
        Option.iter walkSupplementaryMember implicitConstructor

    and walkUnionCaseAttributes (SynUnionCase(attributes = attributes; caseType = caseType)) =
        walkAttributeLists attributes

        match caseType with
        | SynUnionCaseKind.Fields fields ->
            for SynField(attributes = fieldAttributes) in fields do
                walkAttributeLists fieldAttributes
        | _ -> ()

    and walkExceptionRepresentation (SynExceptionDefnRepr(attributes = attributes; caseName = caseName)) =
        walkAttributeLists attributes
        walkUnionCaseAttributes caseName

    and walkExceptionDefinition (SynExceptionDefn(exnRepr = representation; members = members)) =
        walkExceptionRepresentation representation

        for memberDefinition in members do
            walkSupplementaryMember memberDefinition

    let isInsideInterpolation path =
        path
        |> List.exists (function
            | SyntaxNode.SynExpr(SynExpr.InterpolatedString _) -> true
            | _ -> false)

    ParsedInput.fold
        (fun () path node ->
            match node with
            | SyntaxNode.SynExpr(SynExpr.Const(SynConst.String(text = text), expressionRange)) when
                not (isInsideInterpolation path)
                ->
                add text expressionRange
            | _ -> ()

            let (SyntaxNode.Attributes attributeLists) = node
            walkAttributeLists attributeLists

            match node with
            | SyntaxNode.SynModule(SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(attributes = attributes))) ->
                walkAttributeLists attributes
            | SyntaxNode.SynModule(SynModuleDecl.Exception(exnDefn = exceptionDefinition)) ->
                walkExceptionDefinition exceptionDefinition
            | SyntaxNode.SynTypeDefn(SynTypeDefn(typeRepr = SynTypeDefnRepr.Simple(simpleRepr = simpleRepr))) ->
                match simpleRepr with
                | SynTypeDefnSimpleRepr.Record(recordFieldsAndSpreads = fields) ->
                    for field in fields do
                        match field with
                        | SynFieldOrSpread.Field(SynField(attributes = fieldAttributes)) -> walkAttributeLists fieldAttributes
                        | SynFieldOrSpread.Spread _ -> ()
                | SynTypeDefnSimpleRepr.Union(unionCases = cases) -> List.iter walkUnionCaseAttributes cases
                | SynTypeDefnSimpleRepr.Enum(cases = cases) ->
                    for SynEnumCase(attributes = caseAttributes) in cases do
                        walkAttributeLists caseAttributes
                | _ -> ()
            | SyntaxNode.SynTypeDefn(SynTypeDefn(typeRepr = SynTypeDefnRepr.Exception exceptionRepresentation)) ->
                let (SynExceptionDefnRepr(attributes = attributes; caseName = caseName)) =
                    exceptionRepresentation

                walkAttributeLists attributes
                walkUnionCaseAttributes caseName
            | _ -> ())
        ()
        tree
    |> ignore

    found |> Seq.distinct |> Seq.toList
