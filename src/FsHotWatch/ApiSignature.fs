/// The API signature of one checked file: everything it declares that a later file of its
/// project, or a project referencing its project, can type-check against.
///
/// The check-result cache keys a file by what came before it. Keying that by the earlier
/// files' CONTENT means a comment or a function body edited in an early file invalidates
/// every file after it, and every file of every project downstream, although none of them
/// can tell the difference. Keyed by this signature instead, those files keep their
/// results, and only an edit that changes a declaration reaches them.
///
/// The signature covers every declaration at every accessibility (a later file of the
/// same assembly sees `internal`, and a namespace's private types), with the attributes and
/// their arguments, literal values, union cases, record and class fields, members with
/// their parameter names and types, generic constraints, extension members and XML docs.
/// It leaves out bodies, comments and positions.
///
/// It refuses (returns None, so the caller falls back to the file's content) whenever a
/// declaration-only hash cannot stand for the file:
/// - an `inline` value or member, whose body is part of what its callers compile;
/// - an `assembly:` attribute (`InternalsVisibleTo` decides what a referencing project
///   sees), which FCS does not report as a declaration of the file;
/// - a check with errors, whose declarations FCS may have recovered only in part;
/// - a result without typed results, or any symbol FCS cannot describe.
module internal FsHotWatch.ApiSignature

open System
open System.Collections.Generic
open FSharp.Compiler.Symbols
open FSharp.Compiler.Diagnostics
open System.Text.RegularExpressions
open FsHotWatch.Events

let private accessText (access: FSharpAccessibility) =
    if access.IsPublic then "public"
    elif access.IsInternal then "internal"
    else "private"

let private entityName (entity: FSharpEntity) =
    entity.TryFullName |> Option.defaultValue entity.DisplayName

let private attributesText (attributes: IList<FSharpAttribute>) =
    attributes
    |> Seq.map (fun a -> a.Format FSharpDisplayContext.Empty)
    |> Seq.sort
    |> String.concat ", "

let rec private typeText (t: FSharpType) : string =
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
    else
        // Every other type names a definition; one that did not would throw here, and a
        // file whose declarations cannot be described has no signature.
        entityName t.TypeDefinition + "<" + args ", " + ">"

let private literalText (value: obj option) =
    value |> Option.map (sprintf " = %A") |> Option.defaultValue ""

/// A declaration of the checked file carries its doc text; only a referenced assembly's
/// symbol points at an XML file instead.
let private docText (doc: FSharpXmlDoc) =
    (match doc with
     | FSharpXmlDoc.FromXmlText text -> text.UnprocessedLines
     | _ -> [||])
    |> String.concat "\n"

/// The names whose condition holds, space-separated.
let private named (conditions: (bool * string) list) =
    conditions
    |> List.choose (fun (holds, name) -> if holds then Some name else None)
    |> String.concat " "

let private constraintText (c: FSharpGenericParameterConstraint) =
    let flags =
        named
            [ c.IsSupportsNullConstraint, "null"
              c.IsNotSupportsNullConstraint, "not-null"
              c.IsNonNullableValueTypeConstraint, "struct"
              c.IsReferenceTypeConstraint, "not-struct"
              c.IsComparisonConstraint, "comparison"
              c.IsEqualityConstraint, "equality"
              c.IsUnmanagedConstraint, "unmanaged"
              c.IsRequiresDefaultConstructorConstraint, "new"
              c.IsAllowsRefStructConstraint, "allows-ref-struct" ]

    let typed =
        [ if c.IsCoercesToConstraint then
              ":> " + typeText c.CoercesToTarget
          if c.IsEnumConstraint then
              "enum " + typeText c.EnumConstraintTarget
          if c.IsDelegateConstraint then
              "delegate "
              + typeText c.DelegateConstraintData.DelegateTupledArgumentType
              + " "
              + typeText c.DelegateConstraintData.DelegateReturnType
          // A member constraint needs an inline member, and a defaults-to or simple-choice
          // constraint occurs only in FSharp.Core: none of them reaches a file with a
          // signature, and one that did would not be told apart, so it refuses instead.
          if c.IsMemberConstraint || c.IsDefaultsToConstraint || c.IsSimpleChoiceConstraint then
              failwith "a constraint the signature does not render" ]

    flags + " " + String.concat "; " typed

let private genericsText (parameters: IList<FSharpGenericParameter>) =
    parameters
    |> Seq.map (fun g ->
        let constraints = g.Constraints |> Seq.map constraintText |> String.concat ", "
        $"'%s{g.Name} [%b{g.IsSolveAtCompileTime} %b{g.IsMeasure}] when (%s{constraints}) [%s{attributesText g.Attributes}]")
    |> String.concat ", "

let private memberText (m: FSharpMemberOrFunctionOrValue) =
    let parameters =
        m.CurriedParameterGroups
        |> Seq.map (
            Seq.map (fun p ->
                let flags =
                    named
                        [ p.IsOptionalArg, "optional"
                          p.IsParamArrayArg, "params"
                          p.IsOutArg, "out"
                          p.IsInArg, "in" ]

                let name = defaultArg p.Name "_"

                $"%s{name} [%s{flags}] : %s{typeText p.Type} [%s{attributesText p.Attributes}]")
            >> String.concat ", "
        )
        |> String.concat " | "

    let flags =
        named
            [ m.IsInstanceMember, "instance"
              m.IsMutable, "mutable"
              m.IsExtensionMember, "extension"
              m.IsOverrideOrExplicitInterfaceImplementation, "override"
              m.IsDispatchSlot, "abstract"
              m.IsConstructor, "new"
              m.IsProperty, "property"
              m.HasGetterMethod, "get"
              m.HasSetterMethod, "set"
              m.IsEvent, "event"
              m.IsActivePattern, "pattern"
              m.IsCompilerGenerated, "generated" ]

    let declaring = m.DeclaringEntity |> Option.map entityName |> Option.defaultValue ""

    let extending =
        if m.IsExtensionMember then
            m.ApparentEnclosingEntity |> Option.map entityName |> Option.defaultValue ""
        else
            ""

    let returned = typeText m.ReturnParameter.Type

    $"val %s{declaring}.%s{m.CompiledName}/%s{m.DisplayName} on %s{extending} [%s{flags}] %s{accessText m.Accessibility} <%s{genericsText m.GenericParameters}> (%s{parameters}) : %s{typeText m.FullType} -> %s{returned}%s{literalText m.LiteralValue} [%s{attributesText m.Attributes}] [%s{attributesText m.ReturnParameter.Attributes}] %s{docText m.XmlDoc}"

let private fieldText (f: FSharpField) =
    let flags =
        named
            [ f.IsStatic, "static"
              f.IsMutable, "mutable"
              f.IsVolatile, "volatile"
              f.IsCompilerGenerated, "generated"
              f.IsDefaultValue, "default" ]

    $"%s{f.Name} [%s{flags}] %s{accessText f.Accessibility} : %s{typeText f.FieldType}%s{literalText f.LiteralValue} [%s{attributesText f.PropertyAttributes}] [%s{attributesText f.FieldAttributes}] %s{docText f.XmlDoc}"

let private entityText (entity: FSharpEntity) =
    let kinds =
        named
            [ entity.IsNamespace, "namespace"
              entity.IsFSharpModule, "module"
              entity.IsFSharpRecord, "record"
              entity.IsFSharpUnion, "union"
              entity.IsFSharpAbbreviation, "abbreviation"
              entity.IsFSharpExceptionDeclaration, "exception"
              entity.IsEnum, "enum"
              entity.IsDelegate, "delegate"
              entity.IsInterface, "interface"
              entity.IsClass, "class"
              entity.IsValueType, "struct"
              entity.IsMeasure, "measure"
              entity.IsAbstractClass, "abstract"
              entity.IsOpaque, "opaque" ]

    let body =
        [ if entity.IsFSharpAbbreviation then
              "= " + typeText entity.AbbreviatedType
          elif not entity.IsNamespace then
              match entity.BaseType with
              | Some baseType when not entity.IsFSharpModule -> "inherit " + typeText baseType
              | _ -> ()

              yield! entity.DeclaredInterfaces |> Seq.map (fun i -> "interface " + typeText i)
              yield! entity.FSharpFields |> Seq.map (fun f -> "field " + fieldText f)

              yield!
                  entity.UnionCases
                  |> Seq.map (fun case ->
                      let fields = case.Fields |> Seq.map fieldText |> String.concat " * "

                      $"case %s{case.Name} %s{accessText case.Accessibility} of %s{fields} [%s{attributesText case.Attributes}] %s{docText case.XmlDoc}")

              yield! entity.MembersFunctionsAndValues |> Seq.map memberText
              yield! entity.NestedEntities |> Seq.map (fun nested -> "nested " + entityName nested) ]

    $"entity %s{entityName entity} [%s{kinds}] %s{accessText entity.Accessibility} <%s{genericsText entity.GenericParameters}> [%s{attributesText entity.Attributes}] %s{docText entity.XmlDoc}\n  "
    + String.concat "\n  " body

let private assemblyAttribute = Regex(@"\[<\s*assembly\s*:", RegexOptions.Compiled)

let private isInline (m: FSharpMemberOrFunctionOrValue) =
    match m.InlineAnnotation with
    | FSharpInlineAnnotation.NeverInline
    | FSharpInlineAnnotation.OptionalInline -> false
    | _ -> true

/// The declarations of a checked file without errors, or None when it has an inline member.
let private declarationsOf (results: FSharp.Compiler.CodeAnalysis.FSharpCheckFileResults) : string option =
    let lines = HashSet<string>(StringComparer.Ordinal)
    let mutable inlined = false

    // Every declaration of the file is a symbol use at its definition. A value bound
    // inside a body is one too, so only module values and members count.
    for symbolUse in results.GetAllUsesOfAllSymbolsInFile() do
        if symbolUse.IsFromDefinition then
            match symbolUse.Symbol with
            | :? FSharpEntity as entity -> lines.Add(entityText entity) |> ignore
            | :? FSharpMemberOrFunctionOrValue as m when m.IsModuleValueOrMember ->
                if isInline m then
                    inlined <- true

                lines.Add(memberText m) |> ignore
            | :? FSharpField as field ->
                let owner = field.DeclaringEntity |> Option.map entityName |> Option.defaultValue ""
                lines.Add($"field %s{owner}.%s{fieldText field}") |> ignore
            | :? FSharpUnionCase as case ->
                lines.Add($"case %s{entityName case.DeclaringEntity}.%s{case.Name}") |> ignore
            | :? FSharpActivePatternCase as case ->
                let group = String.concat "|" case.Group.Names

                lines.Add($"pattern %s{case.Name} %s{group} : %s{typeText case.Group.OverallType}")
                |> ignore
            | _ -> ()

    if inlined then
        None
    else
        Some(lines |> Seq.sort |> String.concat "\n")

/// The text `ofCheck` hashes, or None when the file has no signature of its own.
let signatureText (source: string) (checkResults: FileCheckState) : string option =
    match checkResults with
    | ParseOnly -> None
    | _ when assemblyAttribute.IsMatch source -> None
    | FullCheck results when isNull (box results) -> None
    | FullCheck results ->
        // FCS can throw from a symbol property on partially-checked code. A file it cannot
        // describe has no signature, so its content stands in.
        try
            let hasErrors =
                results.Diagnostics
                |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            if hasErrors then None else declarationsOf results
        with ex ->
            Logging.debug "cache" $"api signature unavailable (%s{ex.GetType().Name}): %s{ex.Message}"
            None

/// The API signature hash of a checked file, or None when the file has no signature of its
/// own and its content must stand in for it.
let ofCheck (source: string) (checkResults: FileCheckState) : string option =
    signatureText source checkResults |> Option.map CheckCache.sha256Hex
