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
        for reference in opts.ReferencedProjects do
            if projectNames.Add(Path.GetFileNameWithoutExtension reference.OutputFile) then
                match reference with
                | FSharpReferencedProject.FSharpReference(_, referenced) -> addReferences referenced
                | _ -> ()

    if not (isNull (box options)) then
        addReferences options

    let rootPrefix =
        repoRoot
        |> Option.map (fun root ->
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + string Path.DirectorySeparatorChar)

    fun assembly ->
        match assembly.FileName with
        | None -> true
        | Some file ->
            projectNames.Contains assembly.SimpleName
            || rootPrefix
               |> Option.exists (fun prefix -> file.StartsWith(prefix, StringComparison.Ordinal))

let private accessText (access: FSharpAccessibility) =
    if access.IsPublic then "public"
    elif access.IsInternal then "internal"
    elif access.IsProtected then "protected"
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

    // FCS throws from some symbol properties on partially-checked code; the failure is
    // then the rendered fact, which is as deterministic as the code that produced it.
    let guarded (label: string) (render: unit -> unit) =
        try
            render ()
        with ex ->
            add $"%s{label} unrendered %s{ex.GetType().Name}"

    let visit (entity: FSharpEntity) =
        let name = entityName entity

        if visited.Add $"%s{entity.Assembly.SimpleName}/%s{name}" then
            if inRepo entity.Assembly then
                pending.Enqueue entity
            else
                add $"ext %s{entity.Assembly.QualifiedName} %s{name}"

    let attributesText (attributes: FSharpAttribute seq) =
        attributes
        |> Seq.map (fun a -> a.Format FSharpDisplayContext.Empty)
        |> Seq.sort
        |> String.concat ", "

    // `follow`: queue the repository types this type names, so a type reached only
    // through it (an inferred value's record, a field's union) is rendered too.
    let rec typeText (follow: bool) (t: FSharpType) : string =
        let args (separator: string) =
            t.GenericArguments |> Seq.map (typeText follow) |> String.concat separator

        if t.IsGenericParameter then
            "'" + t.GenericParameter.Name
        elif t.IsFunctionType then
            "(" + args " -> " + ")"
        elif t.IsTupleType then
            (if t.IsStructTupleType then "struct (" else "(") + args " * " + ")"
        elif t.IsAnonRecordType then
            Seq.zip t.AnonRecordTypeDetails.SortedFieldNames t.GenericArguments
            |> Seq.map (fun (name, arg) -> $"%s{name}: %s{typeText follow arg}")
            |> String.concat "; "
            |> sprintf "{| %s |}"
        elif t.HasTypeDefinition then
            if follow then
                visit t.TypeDefinition

            entityName t.TypeDefinition + "<" + args ", " + ">"
        else
            t.Format FSharpDisplayContext.Empty

    let literalText (value: obj option) =
        value |> Option.map (sprintf " = %A") |> Option.defaultValue ""

    let memberText (m: FSharpMemberOrFunctionOrValue) =
        let parameters =
            m.CurriedParameterGroups
            |> Seq.map (Seq.map (fun p -> defaultArg p.Name "_") >> String.concat ", ")
            |> String.concat " | "

        let scope = if m.IsInstanceMember then "instance" else "static"
        let mutability = if m.IsMutable then " mutable" else ""

        $"%s{m.CompiledName}/%s{m.DisplayName} %s{scope}%s{mutability} %s{accessText m.Accessibility} (%s{parameters}) : %s{typeText true m.FullType}%s{literalText m.LiteralValue} [%s{attributesText m.Attributes}]"

    let fieldText (f: FSharpField) =
        let mutability = if f.IsMutable then " mutable" else ""
        let scope = if f.IsStatic then " static" else ""

        $"%s{f.Name}%s{scope}%s{mutability} %s{accessText f.Accessibility} : %s{typeText false f.FieldType}%s{literalText f.LiteralValue} [%s{attributesText f.PropertyAttributes}]"

    let entityShape (entity: FSharpEntity) =
        guarded entity.DisplayName (fun () ->
            let kinds =
                [ if entity.IsFSharpModule then
                      "module"
                  if entity.IsFSharpRecord then
                      "record"
                  if entity.IsFSharpUnion then
                      "union"
                  if entity.IsFSharpAbbreviation then
                      "abbreviation"
                  if entity.IsFSharpExceptionDeclaration then
                      "exception"
                  if entity.IsEnum then
                      "enum"
                  if entity.IsDelegate then
                      "delegate"
                  if entity.IsInterface then
                      "interface"
                  if entity.IsClass then
                      "class"
                  if entity.IsValueType then
                      "struct"
                  if entity.IsMeasure then
                      "measure" ]
                |> String.concat " "

            let generics =
                entity.GenericParameters |> Seq.map (fun g -> g.Name) |> String.concat ", "

            let body =
                // An abbreviation IS the type it names, so that type is followed. The
                // types of fields, cases, bases and interfaces are named, not followed,
                // and members are not rendered here: a file reaches any of them through a
                // symbol use of its own, which renders it.
                [ if entity.IsFSharpAbbreviation then
                      "= " + typeText true entity.AbbreviatedType
                  elif not entity.IsFSharpModule && not entity.IsNamespace then
                      match entity.BaseType with
                      | Some baseType -> "inherit " + typeText false baseType
                      | None -> ()

                      for i in entity.DeclaredInterfaces do
                          "interface " + typeText false i

                      for f in entity.FSharpFields do
                          "field " + fieldText f

                      for case in entity.UnionCases do
                          let fields = case.Fields |> Seq.map fieldText |> String.concat " * "
                          $"case %s{case.Name} %s{accessText case.Accessibility} of %s{fields} [%s{attributesText case.Attributes}]" ]

            add (
                $"entity %s{entity.Assembly.SimpleName}/%s{entityName entity} [%s{kinds}] %s{accessText entity.Accessibility} <%s{generics}> [%s{attributesText entity.Attributes}]\n  "
                + String.concat "\n  " body
            ))

    let symbols = HashSet<FSharpSymbol>()

    for symbolUse in results.GetAllUsesOfAllSymbolsInFile() do
        let symbol = symbolUse.Symbol

        if symbols.Add symbol then
            guarded symbol.DisplayName (fun () ->
                let isLocal =
                    symbol.DeclarationLocation
                    |> Option.exists (fun range -> range.FileName = symbolUse.FileName)

                // A symbol declared in this file is in its source. Every type a local
                // value can have comes from a literal, an annotation (an entity use) or a
                // used symbol's type, which is followed below, so locals need no walk.
                match symbol with
                | _ when isLocal -> ()
                | :? FSharpMemberOrFunctionOrValue as m when inRepo m.Assembly ->
                    m.DeclaringEntity |> Option.iter visit
                    add ("val " + memberText m)
                | :? FSharpMemberOrFunctionOrValue as m -> add $"ext %s{m.Assembly.QualifiedName} %s{m.FullName}"
                | :? FSharpEntity as entity -> visit entity
                | :? FSharpField as field ->
                    field.DeclaringEntity |> Option.iter visit
                    typeText true field.FieldType |> ignore
                | :? FSharpUnionCase as case ->
                    visit case.DeclaringEntity

                    for field in case.Fields do
                        typeText true field.FieldType |> ignore
                | :? FSharpActivePatternCase as case ->
                    let group = String.concat "|" case.Group.Names
                    add $"pattern %s{case.Name} %s{group} : %s{typeText true case.Group.OverallType}"
                // Generic and ordinary parameters are part of a signature rendered above.
                | _ -> ())

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
    : string =
    match checkResults with
    | ParseOnly -> "parse-only"
    | FullCheck results when isNull (box results) -> "full-check-null"
    | FullCheck results -> usedShapes repoRoot options results |> CheckCache.sha256Hex
