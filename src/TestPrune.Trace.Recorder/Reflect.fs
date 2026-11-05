namespace TestPrune.Trace.Recorder

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open Microsoft.FSharp.Reflection

// The assembly-sweep shims are named `<Type>_<Method>` like the file shims (the weaver binds
// a redirect to its shim by name); the union-reflection shims keep FSharp.Core's own names.
// fsharplint:disable MemberNames
/// Call-site replacements for reflection whose answer changes with no traced method changing
/// (AUTOMATION-1495, phase-2 plan Task 11):
///
///   * union reflection (`FSharpType`/`FSharpValue`'s union APIs) notes the union type as a
///     `type` input, `<assembly file>|<CLR full name>`, which ingestion joins to the type's
///     symbol: adding a case changes that symbol's hash, so R2 reselects every enumerating test;
///   * a whole-assembly type sweep notes the assembly as a `types` input, hashed over its type
///     names: a new type changes it, so R3 reselects every sweeping test.
///
/// Only types and assemblies under the repository root are noted. Each shim then does exactly
/// what the original does. A type the mutation harness lists (`Contract.MutateTypesEnv`)
/// makes its reflection throw, after it is noted.
[<AbstractClass; Sealed>]
type Reflect =
    static member private UnderRoot(state: RecorderState, file: string) =
        not (isNull state)
        && not (isNull state.RepoRoot)
        && not (String.IsNullOrEmpty file)
        && (let r = state.RepoRoot.TrimEnd Path.DirectorySeparatorChar
            let full = Path.GetFullPath file

            full.StartsWith(r, StringComparison.Ordinal)
            && full.Length > r.Length
            && full.[r.Length] = Path.DirectorySeparatorChar)

    /// Notes an assembly's type set on `state`. Never throws.
    static member internal NoteAssemblyWith(state: RecorderState, a: Assembly) : unit =
        if not (isNull a) && not a.IsDynamic && Reflect.UnderRoot(state, a.Location) then
            state.NoteScope().Inputs.TryAdd(struct ("types", Path.GetFullPath a.Location), 0uy)
            |> ignore

    /// Notes a reflected type (its generic definition) on `state`, then throws when the
    /// mutation list names it.
    static member internal NoteTypeWith(state: RecorderState, t: Type) : unit =
        if not (isNull state) && not (isNull t) then
            let def = if t.IsGenericType then t.GetGenericTypeDefinition() else t

            if not (isNull def.FullName) then
                if not def.Assembly.IsDynamic && Reflect.UnderRoot(state, def.Assembly.Location) then
                    state
                        .NoteScope()
                        .Inputs.TryAdd(
                            struct ("type", Path.GetFullPath def.Assembly.Location + "|" + def.FullName),
                            0uy
                        )
                    |> ignore

                let m: HashSet<string> = state.MutantTypes

                if not (isNull m) && m.Contains def.FullName then
                    raise (MutantHitException -1)

    static member private NoteCase(c: UnionCaseInfo) =
        if not (isNull (box c)) then
            Reflect.NoteTypeWith(Runtime.state, c.DeclaringType)

    // --- union reflection ---------------------------------------------------------------

    /// Shim for `FSharpType.GetUnionCases(Type, ?BindingFlags)`.
    static member GetUnionCases(unionType: Type, bindingFlags: BindingFlags option) : UnionCaseInfo[] =
        Reflect.NoteTypeWith(Runtime.state, unionType)
        FSharpType.GetUnionCases(unionType, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpType.IsUnion(Type, ?BindingFlags)`.
    static member IsUnion(typ: Type, bindingFlags: BindingFlags option) : bool =
        Reflect.NoteTypeWith(Runtime.state, typ)
        FSharpType.IsUnion(typ, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.GetUnionFields(obj, Type, ?BindingFlags)`.
    static member GetUnionFields(value: obj, unionType: Type, bindingFlags: BindingFlags option) =
        Reflect.NoteTypeWith(
            Runtime.state,
            if isNull unionType && not (isNull value) then
                value.GetType()
            else
                unionType
        )

        FSharpValue.GetUnionFields(value, unionType, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.MakeUnion(UnionCaseInfo, obj[], ?BindingFlags)`.
    static member MakeUnion(unionCase: UnionCaseInfo, args: obj[], bindingFlags: BindingFlags option) : obj =
        Reflect.NoteCase unionCase
        FSharpValue.MakeUnion(unionCase, args, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.PreComputeUnionTagReader(Type, ?BindingFlags)`.
    static member PreComputeUnionTagReader(unionType: Type, bindingFlags: BindingFlags option) : obj -> int =
        Reflect.NoteTypeWith(Runtime.state, unionType)
        FSharpValue.PreComputeUnionTagReader(unionType, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.PreComputeUnionTagMemberInfo(Type, ?BindingFlags)`.
    static member PreComputeUnionTagMemberInfo(unionType: Type, bindingFlags: BindingFlags option) : MemberInfo =
        Reflect.NoteTypeWith(Runtime.state, unionType)
        FSharpValue.PreComputeUnionTagMemberInfo(unionType, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.PreComputeUnionReader(UnionCaseInfo, ?BindingFlags)`.
    static member PreComputeUnionReader(unionCase: UnionCaseInfo, bindingFlags: BindingFlags option) : obj -> obj[] =
        Reflect.NoteCase unionCase
        FSharpValue.PreComputeUnionReader(unionCase, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.PreComputeUnionConstructor(UnionCaseInfo, ?BindingFlags)`.
    static member PreComputeUnionConstructor
        (unionCase: UnionCaseInfo, bindingFlags: BindingFlags option)
        : obj[] -> obj =
        Reflect.NoteCase unionCase
        FSharpValue.PreComputeUnionConstructor(unionCase, ?bindingFlags = bindingFlags)

    /// Shim for `FSharpValue.PreComputeUnionConstructorInfo(UnionCaseInfo, ?BindingFlags)`.
    static member PreComputeUnionConstructorInfo
        (unionCase: UnionCaseInfo, bindingFlags: BindingFlags option)
        : MethodInfo =
        Reflect.NoteCase unionCase
        FSharpValue.PreComputeUnionConstructorInfo(unionCase, ?bindingFlags = bindingFlags)

    // --- whole-assembly type sweeps -------------------------------------------------------

    /// Shim for `Assembly.GetTypes()`.
    static member Assembly_GetTypes(a: Assembly) : Type[] =
        Reflect.NoteAssemblyWith(Runtime.state, a)
        a.GetTypes()

    /// Shim for `Assembly.GetExportedTypes()`.
    static member Assembly_GetExportedTypes(a: Assembly) : Type[] =
        Reflect.NoteAssemblyWith(Runtime.state, a)
        a.GetExportedTypes()

    /// Shim for `Assembly.DefinedTypes`.
    static member Assembly_get_DefinedTypes(a: Assembly) : IEnumerable<TypeInfo> =
        Reflect.NoteAssemblyWith(Runtime.state, a)
        a.DefinedTypes

    /// Shim for `Assembly.ExportedTypes`.
    static member Assembly_get_ExportedTypes(a: Assembly) : IEnumerable<Type> =
        Reflect.NoteAssemblyWith(Runtime.state, a)
        a.ExportedTypes

    /// Shim for `Module.GetTypes()`.
    static member Module_GetTypes(m: Module) : Type[] =
        Reflect.NoteAssemblyWith(Runtime.state, m.Assembly)
        m.GetTypes()
