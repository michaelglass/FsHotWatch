namespace TestPrune.Falco

open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// Resolves plain STRING-route navigation through a NAMED URL CONSTANT — a test that calls
/// `navigateTo Routes.settingsUrl` where `let settingsUrl = "/settings"` lives in ANOTHER file —
/// to the same URL literal the matcher already keys on. The test's own span holds no "/settings"
/// substring, so a purely textual matcher drops it (under-selection) — the string-route analogue
/// of the `Route.link` symbolic-navigation hole `UnionRouteLinks` closes.
///
/// This is ADDITIVE, has NOTHING to do with Falco.UnionRoutes, and helps plain string-route repos.
/// The `constant → url` map is built from the untyped syntax tree (a top-level `let`/`let rec`
/// binding whose right-hand side is a string LITERAL starting with `/`) — no fragile regex over
/// the binding itself. A constant contributes ONLY when its literal value matches an AFFECTED
/// route URL, so `let apiBase = "/api"` fires only when `/api` is itself the changed route: no
/// blanket over-selection.
///
/// Scope: only DIRECT string literals are resolved. A URL built dynamically — interpolated
/// (`$"/users/{id}"`), concatenated (`apiBase + "/x"`), or computed — stays unmatched and falls
/// back to plain literal-URL matching.
module StringRouteConstants =

    let private checker = lazy (FSharpChecker.Create())

    let private lastIdent (lid: LongIdent) : string option =
        lid |> List.tryLast |> Option.map (fun i -> i.idText)

    /// The bound identifier of a value binding pattern (`let x` / `let x: string`).
    let rec private identOfPat (pat: SynPat) : string option =
        match pat with
        | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
        | SynPat.Typed(pat = inner) -> identOfPat inner
        | SynPat.Attrib(pat = inner) -> identOfPat inner
        | _ -> None

    /// The string value if the expression is (possibly typed/parenthesised) a string literal.
    let rec private stringLiteral (expr: SynExpr) : string option =
        match expr with
        | SynExpr.Const(SynConst.String(text = s), _) -> Some s
        | SynExpr.Typed(expr = inner) -> stringLiteral inner
        | SynExpr.Paren(expr = inner) -> stringLiteral inner
        | _ -> None

    /// Parse one source file → `(qualified-name, url)` for every top-level `let` bound to a URL
    /// string literal (value starts with `/`). Qualified name = enclosing module's last segment +
    /// "." + identifier (e.g. "Routes.settingsUrl"), matching how a test references it.
    let private parseConstants (fileName: string) (text: string) : (string * string) list =
        let opts =
            { FSharpParsingOptions.Default with
                SourceFiles = [| fileName |] }

        let parseResults =
            checker.Value.ParseFile(fileName, SourceText.ofString text, opts)
            |> Async.RunSynchronously

        let results = ResizeArray<string * string>()

        let handleBindings (moduleName: string) (bindings: SynBinding list) =
            for SynBinding(headPat = pat; expr = rhs) in bindings do
                match identOfPat pat, stringLiteral rhs with
                | Some ident, Some url when url.StartsWith "/" ->
                    let qualified = if moduleName = "" then ident else moduleName + "." + ident

                    results.Add(qualified, url)
                | _ -> ()

        let rec walkDecls (moduleName: string) (decls: SynModuleDecl list) =
            for decl in decls do
                match decl with
                | SynModuleDecl.Let(bindings = bindings) -> handleBindings moduleName bindings
                | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(longId = lid); decls = nested) ->
                    walkDecls (lastIdent lid |> Option.defaultValue moduleName) nested
                | _ -> ()

        match parseResults.ParseTree with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            for SynModuleOrNamespace(longId = lid; decls = decls) in modules do
                walkDecls (lastIdent lid |> Option.defaultValue "") decls
        | _ -> ()

        List.ofSeq results

    /// Build the `"Module.const" → { url literals }` map from in-memory `(fileName, source)` pairs
    /// whose raw text contains one of `affectedUrls`. The `Contains` pre-filter bounds parsing to
    /// files that mention a changed route — cheap, and it keeps precision (an app file defining
    /// unrelated URL constants is only parsed when it also mentions an affected route, and even
    /// then the constant contributes only if ITS value matches — see `constantReferenceRegexes`).
    let buildConstantMap (files: (string * string) list) (affectedUrls: Set<string>) : Map<string, Set<string>> =
        files
        |> List.filter (fun (_, text) -> affectedUrls |> Set.exists (fun u -> text.Contains u))
        |> List.collect (fun (path, text) ->
            try
                parseConstants path text
            with _ ->
                [])
        |> List.fold
            (fun acc (qualified, url) ->
                let urls =
                    match Map.tryFind qualified acc with
                    | Some existing -> Set.add url existing
                    | None -> Set.singleton url

                Map.add qualified urls acc)
            Map.empty

    /// Boundary-anchored regexes matching a reference to any URL constant whose literal value
    /// matches an affected route URL. The affected URLs are supplied as the SAME regexes the
    /// literal matcher builds (`urlPatternToRegex`), so param / constraint / query / `#` / trailing
    /// semantics are IDENTICAL to literal matching — a constant `/users/{id}` is affected exactly
    /// when the literal `/users/123` would be.
    ///
    /// Each in-scope constant emits a regex on its QUALIFIED reference (`Routes.settingsUrl`); the
    /// bare identifier (`settingsUrl`, for an opened module) is emitted ONLY when that identifier
    /// is unique across the map, so bare-name collisions never over-select.
    let constantReferenceRegexes (constantMap: Map<string, Set<string>>) (affectedUrlRegexes: Regex list) : Regex list =
        let bareOf (qualified: string) =
            match qualified.LastIndexOf('.') with
            | i when i >= 0 -> qualified.Substring(i + 1)
            | _ -> qualified

        let bareCounts =
            constantMap |> Map.toList |> List.countBy (fun (q, _) -> bareOf q) |> Map.ofList

        constantMap
        |> Map.toList
        |> List.collect (fun (qualified, urls) ->
            let isAffected =
                urls
                |> Set.exists (fun url -> affectedUrlRegexes |> List.exists (fun r -> r.IsMatch url))

            if not isAffected then
                []
            else
                let qualifiedRegex =
                    Regex(@"(?<![\w])" + Regex.Escape qualified + @"(?![\w])", RegexOptions.Compiled)

                let bare = bareOf qualified

                let bareRegexes =
                    if Map.tryFind bare bareCounts = Some 1 then
                        [ Regex(@"(?<![\w.])" + Regex.Escape bare + @"(?![\w])", RegexOptions.Compiled) ]
                    else
                        []

                qualifiedRegex :: bareRegexes)
