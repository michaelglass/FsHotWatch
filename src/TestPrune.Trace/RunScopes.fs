/// The scopes of one traced run, merged across its processes, and which static-init scopes
/// each test inherits.
///
/// A type initializer runs once per process, in whichever test first touches the type, so
/// the recorder files it under its own `S:<type>` scope rather than that test's. Every test
/// that touches the type depends on what the initializer did (the files it read, the code it
/// ran), so every such test inherits the scope, whichever test happened to trigger it.
module TestPrune.Trace.RunScopes

open System
open TestPrune.Trace.Model

/// The prefix of a static-init scope key: `S:<CLR type>`, or `S:static-init`.
[<Literal>]
let StaticInitPrefix = "S:"

/// The static-init scope of an initializer the recorder could not name.
[<Literal>]
let UnnamedStaticInit = "S:static-init"

/// Whether a scope key is a static-init scope.
let isStaticInit (key: string) =
    key.StartsWith(StaticInitPrefix, StringComparison.Ordinal)

/// Every scope of the run by key, merged across processes. A child process records into
/// the scope that started it, and so does its static init.
let merged (dumps: ProcessDump list) : Map<string, RecordedScope> =
    dumps
    |> List.collect (fun d ->
        d.Scopes
        |> List.map (fun s ->
            match d.ParentScope with
            | Some parent when isStaticInit s.Key -> { s with Key = parent }
            | _ -> s))
    |> List.groupBy (fun s -> s.Key)
    |> List.map (fun (key, group) ->
        key,
        { Key = key
          Test = group |> List.tryPick (fun s -> s.Test)
          Parents = group |> List.collect (fun s -> s.Parents) |> List.distinct
          Links = group |> List.collect (fun s -> s.Links) |> List.distinct
          Ids = group |> Seq.collect (fun s -> s.Ids) |> Seq.distinct |> Array.ofSeq
          Inputs = group |> List.collect (fun s -> s.Inputs) |> List.distinct
          Children = group |> List.collect (fun s -> s.Children) })
    |> Map.ofList

/// The module a `<StartupCode$Assembly>.$N.M` class initializes the values of: `N.M`.
let private startupModule (typeName: string) =
    if typeName.StartsWith("<StartupCode$", StringComparison.Ordinal) then
        match typeName.IndexOf(">.$", StringComparison.Ordinal) with
        | -1 -> None
        | i -> Some(typeName.Substring(i + 3))
    else
        None

/// A CLR type name's outermost type: a closure `N.M+f@12` belongs to `N.M`.
let private outermost (typeName: string) =
    match typeName.IndexOf '+' with
    | -1 -> typeName
    | i -> typeName.Substring(0, i)

/// What touching a type's initializer looks like: executing a probe on the type, or in a
/// source file its initializer's code is in. The file matters for F#: a module's values
/// initialize in a `<StartupCode$…>` class that no user code names, and the module's
/// members (in the same file) read them. Reading a module value from another file runs only
/// the value's getter, which has no sequence points and so no document; in an executable
/// the module has no type initializer of its own either. So a member with no document is
/// in the files of its type's other members, and every member of a module touches the
/// module's startup class.
let private touchKeysOf
    (typeDocuments: Map<string, string list>)
    (startupClasses: Map<string, string list>)
    (row: ManifestRow)
    =
    [ yield "type:" + row.TypeName
      match row.Document with
      | Some d -> yield "doc:" + d
      | None ->
          yield!
              typeDocuments.TryFind row.TypeName
              |> Option.defaultValue []
              |> List.map ((+) "doc:")
      yield!
          startupClasses.TryFind(outermost row.TypeName)
          |> Option.defaultValue []
          |> List.map ((+) "type:") ]

/// The static-init scopes a test inherits, given every scope it holds or inherits otherwise.
///
/// A named `S:<type>` scope is inherited when the test's scopes touched the type (see
/// `touchKeysOf`; the test's own class counts), then transitively: an inherited scope's own
/// probes can touch further initialized types, and an initializer that ran inside another
/// links to it. A scope whose type has no initializer row in the manifest cannot be placed,
/// and neither can `S:static-init`; every test inherits those.
let staticInheritance (manifest: Manifest) (scopes: Map<string, RecordedScope>) : Set<string> -> Set<string> =
    let typeDocuments =
        manifest.Rows
        |> Array.choose (fun r -> r.Document |> Option.map (fun d -> r.TypeName, d))
        |> Array.groupBy fst
        |> Array.map (fun (t, ds) -> t, ds |> Array.map snd |> Array.distinct |> List.ofArray)
        |> Map.ofArray

    let startupClasses =
        manifest.Rows
        |> Array.choose (fun r -> startupModule r.TypeName |> Option.map (fun m -> m, r.TypeName))
        |> Array.groupBy fst
        |> Array.map (fun (m, ts) -> m, ts |> Array.map snd |> Array.distinct |> List.ofArray)
        |> Map.ofArray

    let touchKeysOf = touchKeysOf typeDocuments startupClasses

    let initializers =
        manifest.Rows
        |> Array.filter (fun r -> r.Kind = StaticCtor)
        |> Array.groupBy (fun r -> r.TypeName)
        |> Map.ofArray

    let staticKeys = scopes |> Map.toList |> List.map fst |> List.filter isStaticInit

    // `S:static-init` names no type, so no initializer row matches it.
    let triggersOf (key: string) =
        initializers.TryFind(key.Substring StaticInitPrefix.Length)
        |> Option.map (fun ctors -> ctors |> Seq.collect touchKeysOf |> Seq.distinct |> List.ofSeq)

    let placed =
        staticKeys
        |> List.choose (fun k -> triggersOf k |> Option.map (fun ts -> k, ts))

    let everywhere =
        staticKeys |> List.filter (fun k -> not (List.exists (fst >> (=) k) placed))

    let byTrigger =
        placed
        |> List.collect (fun (k, ts) -> ts |> List.map (fun t -> t, k))
        |> List.groupBy fst
        |> List.map (fun (t, ks) -> t, List.map snd ks)
        |> Map.ofList

    let pullsOf (touches: string list) =
        touches |> List.collect (fun t -> byTrigger.TryFind t |> Option.defaultValue [])

    // Per probe id, once: the scopes it pulls in. Almost every id pulls none.
    let pullsById = Array.create manifest.IdCount []

    for r in manifest.Rows do
        pullsById.[r.Id] <- touchKeysOf r |> pullsOf |> List.distinct

    let pullsAt id =
        if uint32 id < uint32 pullsById.Length then
            pullsById.[id]
        else
            []

    // Per scope, once: fixture and class scopes are shared by many tests.
    let pulled = Collections.Generic.Dictionary<string, string list>()

    let pulledBy (key: string) =
        match pulled.TryGetValue key with
        | true, keys -> keys
        | _ ->
            let keys =
                match scopes.TryFind key with
                | None -> []
                | Some s ->
                    [ yield! s.Ids |> Seq.collect pullsAt
                      match s.Test with
                      | Some t -> yield! pullsOf [ "type:" + t.Class ]
                      | None -> ()
                      if isStaticInit key then
                          yield! s.Links |> List.filter isStaticInit ]
                    |> List.distinct

            pulled.[key] <- keys
            keys

    fun (held: Set<string>) ->
        let rec grow (acc: Set<string>) (frontier: string list) =
            match
                frontier
                |> List.collect pulledBy
                |> List.filter (acc.Contains >> not)
                |> List.distinct
            with
            | [] -> acc
            | fresh -> grow (Set.union acc (Set.ofList fresh)) fresh

        let start = Set.union held (Set.ofList everywhere)

        grow start (Set.toList start)
        |> Set.filter (fun k -> isStaticInit k && scopes.ContainsKey k)

/// Every scope a test with scopes `keys` holds or inherits: those keys, the fixture,
/// collection, assembly and pool scopes reachable through parents and links, and the
/// static-init scopes `staticInheritance` gives them. Only recorded scopes are returned;
/// ambient and static-init scopes are never reached through parents and links.
let inheritedBy (manifest: Manifest) (scopes: Map<string, RecordedScope>) : string list -> Set<string> =
    let statics = staticInheritance manifest scopes

    let rec held (seen: Set<string>) (keys: string list) =
        match keys with
        | [] -> seen
        | k :: rest when
            seen.Contains k
            || k = "A:ambient"
            || isStaticInit k
            || not (scopes.ContainsKey k)
            ->
            held seen rest
        | k :: rest -> held (seen.Add k) (scopes.[k].Parents @ scopes.[k].Links @ rest)

    fun keys ->
        let own = held Set.empty keys
        Set.union own (statics own)
