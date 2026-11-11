/// A managed assembly shaped like an F# compiler output where it matters to a check record's
/// key (AUTOMATION-1478): it embeds the checkout it was built in exactly where `fsc` does —
/// the PDB path in the debug directory, every source path in the compressed F# signature
/// data, each one behind the pickler's length prefix — and its module version id is derived
/// from all of those bytes, as a deterministic build derives it.
module FsHotWatch.Tests.FakeFSharpAssembly

open System
open System.IO
open System.IO.Compression
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Security.Cryptography
open System.Text

/// What the F# pickler writes for a string (`p_string`): its UTF-8 length as `p_int32`,
/// then the bytes.
let pickled (text: string) =
    let bytes = Encoding.UTF8.GetBytes text
    let n = bytes.Length

    let prefix =
        if n <= 0x7F then
            [| byte n |]
        elif n <= 0x3FFF then
            [| byte (0x80 ||| (n >>> 8)); byte (n &&& 0xFF) |]
        else
            Array.append [| 0xFFuy |] (BitConverter.GetBytes n)

    Array.append prefix bytes

/// One built output: its assembly name, the checkout it was built in, the source files and
/// entities its signature data names, its methods, and a string literal in a method body.
type Spec =
    {
        Name: string
        Root: string
        Sources: string list
        Entities: string list
        Methods: string list
        Literal: string
        /// Resources of the project's own, by name, and resources in another file of the assembly.
        Resources: (string * byte[]) list
        Linked: string list
        /// Assembly-level attributes, by the full name of a System.Reflection attribute type that
        /// takes one string, and the string: `AssemblyInformationalVersionAttribute`, say.
        Attributes: (string * string) list
        /// Also apply an attribute whose constructor this assembly defines, and one on a
        /// constructed generic type, as a project that declares its own attributes does.
        OwnAttributes: bool
        /// Quotations, each by the source file its pickle names and how many bytes follow that
        /// path. `fsc` stores a quotation's pickle as static data: an RVA field of the module
        /// type, typed by a nested value type `T_<size>Bytes@` that exists only to give the
        /// data its size, one per distinct size (AUTOMATION-1582).
        Quotations: (string * int) list
        /// A type declared after the module type, which a field of the module type is typed by.
        Later: string
        /// The element type every method returns.
        Returns: byte
    }

let spec (name: string) (root: string) =
    { Name = name
      Root = root
      Sources = [ "src/A/A1.fs"; "src/A/A2.fs"; "src/A/obj/Debug/net10.0/A.AssemblyInfo.fs" ]
      Entities = [ "A1"; "v"; "A2"; "w" ]
      Methods = [ "v"; "w" ]
      Literal = ""
      Resources = []
      Linked = []
      Attributes = []
      OwnAttributes = false
      Quotations = []
      Later = "Later"
      Returns = 0x01uy }

/// The static data `fsc` stores for a quotation of `source`: the pickled path, then `rest`
/// bytes.
let quotationData (path: string) (rest: int) =
    Array.append (pickled path) (Array.create rest 7uy)

let private deflate (bytes: byte[]) =
    use output = new MemoryStream()

    do
        use stream = new DeflateStream(output, CompressionLevel.Optimal, true)
        stream.Write(bytes, 0, bytes.Length)

    output.ToArray()

/// The assembly's bytes.
let build (spec: Spec) : byte[] =
    let path (relative: string) = Path.Combine(spec.Root, relative)
    let metadata = MetadataBuilder()
    let mvid = metadata.ReserveGuid()

    metadata.AddModule(0, metadata.GetOrAddString(spec.Name + ".dll"), mvid.Handle, GuidHandle(), GuidHandle())
    |> ignore

    let assembly =
        metadata.AddAssembly(
            metadata.GetOrAddString spec.Name,
            Version(1, 0, 0, 0),
            StringHandle(),
            BlobHandle(),
            AssemblyFlags(),
            AssemblyHashAlgorithm.Sha1
        )

    let runtime =
        metadata.AddAssemblyReference(
            metadata.GetOrAddString "System.Runtime",
            Version(10, 0, 0, 0),
            StringHandle(),
            BlobHandle(),
            AssemblyFlags(),
            BlobHandle()
        )

    // instance void .ctor(string)
    let stringConstructor = metadata.GetOrAddBlob [| 0x20uy; 0x01uy; 0x01uy; 0x0Euy |]

    for typeName, value in spec.Attributes do
        let attributeType =
            metadata.AddTypeReference(
                AssemblyReferenceHandle.op_Implicit runtime,
                metadata.GetOrAddString "System.Reflection",
                metadata.GetOrAddString typeName
            )

        let constructor =
            metadata.AddMemberReference(
                TypeReferenceHandle.op_Implicit attributeType,
                metadata.GetOrAddString ".ctor",
                stringConstructor
            )

        let argument = BlobBuilder()
        argument.WriteUInt16 1us
        argument.WriteSerializedString value
        argument.WriteUInt16 0us

        metadata.AddCustomAttribute(
            AssemblyDefinitionHandle.op_Implicit assembly,
            MemberReferenceHandle.op_Implicit constructor,
            metadata.GetOrAddBlob argument
        )
        |> ignore

    metadata.AddTypeDefinition(
        TypeAttributes(),
        StringHandle(),
        metadata.GetOrAddString "<Module>",
        EntityHandle(),
        MetadataTokens.FieldDefinitionHandle 1,
        MetadataTokens.MethodDefinitionHandle 1
    )
    |> ignore

    metadata.AddTypeDefinition(
        TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed,
        metadata.GetOrAddString spec.Name,
        metadata.GetOrAddString "Module",
        EntityHandle(),
        MetadataTokens.FieldDefinitionHandle 1,
        MetadataTokens.MethodDefinitionHandle 1
    )
    |> ignore

    // The module type's fields: one per quotation, then one typed by the later type. Its
    // nested raw-data types follow it, one per distinct size, then the later type.
    let quotations =
        spec.Quotations
        |> List.map (fun (source, rest) -> quotationData (path source) rest)

    let sizes = quotations |> List.map Array.length |> List.distinct
    let afterFields = MetadataTokens.FieldDefinitionHandle(quotations.Length + 2)
    let afterMethods = MetadataTokens.MethodDefinitionHandle(spec.Methods.Length + 1)
    let firstRaw = 3

    let valueType =
        metadata.AddTypeReference(
            AssemblyReferenceHandle.op_Implicit runtime,
            metadata.GetOrAddString "System",
            metadata.GetOrAddString "ValueType"
        )

    for size in sizes do
        let raw =
            metadata.AddTypeDefinition(
                TypeAttributes.NestedAssembly
                ||| TypeAttributes.ExplicitLayout
                ||| TypeAttributes.Sealed
                ||| TypeAttributes.BeforeFieldInit,
                StringHandle(),
                metadata.GetOrAddString $"T_%d{size}Bytes@",
                TypeReferenceHandle.op_Implicit valueType,
                afterFields,
                afterMethods
            )

        metadata.AddTypeLayout(raw, 0us, uint32 size)
        metadata.AddNestedType(raw, MetadataTokens.TypeDefinitionHandle 2)

    let later =
        metadata.AddTypeDefinition(
            TypeAttributes.Public,
            metadata.GetOrAddString spec.Name,
            metadata.GetOrAddString spec.Later,
            EntityHandle(),
            afterFields,
            afterMethods
        )

    let fieldData = BlobBuilder()

    for i, data in List.indexed quotations do
        let raw =
            MetadataTokens.TypeDefinitionHandle(firstRaw + List.findIndex ((=) data.Length) sizes)

        let signature = BlobBuilder()
        BlobEncoder(signature).Field().Type().Type(TypeDefinitionHandle.op_Implicit raw, true)

        let field =
            metadata.AddFieldDefinition(
                FieldAttributes.Assembly
                ||| FieldAttributes.Static
                ||| FieldAttributes.HasFieldRVA,
                metadata.GetOrAddString $"field%d{i}_1@",
                metadata.GetOrAddBlob signature
            )

        metadata.AddFieldRelativeVirtualAddress(field, fieldData.Count)
        fieldData.WriteBytes data
        fieldData.Align 8

    let laterSignature = BlobBuilder()
    BlobEncoder(laterSignature).Field().Type().Type(TypeDefinitionHandle.op_Implicit later, false)

    metadata.AddFieldDefinition(
        FieldAttributes.Public ||| FieldAttributes.Static,
        metadata.GetOrAddString "later",
        metadata.GetOrAddBlob laterSignature
    )
    |> ignore

    let il = BlobBuilder()
    let bodies = MethodBodyStreamEncoder il
    // static, no parameters, returns `spec.Returns`.
    let signature = metadata.GetOrAddBlob [| 0x00uy; 0x00uy; spec.Returns |]

    for name in spec.Methods do
        let code = InstructionEncoder(BlobBuilder())
        // A pattern-match failure names its source file in the body, as `fsc` emits it.
        code.LoadString(metadata.GetOrAddUserString(path "src/A/A1.fs" + spec.Literal))
        code.OpCode ILOpCode.Pop
        code.OpCode ILOpCode.Ret

        metadata.AddMethodDefinition(
            MethodAttributes.Public ||| MethodAttributes.Static,
            MethodImplAttributes.IL,
            metadata.GetOrAddString name,
            signature,
            bodies.AddMethodBody code,
            MetadataTokens.ParameterHandle 1
        )
        |> ignore

    if spec.OwnAttributes then
        let value = metadata.GetOrAddBlob [| 1uy; 0uy; 0uy; 0uy |]

        metadata.AddCustomAttribute(
            AssemblyDefinitionHandle.op_Implicit assembly,
            MethodDefinitionHandle.op_Implicit (MetadataTokens.MethodDefinitionHandle 1),
            value
        )
        |> ignore

        // GENERICINST CLASS <first TypeRef> 1 STRING
        let generic =
            metadata.AddTypeSpecification(metadata.GetOrAddBlob [| 0x15uy; 0x12uy; 0x05uy; 0x01uy; 0x0Euy |])

        let constructor =
            metadata.AddMemberReference(
                TypeSpecificationHandle.op_Implicit generic,
                metadata.GetOrAddString ".ctor",
                metadata.GetOrAddBlob [| 0x20uy; 0x00uy; 0x01uy |]
            )

        metadata.AddCustomAttribute(
            AssemblyDefinitionHandle.op_Implicit assembly,
            MemberReferenceHandle.op_Implicit constructor,
            value
        )
        |> ignore

    let resources = BlobBuilder()

    let addResource (name: string) (content: byte[]) =
        let offset = resources.Count
        resources.WriteInt32 content.Length
        resources.WriteBytes content
        resources.Align 8

        metadata.AddManifestResource(
            ManifestResourceAttributes.Public,
            metadata.GetOrAddString name,
            EntityHandle(),
            uint32 offset
        )
        |> ignore

    let signatureData =
        [ yield pickled "FSharp.Core"
          for source in spec.Sources -> pickled (path source)
          for entity in spec.Entities -> pickled entity
          // fsc pickles the assembly's attributes into its signature data too.
          for typeName, value in spec.Attributes do
              yield pickled ("System.Reflection." + typeName)
              yield pickled (if isNull value then "" else value)
          yield pickled spec.Root ]
        |> Array.concat

    addResource $"FSharpSignatureCompressedData.%s{spec.Name}" (deflate signatureData)
    addResource $"FSharpOptimizationCompressedData.%s{spec.Name}" (deflate (pickled (path "src/A/A2.fs")))

    for name, content in spec.Resources do
        addResource name content

    for name in spec.Linked do
        let file =
            metadata.AddAssemblyFile(metadata.GetOrAddString(name + ".bin"), metadata.GetOrAddBlob [| 1uy |], false)

        metadata.AddManifestResource(
            ManifestResourceAttributes.Public,
            metadata.GetOrAddString name,
            AssemblyFileHandle.op_Implicit file,
            0u
        )
        |> ignore

    let debug = DebugDirectoryBuilder()
    let pdb = path $"src/A/obj/Debug/net10.0/%s{spec.Name}.pdb"
    debug.AddCodeViewEntry(pdb, BlobContentId(Guid(SHA256.HashData(Encoding.UTF8.GetBytes pdb)[..15]), 0u), 0x0100us)
    debug.AddReproducibleEntry()

    let builder =
        ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            MetadataRootBuilder metadata,
            il,
            managedResources = resources,
            mappedFieldData = fieldData,
            debugDirectoryBuilder = debug,
            deterministicIdProvider =
                Func<Collections.Generic.IEnumerable<Blob>, BlobContentId>(fun content ->
                    use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256

                    for blob in content do
                        hash.AppendData(blob.GetBytes().Array, blob.GetBytes().Offset, blob.GetBytes().Count)

                    BlobContentId.FromHash(hash.GetHashAndReset()))
        )

    let image = BlobBuilder()
    let id = builder.Serialize image
    BlobWriter(mvid.Content).WriteGuid id.Guid
    image.ToArray()

/// Write the assembly to `file`.
let write (file: string) (spec: Spec) =
    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
    File.WriteAllBytes(file, build spec)
