/// An assembly's metadata tables by what each row names, not where anything sits
/// (AUTOMATION-1582, for `OutputIdentity`).
///
/// A table row names a string, blob or GUID by its offset into a heap and another row by its
/// number, so an edit anywhere in a heap moves every row after it. Hashing the raw rows made
/// the identity as checkout-dependent as the layout: `fsc` stores each quotation's pickle,
/// which names its source file's absolute path, as static data of a nested value type
/// `T_<size>Bytes@`, one per distinct size. The data's bytes were never hashed (a check reads
/// them no more than a method body), but a longer root renamed those types, re-sized them,
/// re-grouped them by size and so re-numbered every type after them, and every heap offset
/// after their names moved too.
///
/// So each column is hashed as the value it names: a heap reference by the string, blob or
/// GUID it points at; a signature as its decoded shape; a type definition by its rank among
/// the types a check can read. Those value types are left out altogether, with the rows that
/// only describe them (their layout, their nesting), and a signature that names one names
/// "raw data". The columns that are file positions are left out, as before.
module internal FsHotWatch.MetadataRows

open System
open System.Buffers.Binary
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Text
open System.Text.RegularExpressions

/// A coded index (ECMA-335 §II.24.2.6): the tables its tag selects, in tag order.
type private Coded =
    { Bits: int
      Tables: TableIndex option list }

type private Column =
    /// A constant, flags or a version part, of this many bytes.
    | Fixed of int
    /// A file position (an RVA or a resource offset): four bytes, not hashed.
    | Position
    | Str
    | Guid
    | Blob
    /// A blob that is a field, method, property, local or method-specification signature.
    | Signature
    /// A TypeSpec's blob: one type.
    | TypeSignature
    | Row of TableIndex
    | CodedIndex of Coded

let private coded bits tables =
    CodedIndex { Bits = bits; Tables = tables }

let private typeDefOrRef =
    coded 2 [ Some TableIndex.TypeDef; Some TableIndex.TypeRef; Some TableIndex.TypeSpec ]

let private hasConstant =
    coded 2 [ Some TableIndex.Field; Some TableIndex.Param; Some TableIndex.Property ]

let private hasCustomAttribute =
    coded
        5
        ([ TableIndex.MethodDef
           TableIndex.Field
           TableIndex.TypeRef
           TableIndex.TypeDef
           TableIndex.Param
           TableIndex.InterfaceImpl
           TableIndex.MemberRef
           TableIndex.Module
           TableIndex.DeclSecurity
           TableIndex.Property
           TableIndex.Event
           TableIndex.StandAloneSig
           TableIndex.ModuleRef
           TableIndex.TypeSpec
           TableIndex.Assembly
           TableIndex.AssemblyRef
           TableIndex.File
           TableIndex.ExportedType
           TableIndex.ManifestResource
           TableIndex.GenericParam
           TableIndex.GenericParamConstraint
           TableIndex.MethodSpec ]
         |> List.map Some)

let private hasFieldMarshal =
    coded 1 [ Some TableIndex.Field; Some TableIndex.Param ]

let private hasDeclSecurity =
    coded 2 [ Some TableIndex.TypeDef; Some TableIndex.MethodDef; Some TableIndex.Assembly ]

let private memberRefParent =
    coded
        3
        [ Some TableIndex.TypeDef
          Some TableIndex.TypeRef
          Some TableIndex.ModuleRef
          Some TableIndex.MethodDef
          Some TableIndex.TypeSpec ]

let private hasSemantics =
    coded 1 [ Some TableIndex.Event; Some TableIndex.Property ]

let private methodDefOrRef =
    coded 1 [ Some TableIndex.MethodDef; Some TableIndex.MemberRef ]

let private memberForwarded =
    coded 1 [ Some TableIndex.Field; Some TableIndex.MethodDef ]

let private implementation =
    coded
        2
        [ Some TableIndex.File
          Some TableIndex.AssemblyRef
          Some TableIndex.ExportedType ]

let private customAttributeType =
    coded 3 [ None; None; Some TableIndex.MethodDef; Some TableIndex.MemberRef; None ]

let private resolutionScope =
    coded
        2
        [ Some TableIndex.Module
          Some TableIndex.ModuleRef
          Some TableIndex.AssemblyRef
          Some TableIndex.TypeRef ]

let private typeOrMethodDef =
    coded 1 [ Some TableIndex.TypeDef; Some TableIndex.MethodDef ]

/// Every table's columns, ECMA-335 §II.22.
let private schema: Map<TableIndex, Column list> =
    Map.ofList
        [ TableIndex.Module, [ Fixed 2; Str; Guid; Guid; Guid ]
          TableIndex.TypeRef, [ resolutionScope; Str; Str ]
          TableIndex.TypeDef,
          [ Fixed 4
            Str
            Str
            typeDefOrRef
            Row TableIndex.Field
            Row TableIndex.MethodDef ]
          TableIndex.FieldPtr, [ Row TableIndex.Field ]
          TableIndex.Field, [ Fixed 2; Str; Signature ]
          TableIndex.MethodPtr, [ Row TableIndex.MethodDef ]
          TableIndex.MethodDef, [ Position; Fixed 2; Fixed 2; Str; Signature; Row TableIndex.Param ]
          TableIndex.ParamPtr, [ Row TableIndex.Param ]
          TableIndex.Param, [ Fixed 2; Fixed 2; Str ]
          TableIndex.InterfaceImpl, [ Row TableIndex.TypeDef; typeDefOrRef ]
          TableIndex.MemberRef, [ memberRefParent; Str; Signature ]
          TableIndex.Constant, [ Fixed 2; hasConstant; Blob ]
          TableIndex.CustomAttribute, [ hasCustomAttribute; customAttributeType; Blob ]
          TableIndex.FieldMarshal, [ hasFieldMarshal; Blob ]
          TableIndex.DeclSecurity, [ Fixed 2; hasDeclSecurity; Blob ]
          TableIndex.ClassLayout, [ Fixed 2; Fixed 4; Row TableIndex.TypeDef ]
          TableIndex.FieldLayout, [ Fixed 4; Row TableIndex.Field ]
          TableIndex.StandAloneSig, [ Signature ]
          TableIndex.EventMap, [ Row TableIndex.TypeDef; Row TableIndex.Event ]
          TableIndex.EventPtr, [ Row TableIndex.Event ]
          TableIndex.Event, [ Fixed 2; Str; typeDefOrRef ]
          TableIndex.PropertyMap, [ Row TableIndex.TypeDef; Row TableIndex.Property ]
          TableIndex.PropertyPtr, [ Row TableIndex.Property ]
          TableIndex.Property, [ Fixed 2; Str; Signature ]
          TableIndex.MethodSemantics, [ Fixed 2; Row TableIndex.MethodDef; hasSemantics ]
          TableIndex.MethodImpl, [ Row TableIndex.TypeDef; methodDefOrRef; methodDefOrRef ]
          TableIndex.ModuleRef, [ Str ]
          TableIndex.TypeSpec, [ TypeSignature ]
          TableIndex.ImplMap, [ Fixed 2; memberForwarded; Str; Row TableIndex.ModuleRef ]
          TableIndex.FieldRva, [ Position; Row TableIndex.Field ]
          TableIndex.EncLog, [ Fixed 4; Fixed 4 ]
          TableIndex.EncMap, [ Fixed 4 ]
          TableIndex.Assembly, [ Fixed 4; Fixed 2; Fixed 2; Fixed 2; Fixed 2; Fixed 4; Blob; Str; Str ]
          TableIndex.AssemblyProcessor, [ Fixed 4 ]
          TableIndex.AssemblyOS, [ Fixed 4; Fixed 4; Fixed 4 ]
          TableIndex.AssemblyRef, [ Fixed 2; Fixed 2; Fixed 2; Fixed 2; Fixed 4; Blob; Str; Str; Blob ]
          TableIndex.AssemblyRefProcessor, [ Fixed 4; Row TableIndex.AssemblyRef ]
          TableIndex.AssemblyRefOS, [ Fixed 4; Fixed 4; Fixed 4; Row TableIndex.AssemblyRef ]
          TableIndex.File, [ Fixed 4; Str; Blob ]
          TableIndex.ExportedType, [ Fixed 4; Fixed 4; Str; Str; implementation ]
          TableIndex.ManifestResource, [ Position; Fixed 4; Str; implementation ]
          TableIndex.NestedClass, [ Row TableIndex.TypeDef; Row TableIndex.TypeDef ]
          TableIndex.GenericParam, [ Fixed 2; Fixed 2; typeOrMethodDef; Str ]
          TableIndex.MethodSpec, [ methodDefOrRef; Signature ]
          TableIndex.GenericParamConstraint, [ Row TableIndex.GenericParam; typeDefOrRef ] ]

/// The rows that exist only to describe a type: left out with the type. The column is the
/// one naming it.
let private describing =
    Map.ofList [ TableIndex.ClassLayout, 2; TableIndex.NestedClass, 0 ]

/// The `HeapSizes` flags of the tables stream (ECMA-335 §II.24.2.6): which heap indexes are
/// four bytes wide. Read from the metadata root's stream headers (§II.24.2.1-2).
let private heapSizes (metadata: byte[]) =
    let int32At (offset: int) =
        BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(metadata, offset, 4))

    let versionLength = int32At 12

    let streams =
        int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(metadata, 18 + versionLength, 2)))

    let mutable header = 20 + versionLength
    let mutable found = None

    for _ in 1..streams do
        let nameStart = header + 8
        let nameEnd = Array.IndexOf(metadata, 0uy, nameStart)
        let name = Encoding.ASCII.GetString(metadata, nameStart, nameEnd - nameStart)

        if name = "#~" || name = "#-" then
            found <- Some metadata[int32At header + 6]

        header <- nameStart + ((nameEnd - nameStart + 4) &&& ~~~3)

    match found with
    | Some flags -> flags
    | None -> raise (BadImageFormatException "The metadata has no tables stream.")

/// A value type `fsc` declares only to give a quotation's or an array literal's static data
/// its size: `T_<size>Bytes@` (older compilers put a counter after the `T`), with an
/// explicit layout and no member of its own.
let private rawDataName = Regex(@"^T\d*_\d+Bytes@$", RegexOptions.Compiled)

let private isRawData (reader: MetadataReader) (handle: TypeDefinitionHandle) =
    let definition = reader.GetTypeDefinition handle

    definition.Attributes &&& TypeAttributes.ExplicitLayout = TypeAttributes.ExplicitLayout
    && definition.GetFields().Count = 0
    && definition.GetMethods().Count = 0
    && rawDataName.IsMatch(reader.GetString definition.Name)

/// Each type definition's number among those a check can read, by row; -1 for raw data.
let private ranks (reader: MetadataReader) =
    let ranks = Array.create (reader.GetTableRowCount TableIndex.TypeDef + 1) -1
    let mutable next = 0

    for handle in reader.TypeDefinitions do
        if not (isRawData reader handle) then
            ranks[MetadataTokens.GetRowNumber handle] <- next
            next <- next + 1

    ranks

let private methodShape (signature: MethodSignature<string>) =
    let mutable header = signature.Header
    $"m%d{header.RawValue}:%d{signature.GenericParameterCount}:%d{signature.RequiredParameterCount}:%s{signature.ReturnType}(%s{String.Join(',', signature.ParameterTypes)})"

let private typeName (ranks: int[]) (row: int) =
    if ranks[row] < 0 then "raw" else string ranks[row]

/// A signature's shape, every type definition named by rank.
let private shapes (ranks: int[]) =
    { new ISignatureTypeProvider<string, unit> with
        member _.GetFunctionPointerType signature = $"fn(%s{methodShape signature})"

        member _.GetGenericMethodParameter(_, index) = $"!!%d{index}"
        member _.GetGenericTypeParameter(_, index) = $"!%d{index}"

        member _.GetModifiedType(modifier, unmodified, isRequired) =
            $"mod%b{isRequired}(%s{modifier})(%s{unmodified})"

        member _.GetPinnedType element = $"pinned(%s{element})"

        member _.GetTypeFromSpecification(_, _, handle, kind) =
            $"spec%d{kind}:%d{MetadataTokens.GetRowNumber handle}"
      interface IConstructedTypeProvider<string> with
          member _.GetArrayType(element, shape) =
              $"[%s{element};%d{shape.Rank};%s{String.Join(',', shape.Sizes)};%s{String.Join(',', shape.LowerBounds)}]"

          member _.GetByReferenceType element = $"&(%s{element})"

          member _.GetGenericInstantiation(generic, arguments) =
              $"%s{generic}<%s{String.Join(',', arguments)}>"

          member _.GetPointerType element = $"*(%s{element})"
      interface ISZArrayTypeProvider<string> with
          member _.GetSZArrayType element = $"[%s{element}]"
      interface ISimpleTypeProvider<string> with
          member _.GetPrimitiveType code = $"p%d{int code}"

          member _.GetTypeFromDefinition(_, handle, kind) =
              $"def%d{kind}:%s{typeName ranks (MetadataTokens.GetRowNumber handle)}"

          member _.GetTypeFromReference(_, handle, kind) =
              $"ref%d{kind}:%d{MetadataTokens.GetRowNumber handle}" }

/// The signature in blob `index` as its shape; its bytes, marked, when it is not one the
/// decoder can read.
let private signature (reader: MetadataReader) (decoder: SignatureDecoder<string, unit>) (isType: bool) (index: int) =
    let handle = MetadataTokens.BlobHandle index
    let mutable blob = reader.GetBlobReader handle

    try
        let shape =
            if isType then
                "t" + decoder.DecodeType(&blob, false)
            else
                let mutable peek = blob

                let mutable header = peek.ReadSignatureHeader()

                match header.Kind with
                | SignatureKind.Field -> "f" + decoder.DecodeFieldSignature(&blob)
                | SignatureKind.Method
                | SignatureKind.Property -> methodShape (decoder.DecodeMethodSignature(&blob))
                | SignatureKind.LocalVariables -> $"l(%s{String.Join(',', decoder.DecodeLocalSignature(&blob))})"
                | SignatureKind.MethodSpecification ->
                    $"i(%s{String.Join(',', decoder.DecodeMethodSpecificationSignature(&blob))})"
                | _ -> raise (BadImageFormatException "Not a signature.")

        // Anything after the signature still counts.
        let rest = blob.ReadBytes(blob.RemainingBytes)
        Encoding.UTF8.GetBytes(shape + ":" + Convert.ToHexString rest)
    with :? BadImageFormatException ->
        Array.append "b:"B (reader.GetBlobBytes handle)

/// Blob `index` of `blobs`, the #Blob heap as the identity hashes it: its compressed length
/// (§II.24.2.4), then the bytes.
let private blobAt (blobs: byte[]) (index: int) =
    let first = int blobs[index]

    let start, length =
        if first &&& 0x80 = 0 then
            index + 1, first
        elif first &&& 0xC0 = 0x80 then
            index + 2, ((first &&& 0x3F) <<< 8) ||| int blobs[index + 1]
        else
            index + 4,
            ((first &&& 0x1F) <<< 24)
            ||| (int blobs[index + 1] <<< 16)
            ||| (int blobs[index + 2] <<< 8)
            ||| int blobs[index + 3]

    blobs.AsSpan(start, length).ToArray()

/// `metadata`'s tables by value, one `(label, bytes)` per table with rows, in table order.
/// `strings`, `blobs` and `guids` are its heaps as the identity hashes them (a version's
/// build metadata masked, the module version id zeroed).
let tables (reader: MetadataReader) (metadata: byte[]) (strings: byte[]) (blobs: byte[]) (guids: byte[]) =
    let flags = heapSizes metadata
    let wide (flag: byte) = if flags &&& flag <> 0uy then 4 else 2
    let ranks = ranks reader
    let decoder = SignatureDecoder<string, unit>(shapes ranks, reader, ())

    let width (column: Column) =
        match column with
        | Fixed n -> n
        | Position -> 4
        | Str -> wide 1uy
        | Guid -> wide 2uy
        | Blob
        | Signature
        | TypeSignature -> wide 4uy
        | Row table -> if reader.GetTableRowCount table < 0x10000 then 2 else 4
        | CodedIndex c ->
            let most =
                c.Tables
                |> List.choose id
                |> List.map reader.GetTableRowCount
                |> List.fold max 0

            if most < (1 <<< (16 - c.Bits)) then 2 else 4

    /// A type definition by rank: -1 for raw data.
    let rank (row: int) =
        if row > 0 && row < ranks.Length then ranks[row] else row

    let read (offset: int) (size: int) =
        if size = 2 then
            int (BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan(metadata, offset, 2)))
        else
            BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(metadata, offset, 4))

    [ for KeyValue(table, columns) in schema do
          let rows = reader.GetTableRowCount table

          if rows > 0 then
              let columns = Array.ofList columns
              let widths = columns |> Array.map width
              let offsets = Array.scan (+) 0 widths
              let rowSize = reader.GetTableRowSize table

              if offsets[columns.Length] <> rowSize then
                  raise (BadImageFormatException $"Table %O{table} rows are not %d{rowSize} bytes.")

              let start = reader.GetTableMetadataOffset table
              use out = new MemoryStream()
              use writer = new BinaryWriter(out)

              let writeBytes (bytes: byte[]) =
                  writer.Write bytes.Length
                  writer.Write bytes

              for row in 1..rows do
                  let at = start + (row - 1) * rowSize
                  let values = Array.init columns.Length (fun i -> read (at + offsets[i]) widths[i])

                  let rawData =
                      if table = TableIndex.TypeDef then
                          rank row < 0
                      else
                          match describing.TryFind table with
                          | Some column -> rank values[column] < 0
                          | None -> false

                  if not rawData then
                      for i in 0 .. columns.Length - 1 do
                          let value = values[i]

                          match columns[i] with
                          | Fixed size -> writer.Write(metadata, at + offsets[i], size)
                          | Position -> ()
                          | Str ->
                              writeBytes (strings.AsSpan(value, Array.IndexOf(strings, 0uy, value) - value).ToArray())
                          | Guid when value = 0 -> writeBytes [||]
                          | Guid -> writeBytes (guids.AsSpan((value - 1) * 16, 16).ToArray())
                          | Blob -> writeBytes (blobAt blobs value)
                          | Signature
                          | TypeSignature when value = 0 -> writeBytes [||]
                          | Signature -> writeBytes (signature reader decoder false value)
                          | TypeSignature -> writeBytes (signature reader decoder true value)
                          | Row TableIndex.TypeDef -> writer.Write(rank value)
                          | Row _ -> writer.Write value
                          | CodedIndex c ->
                              let tag = value &&& ((1 <<< c.Bits) - 1)
                              let target = value >>> c.Bits
                              writer.Write tag

                              match List.tryItem tag c.Tables with
                              | Some(Some TableIndex.TypeDef) -> writer.Write(rank target)
                              | _ -> writer.Write target

              writer.Flush()
              yield $"table %O{table}", out.ToArray() ]
