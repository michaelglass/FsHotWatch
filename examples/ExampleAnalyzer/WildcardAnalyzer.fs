/// Example analyzer: forbids catch-all wildcards on discriminated union match expressions.
/// Demonstrates how to write a custom analyzer for use with FsHotWatch.
module ExampleAnalyzer.WildcardAnalyzer

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

let rec private unwrapParen (pat: SynPat) =
    match pat with
    | SynPat.Paren(inner, _) -> unwrapParen inner
    | _ -> pat

let private isWildcard (pat: SynPat) =
    match unwrapParen pat with
    | SynPat.Wild _ -> true
    | _ -> false

let private isLongIdent (pat: SynPat) =
    match unwrapParen pat with
    | SynPat.LongIdent _ -> true
    | _ -> false

let private getClausePattern (SynMatchClause(pat = pat)) = pat
let private getClauseBody (SynMatchClause(resultExpr = body)) = body

let rec private walkExpr (ranges: ResizeArray<range>) (expr: SynExpr) =
    match expr with
    | SynExpr.Match(clauses = clauses)
    | SynExpr.MatchLambda(matchClauses = clauses)
    | SynExpr.MatchBang(clauses = clauses) ->
        let hasCase = clauses |> List.exists (fun c -> isLongIdent (getClausePattern c))

        if hasCase then
            for c in clauses do
                let pat = getClausePattern c

                if isWildcard pat then
                    match unwrapParen pat with
                    | SynPat.Wild range -> ranges.Add(range)
                    | _ -> ()

        for clause in clauses do
            walkExpr ranges (getClauseBody clause)
    | SynExpr.LetOrUse(letOrUse) ->
        for SynBinding(expr = e) in letOrUse.Bindings do
            walkExpr ranges e

        walkExpr ranges letOrUse.Body
    | SynExpr.App(funcExpr = f; argExpr = a) ->
        walkExpr ranges f
        walkExpr ranges a
    | SynExpr.IfThenElse(ifExpr = c; thenExpr = t; elseExpr = e) ->
        walkExpr ranges c
        walkExpr ranges t

        match e with
        | Some e -> walkExpr ranges e
        | None -> ()
    | SynExpr.Sequential(expr1 = e1; expr2 = e2) ->
        walkExpr ranges e1
        walkExpr ranges e2
    | SynExpr.Paren(expr = e) -> walkExpr ranges e
    | SynExpr.Lambda(body = b) -> walkExpr ranges b
    | SynExpr.TryWith(tryExpr = t; withCases = cs) ->
        walkExpr ranges t

        for c in cs do
            walkExpr ranges (getClauseBody c)
    | _ -> ()

[<CliAnalyzer("WildcardOnDUAnalyzer", "Forbids catch-all wildcards on DU match expressions")>]
let wildcardAnalyzer: Analyzer<CliContext> =
    fun (context: CliContext) ->
        async {
            let ranges = ResizeArray<range>()

            match context.ParseFileResults.ParseTree with
            | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
                for SynModuleOrNamespace(decls = decls) in modules do
                    for decl in decls do
                        match decl with
                        | SynModuleDecl.Let(bindings = bindings) ->
                            for SynBinding(expr = e) in bindings do
                                walkExpr ranges e
                        | SynModuleDecl.Expr(expr = e) -> walkExpr ranges e
                        | _ -> ()
            | _ -> ()

            return
                ranges
                |> Seq.toList
                |> List.map (fun range ->
                    { Type = "Wildcard on DU"
                      Message =
                        "Catch-all wildcard on DU hides exhaustiveness checking. List all cases explicitly."
                      Code = "EXAMPLE-WILDCARD-001"
                      Severity = Severity.Warning
                      Range = range
                      Fixes = [] })
        }
