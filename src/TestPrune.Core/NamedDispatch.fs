/// Named dispatch: coupling a test to a handler it reaches only through a STRING NAME.
///
/// A job runner, a message bus, a command table: a handler is registered under a name,
/// and callers — tests especially — reach it by dispatching that name (`POST
/// /admin/jobs/run/Purge`, `publish "order-shipped"`). Neither side names the other, so
/// the AST graph has no path between them. When the registry is also a composition root
/// (`[<CompositionRoot>]`), the reverse walk from an edited handler stops at the registry
/// by design, and the one test that genuinely exercises the handler is never selected.
///
/// This is the named analogue of TestPrune.Falco's route edges, and it declares both
/// sides IN THE TREE, by attribute name, so the edges are a function of the indexed
/// source and nothing else:
///
/// * REGISTRATION — `[<DispatchedAs("job", "Purge")>]` on the handler function. The
///   channel ("job") scopes the name, so a job and a queue topic may share one.
/// * DISPATCH SHAPE — `[<DispatchTemplate("job", "/admin/jobs/{action}/{name}")>]` on any
///   symbol, conventionally the registry. `{name}` marks where the registered name sits;
///   any other `{placeholder}` matches one path-like segment.
///
/// A template is searched for in every indexed source file that declares a test method.
/// Each match whose captured name is registered on the template's channel yields an edge
/// from the symbol enclosing the match to that name's handler. Unregistered names
/// (`/admin/jobs/run/NotAJob`, a negative test) yield nothing.
///
/// Like the other marker attributes, both are matched by NAME (`DispatchedAs` /
/// `DispatchedAsAttribute`, `DispatchTemplate` / `DispatchTemplateAttribute`), so a
/// consumer may declare the two-line types itself instead of referencing
/// TestPrune.Attributes.
module TestPrune.NamedDispatch

open System
open System.IO
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports

/// A handler registered under `Name` on `Channel`.
type Registration =
    { Channel: string
      Name: string
      Handler: string }

/// The textual shape a dispatch of `Channel` takes at a call site.
type Template = { Channel: string; Template: string }

let private registrationNames = set [ "DispatchedAs"; "DispatchedAsAttribute" ]

let private templateNames = set [ "DispatchTemplate"; "DispatchTemplateAttribute" ]

/// The string constructor arguments of a stored attribute, in order. Stored args are
/// rendered as `["a", "b"]` (see `AstAnalyzer`); a quote inside an argument is not
/// escaped there, so arguments must not contain one — channel names, dispatch names and
/// templates have no reason to.
let private stringArgs (argsJson: string) : string list =
    Regex.Matches(argsJson, "\"([^\"]*)\"")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> List.ofSeq

/// Every `(symbol, args)` for attributes whose name is in `names`, in a stable order.
let private attributeArgs (store: SymbolStore) (names: Set<string>) =
    store.GetAllAttributes()
    |> Map.toList
    |> List.collect (fun (symbol, attributes) ->
        attributes
        |> List.choose (fun (name, argsJson) ->
            if names.Contains name then
                Some(symbol, stringArgs argsJson)
            else
                None))
    |> List.sort

/// Every `[<DispatchedAs(channel, name)>]` in the store.
let registrations (store: SymbolStore) : Registration list =
    attributeArgs store registrationNames
    |> List.choose (fun (symbol, args) ->
        match args with
        | channel :: name :: _ ->
            Some
                { Channel = channel
                  Name = name
                  Handler = symbol }
        | _ -> None)

/// Every `[<DispatchTemplate(channel, template)>]` in the store.
let templates (store: SymbolStore) : Template list =
    attributeArgs store templateNames
    |> List.choose (fun (_, args) ->
        match args with
        | channel :: template :: _ ->
            Some
                { Channel = channel
                  Template = template }
        | _ -> None)
    |> List.distinct

let private placeholder = Regex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")

/// The regex a template compiles to. `{name}` becomes the named group `name`, which
/// captures a WHOLE name token (`[\w.-]+`), so `/run/PurgeAll` yields `PurgeAll` and never
/// `Purge`; the captured token is then looked up exactly. Every other `{placeholder}`
/// matches one segment. Literal text is matched verbatim.
///
/// A template with no `{name}` cannot say which handler it reaches, and silently
/// matching nothing would look exactly like "no test dispatches this", so it is refused.
let templateRegex (template: string) : Result<Regex, string> =
    let placeholders =
        placeholder.Matches template |> Seq.map _.Groups[1].Value |> List.ofSeq

    match placeholders |> List.filter ((=) "name") |> List.length with
    | 1 ->
        let pattern =
            placeholder.Split template
            |> Array.mapi (fun i part ->
                // Split with one capture group alternates literal text and group names.
                if i % 2 = 0 then Regex.Escape part
                elif part = "name" then @"(?<name>[\w.-]+)"
                else @"[^/""'?#&\s]+")
            |> String.concat ""

        Ok(Regex(pattern, RegexOptions.CultureInvariant))
    | 0 -> Error $"dispatch template '%s{template}' has no {{name}} placeholder"
    | _ -> Error $"dispatch template '%s{template}' has more than one {{name}} placeholder"

/// The tracked symbols of one file that enclose `line`, found through the parse tree
/// (stored symbol ranges cover only the identifier, never the body). The innermost
/// binding or type containing the line wins; it is resolved to the stored symbol of the
/// same short name whose identifier lies inside that range. `None` when the file does
/// not parse.
let private enclosingSymbols
    (checker: FSharpChecker)
    (path: string)
    (text: string)
    (fileSymbols: SymbolInfo list)
    : (int -> SymbolInfo list) option =
    let options =
        { FSharpParsingOptions.Default with
            SourceFiles = [| path |] }

    let parsed =
        checker.ParseFile(path, SourceText.ofString text, options)
        |> Async.RunSynchronously

    if parsed.ParseHadErrors then
        None
    else
        let tree = parsed.ParseTree

        let ranges =
            collectModuleBindingRanges tree
            @ collectTypeMemberRanges tree
            @ collectTypeDefnRanges tree

        Some(fun line ->
            ranges
            |> List.filter (fun (_, r: range) -> r.StartLine <= line && line <= r.EndLine)
            |> List.sortBy (fun (_, r) -> r.EndLine - r.StartLine, r.StartLine)
            |> List.tryPick (fun (name, r) ->
                let lastSegment = canonicalShortName name

                match
                    fileSymbols
                    |> List.filter (fun s ->
                        canonicalShortName s.FullName = lastSegment
                        && r.StartLine <= s.LineStart
                        && s.LineStart <= r.EndLine)
                with
                | [] -> None
                | found -> Some found)
            |> Option.defaultValue [])

/// All named-dispatch edges the store's tree implies. Reads only the store and the
/// source files it indexes — never a change set — so two index builds of one tree give
/// the same edges.
let buildEdges (checker: FSharpChecker) (store: SymbolStore) (repoRoot: string) : Dependency list =
    let registered =
        registrations store |> List.groupBy (fun r -> r.Channel, r.Name) |> Map.ofList

    let compiled =
        templates store
        |> List.map (fun t ->
            match templateRegex t.Template with
            | Ok regex -> t.Channel, regex
            | Error message -> invalidArg "template" message)

    if registered.IsEmpty || compiled.IsEmpty then
        []
    else
        let testNames = store.GetTestMethodSymbolNames()

        let testFiles =
            store.GetAllSymbols()
            |> List.filter (fun s -> testNames.Contains s.FullName)
            |> List.map _.SourceFile
            |> List.distinct
            |> List.sort

        testFiles
        |> List.collect (fun file ->
            let path = Path.Combine(repoRoot, file)

            let text =
                try
                    Some(File.ReadAllText path)
                with _ ->
                    None

            match text with
            | None -> []
            | Some text ->
                let hits =
                    compiled
                    |> List.collect (fun (channel, regex) ->
                        regex.Matches text
                        |> Seq.choose (fun m ->
                            registered
                            |> Map.tryFind (channel, m.Groups["name"].Value)
                            |> Option.map (fun handlers -> m.Index, handlers))
                        |> List.ofSeq)

                if hits.IsEmpty then
                    []
                else
                    // A match outside every tracked binding (a file header), or a file the
                    // parser rejects, cannot be attributed; every test in the file is then
                    // a dispatcher. Coarse, but a superset is safe where a gap is not.
                    let wholeFile () =
                        store.GetTestMethodsInFile file |> List.map _.SymbolFullName |> List.distinct

                    let enclosing = enclosingSymbols checker path text (store.GetSymbolsInFile file)

                    hits
                    |> List.collect (fun (index, handlers) ->
                        let line = 1 + text.AsSpan(0, index).Count('\n')

                        let dispatchers =
                            match enclosing |> Option.map (fun find -> find line) with
                            | Some(found: SymbolInfo list) when not found.IsEmpty -> found |> List.map _.FullName
                            | _ -> wholeFile ()

                        dispatchers
                        |> List.collect (fun dispatcher ->
                            handlers
                            |> List.choose (fun handler ->
                                if dispatcher = handler.Handler then
                                    None
                                else
                                    Some
                                        { FromSymbol = dispatcher
                                          ToSymbol = handler.Handler
                                          Kind = SharedState
                                          Source = "named-dispatch" }))))
        |> List.distinct
        |> List.sort

/// The extension a host registers. Stateless between calls: every call recomputes the
/// edges from the store and the files it indexes, and the change set is ignored, because
/// an edge that exists only in the flush where one side changed disappears the next time
/// the other side is re-indexed.
type NamedDispatchExtension(checker: FSharpChecker) =

    new() = NamedDispatchExtension(FSharpChecker.Create())

    interface ITestPruneExtension with
        member _.Name = "Named Dispatch"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (repoRoot: string) =
            buildEdges checker symbolStore repoRoot
