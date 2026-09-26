/// Source documents for F# closures that have no sequence points of their own.
///
/// F# emits the closures of a class's `let` bindings and members into its file's
/// `<StartupCode$Assembly>` class. A closure that only passes a function along
/// (`List.map double`) gets no sequence points, so the PDB names no document for it, and
/// its owner (`<StartupCode$A>.$File`) is no symbol: the joiner could map it to nothing.
/// The method that creates it (`newobj` of its constructor, or `ldsfld` of its cached
/// instance) is the code it was written in, so that method's document is the closure's.
/// F# names a closure after its source line (`DoubleAll@12`, `clo@263-1`).
module internal TestPrune.Trace.ClosureDocuments

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Mono.Cecil
open Mono.Cecil.Cil

let private startupCodePrefix = "<StartupCode$"

let private isStartupCodeName (fullName: string) =
    fullName.StartsWith(startupCodePrefix, StringComparison.Ordinal)

/// The line F# encodes in a closure's name: `DoubleAll@12` and `clo@263-1` are 12 and
/// 263; a name without one is 0, which the joiner treats as "no line".
let private closureLine = Regex(@"@(\d+)(?:-\d+)?$", RegexOptions.Compiled)

let lineOf (typeName: string) =
    let m = closureLine.Match typeName
    if m.Success then int m.Groups.[1].Value else 0

/// The documents of a method's visible sequence points.
let private documentsOf (meth: MethodDefinition) =
    if meth.DebugInformation.HasSequencePoints then
        meth.DebugInformation.SequencePoints
        |> Seq.filter (fun s -> not s.IsHidden)
        |> Seq.map (fun s -> s.Document)
        |> Seq.toList
    else
        []

/// The StartupCode type an instruction creates: `newobj` of its constructor, or `ldsfld`
/// of its cached instance.
let private createdBy (ins: Instruction) : TypeReference option =
    match ins.Operand with
    | :? MethodReference as r when ins.OpCode = OpCodes.Newobj -> Some r.DeclaringType
    | :? FieldReference as f when ins.OpCode = OpCodes.Ldsfld -> Some f.DeclaringType
    | _ -> None
    |> Option.filter (fun t -> isStartupCodeName t.FullName)

/// Every StartupCode type of a module, by Cecil full name, to the methods that create it.
let private creatorsOf (m: ModuleDefinition) : IDictionary<string, MethodDefinition list> =
    m.GetTypes()
    |> Seq.collect _.Methods
    |> Seq.filter _.HasBody
    |> Seq.collect (fun meth ->
        meth.Body.Instructions
        |> Seq.choose createdBy
        |> Seq.map (fun t -> t.FullName, meth))
    |> Seq.groupBy fst
    |> Seq.map (fun (created, pairs) -> created, pairs |> Seq.map snd |> Seq.toList)
    |> dict

/// Derives a document for the sequence-point-less methods of one module's StartupCode
/// types. The creators are found on first use, which may be part-way through the
/// module's rewrite: the weave adds no `newobj` or `ldsfld` of a StartupCode type and
/// removes none, so the creators it finds are the same either way.
type Resolver(m: ModuleDefinition) =
    let creators = lazy (creatorsOf m)

    /// The documents of `t`'s creators; a creator with no sequence points of its own that
    /// is itself a StartupCode closure contributes its own creators'.
    let rec documents (visited: Set<string>) (t: TypeDefinition) : Document list =
        match creators.Value.TryGetValue t.FullName with
        | true, methods ->
            methods
            |> List.collect (fun c ->
                match documentsOf c with
                | [] when
                    isStartupCodeName c.DeclaringType.FullName
                    && not (visited.Contains c.DeclaringType.FullName)
                    ->
                    documents (visited.Add c.DeclaringType.FullName) c.DeclaringType
                | docs -> docs)
        | _ -> []

    /// The document and line of a method of `t` that has no sequence points: its creators'
    /// one document, when `t` is a StartupCode type and they agree on exactly one.
    member _.Derive(t: TypeDefinition) : (Document * int) option =
        if isStartupCodeName t.FullName then
            match documents (Set.singleton t.FullName) t |> List.distinctBy (fun d -> d.Url) with
            | [ d ] -> Some(d, lineOf t.Name)
            | _ -> None
        else
            None
