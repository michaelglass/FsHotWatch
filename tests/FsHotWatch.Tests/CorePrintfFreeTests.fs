/// FsHotWatch.Core never reaches FSharp.Core's printf engine or its F# reflection
/// (AUTOMATION-1695, experiment E5). Both are trim/AOT hazards: `PrintfImpl` builds its closures
/// with MakeGenericMethod/MakeGenericType, which Native AOT cannot guarantee for a value-type
/// instantiation (`%d{n}`), and the `%A` formatter and `FSharpType`/`FSharpValue` read types by
/// reflection. A Native AOT publish of a Core consumer listed 46 such warning sites in FSharp.Core,
/// every one behind printf, so Core must reach none of it.
///
/// The compiler routes more through printf than `sprintf` and typed holes (`%s{x}`): measured on
/// this tree, an UNTYPED interpolation `$"..{x}.."` also lowers to `PrintFormatToString` when a
/// hole is not a string or the string has more than four parts; only the rest become
/// `String.Concat`. So Core builds such text with `+` (numbers through
/// `ToString(CultureInfo.InvariantCulture)`, as printf's `%d` printed them) and writes with
/// `Console`. This reads the compiled assembly's metadata, so any of those forms anywhere in Core
/// fails here, not in a later AOT publish.
module FsHotWatch.Tests.CorePrintfFreeTests

open System
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open Xunit
open Swensen.Unquote

/// A type reference's full name, `Outer+Inner` for a nested type.
let rec private typeRefName (md: MetadataReader) (handle: TypeReferenceHandle) : string =
    let t = md.GetTypeReference handle
    let name = md.GetString t.Name

    if t.ResolutionScope.Kind = HandleKind.TypeReference then
        let outer: TypeReferenceHandle = TypeReferenceHandle.op_Explicit t.ResolutionScope
        typeRefName md outer + "+" + name
    else
        let ns = md.GetString t.Namespace
        if String.IsNullOrEmpty ns then name else ns + "." + name

/// Types whose every use is printf or F# reflection.
let private forbiddenType (fullName: string) =
    fullName = "Microsoft.FSharp.Core.PrintfModule"
    || fullName.StartsWith("Microsoft.FSharp.Core.PrintfImpl", StringComparison.Ordinal)
    || fullName.StartsWith("Microsoft.FSharp.Core.PrintfFormat`", StringComparison.Ordinal)
    || fullName = "Microsoft.FSharp.Reflection.FSharpType"
    || fullName = "Microsoft.FSharp.Reflection.FSharpValue"

/// `ExtraTopLevelOperators`' printf entry points: `sprintf`, `printfn`, `eprintfn`, `failwithf`
/// (`PrintFormatToStringThenFail`) and the rest are all `PrintFormat*`.
[<Literal>]
let private Operators = "Microsoft.FSharp.Core.ExtraTopLevelOperators"

/// What `assembly` references of printf and F# reflection: types by full name, operators as
/// `ExtraTopLevelOperators.<member>`. Sorted, distinct.
let private printfReferences (assembly: Assembly) : string list =
    use stream = File.OpenRead assembly.Location
    use pe = new PEReader(stream)
    let md = pe.GetMetadataReader()

    let types =
        [ for h in md.TypeReferences do
              let name = typeRefName md h

              if forbiddenType name then
                  name ]

    let operators =
        [ for h in md.MemberReferences do
              let m = md.GetMemberReference h

              if m.Parent.Kind = HandleKind.TypeReference then
                  let parent: TypeReferenceHandle = TypeReferenceHandle.op_Explicit m.Parent
                  let name = md.GetString m.Name

                  if
                      typeRefName md parent = Operators
                      && name.StartsWith("Print", StringComparison.Ordinal)
                  then
                      "ExtraTopLevelOperators." + name ]

    types @ operators |> List.distinct |> List.sort

[<Fact>]
let ``FsHotWatch.Core references neither the printf engine nor F# reflection`` () =
    let core = typeof<FsHotWatch.Core.NonEmpty<int>>.Assembly
    test <@ core.GetName().Name = "FsHotWatch.Core" @>
    test <@ printfReferences core = [] @>

/// The control: the same scan finds printf where it is used, so a green above means something.
[<Fact>]
let ``the printf scan finds printf in an assembly that uses it`` () =
    let daemon = typeof<FsHotWatch.RecordAudit.Report>.Assembly
    let found = printfReferences daemon
    test <@ found |> List.exists _.StartsWith("Microsoft.FSharp.Core.PrintfFormat`") @>
    test <@ found |> List.exists _.StartsWith("ExtraTopLevelOperators.PrintFormat") @>
