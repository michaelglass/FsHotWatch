/// Cache-key inputs that live OUTSIDE the file a plugin is judging, but decide its verdict.
///
/// A per-file key that names only the file's own bytes replays across two kinds of change
/// it cannot see: configuration a tool discovers by walking up from the file
/// (`.editorconfig`, `fsharplint.json`), and the shape of what the file's type check
/// resolved in other files. Both are gathered here without absolute paths, so two
/// checkouts of the same repository still share an entry.
module FsHotWatch.CacheInputs

open System
open System.Collections.Generic
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FsHotWatch.Events

/// Every `configFileName` a walk-up discovery could find for `files`: one per directory
/// from the repository root down to each file's directory, as (label, content) pairs for
/// a cache key. `label` prefixes the repo-relative path, so the same file produces the
/// same input in every checkout of the repository.
///
/// A tool setting is an input to the verdict exactly as the source bytes are; a key that
/// omitted it would replay the old verdict across a config edit.
let configChainInputs
    (repoRoot: string)
    (configFileName: string)
    (label: string)
    (files: string list)
    : (string * string) list =
    let root = Path.GetFullPath repoRoot

    let dirsOf (file: string) =
        let rec up (dir: DirectoryInfo) acc =
            if isNull dir then
                acc
            else
                let acc = dir.FullName :: acc

                if String.Equals(dir.FullName, root, StringComparison.Ordinal) then
                    acc
                else
                    up dir.Parent acc

        up (DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath file))) []

    files
    |> List.collect dirsOf
    |> List.distinct
    |> List.choose (fun dir ->
        let candidate = Path.Combine(dir, configFileName)

        // A config that exists but cannot be read THROWS: keying it as absent would
        // name the verdict as if no config applied.
        if File.Exists candidate then
            let key = CachePathIdentity.keyOf (Some root) candidate
            Some($"%s{label}:%s{key}", File.ReadAllText candidate)
        else
            None)

/// The `.editorconfig` files that apply to `files`, labelled `editorconfig:<path>`.
let editorConfigInputs (repoRoot: string) (files: string list) : (string * string) list =
    configChainInputs repoRoot ".editorconfig" "editorconfig" files

/// Whether an assembly's shape comes from sources in this repository: the project being
/// checked (no file yet), a referenced F# project (transitively), or an output under the
/// repository root. Anything else — NuGet, the SDK — is identified by its qualified name,
/// which carries its version.
let private inRepository (repoRoot: string option) (options: FSharpProjectOptions) : FSharpAssembly -> bool =
    let projectNames = HashSet<string>(StringComparer.Ordinal)

    let rec addReferences (opts: FSharpProjectOptions) =
        opts.ReferencedProjects
        |> Array.iter (fun reference ->
            match reference with
            | FSharpReferencedProject.FSharpReference(output, referenced) when
                projectNames.Add(Path.GetFileNameWithoutExtension output)
                ->
                addReferences referenced
            | other -> projectNames.Add(Path.GetFileNameWithoutExtension other.OutputFile) |> ignore)

    addReferences options

    fun assembly ->
        match assembly.FileName with
        | None -> true
        | Some file ->
            projectNames.Contains assembly.SimpleName
            || (repoRoot.IsSome && CheckCache.isUnderRoot repoRoot file)

// F# declares nothing protected; a protected .NET member reads as private here.
let private accessText (access: FSharpAccessibility) =
    if access.IsPublic then "public"
    elif access.IsInternal then "internal"
    else "private"

let private entityName (entity: FSharpEntity) =
    entity.TryFullName |> Option.defaultValue entity.DisplayName

/// The text `usedSignaturesHash` hashes: one line per external symbol and one block per
/// repository entity, sorted, so the order FCS reports uses in cannot move it.
let usedShapes (repoRoot: string option) (options: FSharpProjectOptions) (results: FSharpCheckFileResults) =
    let inRepo = inRepository repoRoot options
    let lines = HashSet<string>(StringComparer.Ordinal)
    let visited = HashSet<string>(StringComparer.Ordinal)
    let pending = Queue<FSharpEntity>()

    let add (line: string) = lines.Add line |> ignore

    let visit (entity: FSharpEntity) =
        let name = entityName entity
        let assembly = entity.Assembly

        if visited.Add $"%s{assembly.SimpleName}/%s{name}" then
            if inRepo assembly then
                pending.Enqueue entity
            else
                add $"ext %s{assembly.QualifiedName} %s{name}"

    let attributesText (attributes: IList<FSharpAttribute>) =
        if attributes.Count = 0 then
            ""
        else
            attributes
            |> Seq.map (fun a -> a.Format FSharpDisplayContext.Empty)
            |> Seq.sort
            |> String.concat ", "

    // A type with arguments FCS will enumerate. A generic function's type (an active
    // pattern's, say: `'K 'V. KeyValuePair<'K,'V> -> 'K * 'V`) is none of these, and
    // `GenericArguments` throws on it.
    let hasArguments (t: FSharpType) =
        t.HasTypeDefinition || t.IsFunctionType || t.IsTupleType || t.IsAnonRecordType

    // Queue every type `t` names, so a type reached only through it (an inferred value's
    // record, a field's union) is rendered too.
    let rec follow (t: FSharpType) =
        if hasArguments t then
            if t.HasTypeDefinition then
                visit t.TypeDefinition

            t.GenericArguments |> Seq.iter follow

    let rec typeText (t: FSharpType) : string =
        let args (separator: string) =
            t.GenericArguments |> Seq.map typeText |> String.concat separator

        if t.IsGenericParameter then
            "'" + t.GenericParameter.Name
        elif t.IsFunctionType then
            "(" + args " -> " + ")"
        elif t.IsTupleType then
            (if t.IsStructTupleType then "struct (" else "(") + args " * " + ")"
        elif t.IsAnonRecordType then
            Seq.zip t.AnonRecordTypeDetails.SortedFieldNames t.GenericArguments
            |> Seq.map (fun (name, arg) -> $"%s{name}: %s{typeText arg}")
            |> String.concat "; "
            |> sprintf "{| %s |}"
        elif t.HasTypeDefinition then
            // Measures included: `m/s` is `MeasureProduct<m, MeasureInverse<s>>`.
            entityName t.TypeDefinition + "<" + args ", " + ">"
        else
            t.Format FSharpDisplayContext.Empty

    let literalText (value: obj option) =
        value |> Option.map (sprintf " = %A") |> Option.defaultValue ""

    /// The names whose condition holds, space-separated.
    let named (conditions: (bool * string) list) =
        conditions
        |> List.choose (fun (holds, name) -> if holds then Some name else None)
        |> String.concat " "

    let memberText (m: FSharpMemberOrFunctionOrValue) =
        let parameters =
            m.CurriedParameterGroups
            |> Seq.map (Seq.map (fun p -> defaultArg p.Name "_") >> String.concat ", ")
            |> String.concat " | "

        let flags = named [ m.IsInstanceMember, "instance"; m.IsMutable, "mutable" ]
        follow m.FullType

        $"%s{m.CompiledName}/%s{m.DisplayName} [%s{flags}] %s{accessText m.Accessibility} (%s{parameters}) : %s{typeText m.FullType}%s{literalText m.LiteralValue} [%s{attributesText m.Attributes}]"

    let fieldText (f: FSharpField) =
        let flags = named [ f.IsStatic, "static"; f.IsMutable, "mutable" ]

        $"%s{f.Name} [%s{flags}] %s{accessText f.Accessibility} : %s{typeText f.FieldType}%s{literalText f.LiteralValue} [%s{attributesText f.PropertyAttributes}]"

    let entityShape (entity: FSharpEntity) =
        let kinds =
            named
                [ entity.IsFSharpModule, "module"
                  entity.IsFSharpRecord, "record"
                  entity.IsFSharpUnion, "union"
                  entity.IsFSharpAbbreviation, "abbreviation"
                  entity.IsFSharpExceptionDeclaration, "exception"
                  entity.IsEnum, "enum"
                  entity.IsDelegate, "delegate"
                  entity.IsInterface, "interface"
                  entity.IsClass, "class"
                  entity.IsValueType, "struct"
                  entity.IsMeasure, "measure" ]

        let generics =
            entity.GenericParameters |> Seq.map (fun g -> g.Name) |> String.concat ", "

        let body =
            // An abbreviation IS the type it names, so that type is followed. The
            // types of fields, cases, bases and interfaces are named, not followed,
            // and members are not rendered here: a file reaches any of them through a
            // symbol use of its own, which renders it.
            [ if entity.IsFSharpAbbreviation then
                  follow entity.AbbreviatedType
                  "= " + typeText entity.AbbreviatedType
              elif not entity.IsFSharpModule && not entity.IsNamespace then
                  match entity.BaseType with
                  | Some baseType -> "inherit " + typeText baseType
                  | None -> ()

                  yield! entity.DeclaredInterfaces |> Seq.map (fun i -> "interface " + typeText i)
                  yield! entity.FSharpFields |> Seq.map (fun f -> "field " + fieldText f)

                  yield!
                      entity.UnionCases
                      |> Seq.map (fun case ->
                          let fields = case.Fields |> Seq.map fieldText |> String.concat " * "
                          $"case %s{case.Name} %s{accessText case.Accessibility} of %s{fields} [%s{attributesText case.Attributes}]") ]

        add (
            $"entity %s{entity.Assembly.SimpleName}/%s{entityName entity} [%s{kinds}] %s{accessText entity.Accessibility} <%s{generics}> [%s{attributesText entity.Attributes}]\n  "
            + String.concat "\n  " body
        )

    let symbols = HashSet<FSharpSymbol>()

    let render (symbolUse: FSharpSymbolUse) =
        let symbol = symbolUse.Symbol

        let isLocal =
            symbol.DeclarationLocation
            |> Option.exists (fun range -> range.FileName = symbolUse.FileName)

        // A symbol declared in this file is in its source. Every type a local value can
        // have comes from a literal, an annotation (an entity use) or a used symbol's
        // type, which is followed below, so locals need no walk.
        match symbol with
        | _ when isLocal || not (symbols.Add symbol) -> ()
        | :? FSharpMemberOrFunctionOrValue as m ->
            let assembly = m.Assembly

            if inRepo assembly then
                m.DeclaringEntity |> Option.iter visit
                add ("val " + memberText m)
            else
                add $"ext %s{assembly.QualifiedName} %s{m.FullName}"
        | :? FSharpEntity as entity -> visit entity
        | :? FSharpField as field ->
            field.DeclaringEntity |> Option.iter visit
            follow field.FieldType
        | :? FSharpUnionCase as case ->
            visit case.DeclaringEntity
            case.Fields |> Seq.iter (fun field -> follow field.FieldType)
        | :? FSharpActivePatternCase as case ->
            let group = String.concat "|" case.Group.Names
            follow case.Group.OverallType
            add $"pattern %s{case.Name} %s{group} : %s{typeText case.Group.OverallType}"
        // Generic and ordinary parameters are part of a signature rendered above.
        | _ -> ()

    results.GetAllUsesOfAllSymbolsInFile() |> Seq.iter render

    while pending.Count > 0 do
        entityShape (pending.Dequeue())

    lines |> Seq.sort |> String.concat "\n"

/// A hash of the public SHAPE of everything a file's type check resolved outside the
/// file: each upstream value's and member's signature, and the declaration of each type
/// from this repository the file names or reaches through such a signature (kind,
/// fields, union cases, base types, interfaces, attributes) — never a body, a position or
/// a path.
///
/// A file's analyzer verdict depends on other files only through what its check
/// resolved there. Its diagnostics do not record that: a type in an earlier file or a
/// referenced project can change from a record to a union while this file still compiles
/// cleanly, and a typed analyzer then answers differently. Hashing those files' content
/// instead would miss on every body edit anywhere upstream.
///
/// Symbols from outside the repository contribute only their name and their assembly's
/// qualified name (which carries its version). The cost is proportional to the symbols
/// this file uses, not to the size of the project above it.
let usedSignaturesHash
    (repoRoot: string option)
    (options: FSharpProjectOptions)
    (checkResults: FileCheckState)
    : string option =
    match checkResults with
    | ParseOnly -> Some "parse-only"
    | FullCheck results when isNull (box results) -> Some "full-check-null"
    | FullCheck results ->
        // FCS can throw from a symbol property on partially-checked code. A shape that
        // cannot be described leaves no key, so the caller runs rather than replays.
        try
            Some(usedShapes repoRoot options results |> CheckCache.sha256Hex)
        with ex ->
            Logging.debug "cache" $"used-signatures unavailable (%s{ex.GetType().Name}): %s{ex.Message}"
            None
