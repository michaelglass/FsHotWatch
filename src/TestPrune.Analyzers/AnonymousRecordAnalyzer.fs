/// Opt-in FSharp.Analyzers.SDK analyzer that flags anonymous-record usage.
///
/// Anonymous records (`{| Year = d.Year |}` expressions and `{| Year: int |}` type
/// annotations) have no stable, cross-build name, so TestPrune's AST impact analysis
/// SKIPS them (see `TestPrune.AstAnalyzer.tryName`). A test or symbol coupled to a
/// change ONLY through an anonymous record is therefore invisible to impact selection:
/// editing the producing/consuming code will not pull the dependent test.
///
/// This analyzer lets precision-sensitive repositories opt in (by loading the package)
/// to a diagnostic that flags every anonymous-record occurrence and steers the author
/// to a tracked alternative (a named record, or an explicit `[<TestPrune.DependsOnFile>]`
/// / `[<TestPrune.DependsOnGlob>]` edge from `TestPrune.Attributes`).
module TestPrune.Analyzers.AnonymousRecordAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Stable analyzer code. `TP001` is the first TestPrune analyzer diagnostic.
[<Literal>]
let Code = "TP001"

/// Stable analyzer name.
[<Literal>]
let Name = "TestPrune.AnonymousRecord"

/// Human-readable diagnostic message. Explains the impact-analysis blind spot and the fix.
[<Literal>]
let Message =
    "Anonymous record is invisible to TestPrune impact analysis — coupling through it won't \
     select dependent tests. Promote to a named record, or make the dependency explicit with \
     [<TestPrune.DependsOnFile>] / [<TestPrune.DependsOnGlob>] (TestPrune.Attributes)."

/// Walks an entire parsed input and collects the `range` of every anonymous-record
/// expression (`SynExpr.AnonRecd`) and anonymous-record type (`SynType.AnonRecd`).
///
/// The walk is exhaustive over the syntactic constructs that can nest the two target
/// nodes: module/namespace declarations, member/let bindings, expressions, patterns
/// (which can carry type annotations), and types. Each occurrence yields one range, so
/// nested and repeated anonymous records each produce their own diagnostic.
let collectAnonRecordRanges (input: ParsedInput) : range list =
    let ranges = ResizeArray<range>()

    let rec walkType (ty: SynType) =
        match ty with
        | SynType.AnonRecd(fields = fields; range = r) ->
            ranges.Add r
            // Field types of an anonymous record can themselves be anonymous records.
            for (_, t) in fields do
                walkType t
        | SynType.App(typeName = t; typeArgs = args) ->
            walkType t
            List.iter walkType args
        | SynType.LongIdentApp(typeName = t; typeArgs = args) ->
            walkType t
            List.iter walkType args
        | SynType.Tuple(path = segs) ->
            for seg in segs do
                match seg with
                | SynTupleTypeSegment.Type t -> walkType t
                | _ -> ()
        | SynType.Array(elementType = t) -> walkType t
        | SynType.Fun(argType = a; returnType = b) ->
            walkType a
            walkType b
        | SynType.WithGlobalConstraints(typeName = t) -> walkType t
        | SynType.HashConstraint(innerType = t) -> walkType t
        | SynType.MeasurePower(baseMeasure = t) -> walkType t
        | SynType.Paren(innerType = t) -> walkType t
        | SynType.SignatureParameter(usedType = t) -> walkType t
        | SynType.Or(lhsType = a; rhsType = b) ->
            walkType a
            walkType b
        | SynType.WithNull(innerType = t) -> walkType t
        | _ -> ()

    let rec walkPat (pat: SynPat) =
        match pat with
        | SynPat.Typed(pat = p; targetType = t) ->
            walkPat p
            walkType t
        | SynPat.Paren(pat = p) -> walkPat p
        | SynPat.Tuple(elementPats = pats)
        | SynPat.ArrayOrList(elementPats = pats) -> List.iter walkPat pats
        | SynPat.Ands(pats = pats) -> List.iter walkPat pats
        | SynPat.As(lhsPat = a; rhsPat = b)
        | SynPat.Or(lhsPat = a; rhsPat = b) ->
            walkPat a
            walkPat b
        | SynPat.LongIdent(argPats = SynArgPats.Pats pats) -> List.iter walkPat pats
        | SynPat.Attrib(pat = p) -> walkPat p
        | SynPat.Record(fieldPats = fields) ->
            for fld in fields do
                walkPat fld.Pattern
        | _ -> ()

    let rec walkExpr (expr: SynExpr) =
        match expr with
        | SynExpr.AnonRecd(recordFields = fields; copyInfo = copyInfo; range = r) ->
            ranges.Add r

            match copyInfo with
            | Some(e, _) -> walkExpr e
            | None -> ()

            for (_, _, fieldExpr) in fields do
                walkExpr fieldExpr
        | SynExpr.Typed(expr = e; targetType = t) ->
            walkExpr e
            walkType t
        | SynExpr.Paren(expr = e) -> walkExpr e
        | SynExpr.App(funcExpr = f; argExpr = a) ->
            walkExpr f
            walkExpr a
        | SynExpr.Lambda(body = b) -> walkExpr b
        | SynExpr.Tuple(exprs = es)
        | SynExpr.ArrayOrList(exprs = es) -> List.iter walkExpr es
        | SynExpr.Sequential(expr1 = e1; expr2 = e2) ->
            walkExpr e1
            walkExpr e2
        | SynExpr.IfThenElse(ifExpr = c; thenExpr = t; elseExpr = e) ->
            walkExpr c
            walkExpr t
            Option.iter walkExpr e
        | SynExpr.LetOrUse(letOrUse) ->
            List.iter walkBinding letOrUse.Bindings
            walkExpr letOrUse.Body
        | SynExpr.Match(expr = e; clauses = clauses)
        | SynExpr.MatchBang(expr = e; clauses = clauses) ->
            walkExpr e
            List.iter walkClause clauses
        | SynExpr.MatchLambda(matchClauses = clauses) -> List.iter walkClause clauses
        | SynExpr.TryWith(tryExpr = t; withCases = cs) ->
            walkExpr t
            List.iter walkClause cs
        | SynExpr.TryFinally(tryExpr = t; finallyExpr = f) ->
            walkExpr t
            walkExpr f
        | SynExpr.Record(copyInfo = copyInfo; recordFields = fields) ->
            match copyInfo with
            | Some(e, _) -> walkExpr e
            | None -> ()

            for SynExprRecordField(expr = e) in fields do
                Option.iter walkExpr e
        | SynExpr.ArrayOrListComputed(expr = e)
        | SynExpr.ComputationExpr(expr = e)
        | SynExpr.Do(expr = e)
        | SynExpr.Assert(expr = e)
        | SynExpr.Lazy(expr = e)
        | SynExpr.DoBang(expr = e)
        | SynExpr.YieldOrReturn(expr = e)
        | SynExpr.YieldOrReturnFrom(expr = e)
        | SynExpr.AddressOf(expr = e)
        | SynExpr.InferredUpcast(expr = e)
        | SynExpr.InferredDowncast(expr = e)
        | SynExpr.New(expr = e) -> walkExpr e
        | SynExpr.Upcast(expr = e; targetType = t)
        | SynExpr.Downcast(expr = e; targetType = t)
        | SynExpr.TypeTest(expr = e; targetType = t) ->
            walkExpr e
            walkType t
        | SynExpr.TypeApp(expr = e; typeArgs = args) ->
            walkExpr e
            List.iter walkType args
        | SynExpr.While(whileExpr = c; doExpr = b) ->
            walkExpr c
            walkExpr b
        | SynExpr.For(identBody = a; toBody = b; doBody = c) ->
            walkExpr a
            walkExpr b
            walkExpr c
        | SynExpr.ForEach(enumExpr = e; bodyExpr = b) ->
            walkExpr e
            walkExpr b
        | SynExpr.Set(targetExpr = a; rhsExpr = b)
        | SynExpr.DotSet(targetExpr = a; rhsExpr = b)
        | SynExpr.NamedIndexedPropertySet(expr1 = a; expr2 = b)
        | SynExpr.JoinIn(lhsExpr = a; rhsExpr = b) ->
            walkExpr a
            walkExpr b
        | SynExpr.DotGet(expr = e) -> walkExpr e
        | SynExpr.DotIndexedGet(objectExpr = e; indexArgs = a) ->
            walkExpr e
            walkExpr a
        | SynExpr.DotIndexedSet(objectExpr = a; indexArgs = i; valueExpr = v) ->
            walkExpr a
            walkExpr i
            walkExpr v
        | SynExpr.LongIdentSet(expr = e) -> walkExpr e
        | SynExpr.ObjExpr(bindings = bindings; members = members) ->
            List.iter walkBinding bindings
            List.iter walkMemberDefn members
        | SynExpr.InterpolatedString(contents = parts) ->
            for part in parts do
                match part with
                | SynInterpolatedStringPart.FillExpr(fillExpr = e) -> walkExpr e
                | _ -> ()
        | _ -> ()

    and walkClause (SynMatchClause(whenExpr = whenExpr; resultExpr = body; pat = pat)) =
        walkPat pat
        Option.iter walkExpr whenExpr
        walkExpr body

    and walkBinding (SynBinding(headPat = pat; expr = e; returnInfo = returnInfo)) =
        walkPat pat
        walkExpr e

        match returnInfo with
        | Some(SynBindingReturnInfo(typeName = t)) -> walkType t
        | None -> ()

    and walkMemberDefn (memb: SynMemberDefn) =
        match memb with
        | SynMemberDefn.Member(memberDefn = b) -> walkBinding b
        | SynMemberDefn.LetBindings(bindings = bindings) -> List.iter walkBinding bindings
        | SynMemberDefn.AutoProperty(typeOpt = t; synExpr = e) ->
            Option.iter walkType t
            walkExpr e
        | SynMemberDefn.ValField(fieldInfo = SynField(fieldType = t)) -> walkType t
        | SynMemberDefn.GetSetMember(memberDefnForGet = g; memberDefnForSet = s) ->
            Option.iter walkBinding g
            Option.iter walkBinding s
        | _ -> ()

    let walkTypeDefn (SynTypeDefn(typeRepr = repr; members = members)) =
        match repr with
        | SynTypeDefnRepr.ObjectModel(members = objMembers) -> List.iter walkMemberDefn objMembers
        | SynTypeDefnRepr.Simple(simpleRepr = simple) ->
            match simple with
            | SynTypeDefnSimpleRepr.Record(recordFields = fields) ->
                for SynField(fieldType = t) in fields do
                    walkType t
            | SynTypeDefnSimpleRepr.TypeAbbrev(rhsType = t) -> walkType t
            | _ -> ()
        | _ -> ()

        List.iter walkMemberDefn members

    let rec walkDecl (decl: SynModuleDecl) =
        match decl with
        | SynModuleDecl.Let(bindings = bindings) -> List.iter walkBinding bindings
        | SynModuleDecl.Expr(expr = e) -> walkExpr e
        | SynModuleDecl.Types(typeDefns = typeDefns) -> List.iter walkTypeDefn typeDefns
        | SynModuleDecl.NestedModule(decls = decls) -> List.iter walkDecl decls
        | _ -> ()

    match input with
    | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
        for SynModuleOrNamespace(decls = decls) in modules do
            List.iter walkDecl decls
    | ParsedInput.SigFile _ -> ()

    // Distinct by range: a single annotation can be reached by more than one walk path
    // (e.g. a binding's return-type type appears in both the binding's `returnInfo` and
    // the desugared `SynExpr.Typed` body). Each source occurrence must yield exactly one
    // diagnostic, so collapse byte-identical ranges. Order is preserved for stable output.
    ranges |> Seq.distinct |> List.ofSeq

/// Build the diagnostic `Message` list from collected ranges. Severity is `Warning`:
/// the anonymous-record blind spot is an advisory precision hint, not a compile error —
/// the code is valid F# and the analyzer is opt-in, so it must never break a build.
let buildMessages (ranges: range list) : Message list =
    ranges
    |> List.map (fun r ->
        { Type = Name
          Message = Message
          Code = Code
          Severity = Severity.Warning
          Range = r
          Fixes = [] })

/// Analyzer entry point. Flags every anonymous-record expression and type annotation
/// in the analyzed file. Untyped-AST only — requires no type-check information.
[<CliAnalyzer(Name, "Flags anonymous-record usage invisible to TestPrune impact analysis")>]
let anonymousRecordAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async { return context.ParseFileResults.ParseTree |> collectAnonRecordRanges |> buildMessages }
