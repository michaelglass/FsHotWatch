namespace TestPrune.Falco

open System.IO
open System.Text
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open TestPrune

/// Resolves Falco.UnionRoutes SYMBOLIC navigation — `Route.link (Route.Admin(_, AdminPages.Settings))`
/// — to the very same URL patterns the literal-string matcher already keys on, by deriving a
/// route-case → URL map from the route DU's `[<Route(Path = "…")>]` attributes.
///
/// This support is ADDITIVE and Falco.UnionRoutes-specific. A codebase that writes plain string
/// routes (`mapGet "/admin/settings"`) has no such DU: the scan finds nothing, the map is empty,
/// and NOTHING changes — the core matcher stays UnionRoutes-agnostic and no consumer is coupled to
/// Falco.UnionRoutes. No RUNTIME dependency on the package is taken either: the composition rule
/// (nested `Path` segments concatenate up the DU; an absent/`""` path falls back to the RESTful
/// empty-segment convention or a kebab-cased case name) is re-derived by reading the DU source,
/// mirroring Falco.UnionRoutes' own `Route.info`/`extractRouteInfo` reflection.
///
/// Scope (this pass): the explicit-`Path` concatenation pattern plus the empty-segment/kebab
/// conventions and single simple route params. Routes whose segments depend on richer field-based
/// inference are best-effort — a miss keeps the pre-existing (literal-URL-only) behaviour, never
/// a regression.
module UnionRouteLinks =

    // Field marker types that carry NO URL segment (auth / query / body / response markers).
    // Mirrors Falco.UnionRoutes' isPrecondition/isQueryParam/... classification.
    let private markerTypeNames =
        set
            [ "PreCondition"
              "OverridablePreCondition"
              "QueryParam"
              "JsonBody"
              "FormBody"
              "Returns" ]

    // Case names that contribute NO path segment — Falco.UnionRoutes' RESTful convention
    // (see its `EmptySegmentName` active pattern).
    let private emptySegmentNames =
        set [ "Root"; "List"; "Show"; "Member"; "Create"; "Delete"; "Patch" ]

    let private kebabCaseRegex =
        Regex(@"([a-z])([A-Z])|([A-Z]+)([A-Z][a-z])", RegexOptions.Compiled)

    /// PascalCase → kebab-case, matching Falco.UnionRoutes' `toKebabCase`.
    let private toKebabCase (s: string) : string =
        kebabCaseRegex
            .Replace(
                s,
                fun m ->
                    if m.Groups.[1].Success then
                        $"%s{m.Groups.[1].Value}-%s{m.Groups.[2].Value}"
                    else
                        $"%s{m.Groups.[3].Value}-%s{m.Groups.[4].Value}"
            )
            .ToLowerInvariant()

    type private ParsedField =
        { Name: string option
          TypeHead: string option }

    type private ParsedCase =
        { Name: string
          ExplicitPath: string option
          HasRouteAttr: bool
          Fields: ParsedField list }

    type private ParsedUnion =
        { TypeName: string
          Cases: ParsedCase list }

    // The `Path = "…"` named argument of a `[<Route(…)>]` attribute. Read from the attribute's
    // source text: the value is a NAMED argument, which the untyped syntax tree does not surface
    // as cleanly as a string literal in the attribute's range.
    let private pathAttrRegex =
        Regex("Path\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled)

    let private checker = lazy (FSharpChecker.Create())

    let private lastIdent (lid: LongIdent) : string option =
        lid |> List.tryLast |> Option.map (fun i -> i.idText)

    /// Head type name of a field's declared type (`PreCondition<UserId>` → "PreCondition",
    /// `AdminPages` → "AdminPages"), used to classify markers and nested route unions.
    let rec private synTypeHead (t: SynType) : string option =
        match t with
        | SynType.App(typeName = inner) -> synTypeHead inner
        | SynType.Paren(innerType = inner) -> synTypeHead inner
        | SynType.LongIdent(SynLongIdent(id = lid)) -> lastIdent lid
        | _ -> None

    /// The source substring covered by a range (attributes span one line in practice, but
    /// multi-line is handled defensively).
    let private textOfRange (lines: string[]) (r: range) : string =
        try
            if r.StartLine = r.EndLine then
                let line = lines.[r.StartLine - 1]
                let start = min r.StartColumn line.Length
                let len = min (r.EndColumn - r.StartColumn) (line.Length - start)
                line.Substring(start, max 0 len)
            else
                let sb = StringBuilder()

                for ln in r.StartLine .. r.EndLine do
                    let line = lines.[ln - 1]

                    if ln = r.StartLine then
                        sb.Append(line.Substring(min r.StartColumn line.Length)) |> ignore
                    elif ln = r.EndLine then
                        sb.Append(line.Substring(0, min r.EndColumn line.Length)) |> ignore
                    else
                        sb.Append(line) |> ignore

                    sb.Append(' ') |> ignore

                sb.ToString()
        with _ ->
            ""

    let private parseSource (fileName: string) (text: string) : ParsedUnion list =
        let opts =
            { FSharpParsingOptions.Default with
                SourceFiles = [| fileName |] }

        let parseResults =
            checker.Value.ParseFile(fileName, SourceText.ofString text, opts)
            |> Async.RunSynchronously

        let lines = text.Replace("\r\n", "\n").Split('\n')
        let unions = ResizeArray<ParsedUnion>()

        let parseCase (SynUnionCase(attributes = attrs; ident = SynIdent(cid, _); caseType = caseType)) : ParsedCase =
            let routeAttrs =
                attrs
                |> List.collect (fun (al: SynAttributeList) -> al.Attributes)
                |> List.filter (fun a ->
                    match lastIdent a.TypeName.LongIdent with
                    | Some n -> n = "Route" || n = "RouteAttribute"
                    | None -> false)

            let explicitPath =
                routeAttrs
                |> List.tryPick (fun a ->
                    let m = pathAttrRegex.Match(textOfRange lines a.Range)
                    if m.Success then Some m.Groups.[1].Value else None)

            let fields =
                match caseType with
                | SynUnionCaseKind.Fields fs ->
                    fs
                    |> List.map (fun (SynField(idOpt = idOpt; fieldType = ft)) ->
                        { Name = idOpt |> Option.map (fun i -> i.idText)
                          TypeHead = synTypeHead ft })
                | _ -> []

            { Name = cid.idText
              ExplicitPath = explicitPath
              HasRouteAttr = not routeAttrs.IsEmpty
              Fields = fields }

        let rec walkDecls (decls: SynModuleDecl list) =
            for decl in decls do
                match decl with
                | SynModuleDecl.Types(typeDefns, _) ->
                    for SynTypeDefn(typeInfo = SynComponentInfo(longId = ci); typeRepr = repr) in typeDefns do
                        match repr with
                        | SynTypeDefnRepr.Simple(SynTypeDefnSimpleRepr.Union(_, cases, _), _) ->
                            match lastIdent ci with
                            | Some typeName ->
                                unions.Add
                                    { TypeName = typeName
                                      Cases = cases |> List.map parseCase }
                            | None -> ()
                        | _ -> ()
                | SynModuleDecl.NestedModule(decls = nested) -> walkDecls nested
                | _ -> ()

        match parseResults.ParseTree with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            for SynModuleOrNamespace(decls = decls) in modules do
                walkDecls decls
        | _ -> ()

        List.ofSeq unions

    /// Compose the `"Type.Case" → { url patterns }` map for a set of parsed unions.
    let private composeLinks (unions: ParsedUnion list) : Map<string, Set<string>> =
        let byName = unions |> List.map (fun u -> u.TypeName, u.Cases) |> Map.ofList
        let allNames = unions |> List.map (fun u -> u.TypeName) |> Set.ofList

        let isNestedUnionName (name: string) =
            allNames.Contains name && not (markerTypeNames.Contains name)

        // Candidate route unions: those with a `[<Route>]`-attributed case, plus (transitively)
        // any union they nest as a field. Markers are never candidates.
        let nestedNamesOf (cases: ParsedCase list) =
            cases
            |> List.collect (fun c -> c.Fields |> List.choose (fun f -> f.TypeHead))
            |> List.filter isNestedUnionName
            |> Set.ofList

        let seeded =
            unions
            |> List.filter (fun u -> u.Cases |> List.exists (fun c -> c.HasRouteAttr))
            |> List.map (fun u -> u.TypeName)
            |> Set.ofList

        let rec expand (acc: Set<string>) =
            let next =
                acc
                |> Set.toList
                |> List.collect (fun n ->
                    match Map.tryFind n byName with
                    | Some cs -> Set.toList (nestedNamesOf cs)
                    | None -> [])
                |> Set.ofList

            let merged = Set.union acc next
            if merged = acc then acc else expand merged

        let candidates = expand seeded

        let referencedAsNested =
            candidates
            |> Set.toList
            |> List.collect (fun n ->
                match Map.tryFind n byName with
                | Some cs -> Set.toList (nestedNamesOf cs |> Set.filter candidates.Contains)
                | None -> [])
            |> Set.ofList

        let roots = candidates |> Set.filter (fun n -> not (referencedAsNested.Contains n))

        // Segment contributed by one case, mirroring Falco.UnionRoutes' `getPathSegment`.
        let segmentFor (case: ParsedCase) : string =
            match case.ExplicitPath with
            | Some p -> p
            | None ->
                let nonMarker =
                    case.Fields
                    |> List.filter (fun f ->
                        match f.TypeHead with
                        | Some h -> not (markerTypeNames.Contains h)
                        | None -> true)

                let paramFields =
                    nonMarker
                    |> List.filter (fun f ->
                        match f.TypeHead with
                        | Some h -> not (isNestedUnionName h)
                        | None -> true)

                if paramFields.IsEmpty then
                    if emptySegmentNames.Contains case.Name then
                        ""
                    else
                        toKebabCase case.Name
                else
                    let paramPath =
                        paramFields
                        |> List.map (fun f -> "{" + (f.Name |> Option.defaultValue "param") + "}")
                        |> String.concat "/"

                    if emptySegmentNames.Contains case.Name then
                        paramPath
                    else
                        toKebabCase case.Name + "/" + paramPath

        let results = System.Collections.Generic.Dictionary<string, Set<string>>()

        let addLeaf (qualified: string) (url: string) =
            match results.TryGetValue qualified with
            | true, existing -> results.[qualified] <- Set.add url existing
            | _ -> results.[qualified] <- Set.singleton url

        // Guard against a pathological self-referential DU.
        let rec walk (visiting: Set<string>) (typeName: string) (prefix: string list) =
            if visiting.Contains typeName then
                ()
            else
                match Map.tryFind typeName byName with
                | None -> ()
                | Some cases ->
                    for case in cases do
                        let segment = segmentFor case
                        let segments = if segment = "" then prefix else prefix @ [ segment ]

                        let nestedField =
                            case.Fields
                            |> List.tryPick (fun f ->
                                match f.TypeHead with
                                | Some h when isNestedUnionName h -> Some h
                                | _ -> None)

                        match nestedField with
                        | Some childType -> walk (Set.add typeName visiting) childType segments
                        | None ->
                            let url =
                                if segments.IsEmpty then
                                    "/"
                                else
                                    "/" + String.concat "/" segments

                            addLeaf (typeName + "." + case.Name) url

        for root in roots do
            walk Set.empty root []

        results |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// Build the `"Type.Case" → { url patterns }` map from in-memory `(fileName, source)` pairs.
    /// Files that fail to parse are skipped rather than aborting the whole map.
    let buildLinkMap (sources: (string * string) list) : Map<string, Set<string>> =
        sources
        |> List.collect (fun (path, text) ->
            try
                parseSource path text
            with _ ->
                [])
        |> composeLinks

    // A cheap textual pre-filter so only files that actually declare route cases are parsed.
    let private routeAttrHint = "[<Route("

    /// Discover route-DU source files under `repoRoot` and build the case → URL map.
    /// Returns an empty map for a repo with no Falco.UnionRoutes route DU.
    let buildLinkMapFromRepo (repoRoot: string) : Map<string, Set<string>> =
        if not (Directory.Exists repoRoot) then
            Map.empty
        else
            SafeWalk.enumerateFiles "*.fs" repoRoot
            |> Seq.choose (fun path ->
                try
                    let text = File.ReadAllText path

                    if text.Contains routeAttrHint then
                        Some(path, text)
                    else
                        None
                with _ ->
                    None)
            |> List.ofSeq
            |> buildLinkMap

    /// Normalise a route constraint away: `{id:guid}` → `{id}`, leaving the parameter NAME. The
    /// source-derived composition cannot know a wrapped id type resolves to `:guid`/`:int`, but the
    /// host's route table (seeded from `Route.info`) carries the constraint. Comparing both sides
    /// constraint-insensitively lets an AST-composed `/admin/{id}` match a table `/admin/{id:guid}`.
    let private stripConstraints (url: string) : string =
        Regex.Replace(url, @"\{([^:}]+)(:[^}]*)?\}", "{$1}")

    /// Boundary-anchored regexes matching a QUALIFIED reference (`AdminPages.Settings`) to any
    /// route case whose composed URL pattern matches one of `targetUrls`. Appended to the URL-regex
    /// list so a symbolic navigation is attributed to the declaration whose span references it,
    /// exactly like a literal URL. Empty when no route DU is present, so string-route matching is
    /// unaffected.
    ///
    /// Matching is constraint-INSENSITIVE (`stripConstraints` on both sides): a strict SUPERSET of
    /// exact matching — it only ever adds matches, so it cannot under-select — that closes the gap
    /// where the AST composes an unconstrained `{id}` and the route table carries `{id:guid}`. The
    /// only cost is over-selecting two routes that share a path + parameter name but differ ONLY by
    /// constraint (e.g. `/x/{id:guid}` vs `/x/{id:int}`); such collisions are rare and merely widen
    /// the selected set.
    let leafReferenceRegexes (linkMap: Map<string, Set<string>>) (targetUrls: Set<string>) : Regex list =
        let normalizedTargets = targetUrls |> Set.map stripConstraints

        linkMap
        |> Map.toList
        |> List.choose (fun (leaf, urls) ->
            if
                urls
                |> Set.exists (fun url -> Set.contains (stripConstraints url) normalizedTargets)
            then
                Some(Regex(@"(?<![\w])" + Regex.Escape leaf + @"(?![\w])", RegexOptions.Compiled))
            else
                None)
