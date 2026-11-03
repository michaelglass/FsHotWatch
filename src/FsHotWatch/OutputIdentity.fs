/// A referenced project's built output, named by what a check reads of it and not by the
/// checkout it was built in (AUTOMATION-1478, ADR-059 §2).
///
/// An F# compiler output embeds its checkout's absolute path: the PDB path in the debug
/// directory, the source file of every range in the F# signature data (compressed, so the
/// bytes after it move too), and a module version id and PE timestamp derived from all of
/// that. The same sources built in two checkouts therefore never have the same bytes, and a
/// check record keyed on those bytes speaks for one checkout only.
///
/// The identity hashes what FCS reads when it types a dependent against the output:
///   * the metadata tables, with the columns that are file positions zeroed (a method's or
///     field's RVA, a resource's offset);
///   * the `#Strings`, `#Blob` and `#GUID` heaps, the module version id zeroed;
///   * every manifest resource by name, an F# one inflated where it is compressed, with
///     each occurrence of the checkout root replaced by a placeholder and the length that
///     precedes it in the F# pickle re-stated without the root.
/// It does not hash method bodies, the `#US` heap they alone read, or the PE and debug
/// headers: a check reads none of them, and each carries a path or a position that moves
/// with one. A file that is not a managed assembly keeps its plain content hash.
module FsHotWatch.OutputIdentity

open System
open System.Collections.Concurrent
open System.IO
open System.IO.Compression
open System.Reflection.Metadata
open System.Reflection.Metadata.Ecma335
open System.Reflection.PortableExecutable
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// The identity's own format: bumping it re-keys every record that names an output.
[<Literal>]
let Schema = "pe-v1"

/// Stands for the checkout root inside an F# resource. NUL-delimited, so no path or
/// identifier the compiler pickles contains it.
let private placeholder = Encoding.UTF8.GetBytes "\u0000${repo}\u0000"

/// Marks where a pickled length was re-stated, so a re-stated length is never read as the
/// bytes it replaced.
let private restated = Encoding.UTF8.GetBytes "\u0000${len}"

let private indicesOf (haystack: byte[]) (needle: byte[]) =
    let found = Collections.Generic.List<int>()
    let span = ReadOnlySpan<byte> haystack
    let mutable start = 0
    let mutable searching = needle.Length > 0

    while searching do
        let at = span.Slice(start).IndexOf(ReadOnlySpan<byte> needle)

        if at < 0 then
            searching <- false
        else
            found.Add(start + at)
            start <- start + at + needle.Length

    found

/// The pickled length in front of a root occurrence at `at`, as the F# pickler writes an
/// int (`p_int32`): one byte up to 0x7F, two bytes `0x80 ||| n >>> 8; n &&& 0xFF` up to
/// 0x3FFF, else `0xFF` and four little-endian bytes. Returns where the length starts and
/// its value, or None when the bytes in front of it cannot be a length that covers the
/// root (the root sits inside a longer string).
let private pickledLengthBefore (data: byte[]) (after: int) (at: int) (rootLength: int) =
    let fits (start: int) (length: int) =
        start >= after && length >= rootLength && at + length <= data.Length

    let two =
        if at >= 2 && data[at - 2] &&& 0xC0uy = 0x80uy then
            let length = (int (data[at - 2] &&& 0x3Fuy) <<< 8) ||| int data[at - 1]

            if length >= 0x80 && fits (at - 2) length then
                Some(at - 2, length)
            else
                None
        else
            None

    let one =
        if at >= 1 && data[at - 1] < 0x80uy && fits (at - 1) (int data[at - 1]) then
            Some(at - 1, int data[at - 1])
        else
            None

    let five =
        if at >= 5 && data[at - 5] = 0xFFuy then
            let length = BitConverter.ToInt32(data, at - 4)

            if length > 0x3FFF && fits (at - 5) length then
                Some(at - 5, length)
            else
                None
        else
            None

    match two, one, five with
    | Some found, _, _
    | None, Some found, _
    | None, None, Some found -> Some found
    | None, None, None -> None

/// `data` with every occurrence of `root` replaced by the placeholder, and the pickled
/// length in front of each one re-stated as the length of what follows the root.
let internal normalizeResource (root: byte[]) (data: byte[]) : byte[] =
    let occurrences = indicesOf data root

    if occurrences.Count = 0 || root.Length = 0 then
        data
    else
        use out = new MemoryStream(data.Length)
        let mutable copied = 0

        for at in occurrences do
            match pickledLengthBefore data copied at root.Length with
            | Some(start, length) ->
                out.Write(data, copied, start - copied)
                out.Write(restated, 0, restated.Length)
                out.Write(BitConverter.GetBytes(length - root.Length), 0, 4)
            | None -> out.Write(data, copied, at - copied)

            out.Write(placeholder, 0, placeholder.Length)
            copied <- at + root.Length

        out.Write(data, copied, data.Length - copied)
        out.ToArray()

/// The metadata-table columns that are file positions, each the first four bytes of its
/// row: they move whenever anything before the method bodies, field data or resources does.
/// The type whose assembly-level value carries build metadata: SourceLink appends
/// `+<commit id>` to it on a git checkout (AUTOMATION-1480).
[<Literal>]
let private InformationalVersion =
    "System.Reflection.AssemblyInformationalVersionAttribute"

let private attributeTypeName (reader: MetadataReader) (attribute: CustomAttribute) =
    if attribute.Constructor.Kind <> HandleKind.MemberReference then
        None
    else
        let parent =
            reader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor).Parent

        if parent.Kind <> HandleKind.TypeReference then
            None
        else
            let typeRef = reader.GetTypeReference(TypeReferenceHandle.op_Explicit parent)
            Some(reader.GetString typeRef.Namespace + "." + reader.GetString typeRef.Name)

/// `blobs` (the #Blob heap) with the build metadata of the assembly's informational version
/// zeroed in place: everything from its `+` to the end of the string. No check reads that
/// attribute (FCS imports only the assembly attributes it acts on, such as
/// InternalsVisibleTo and AutoOpen), and a commit id is the same length at every commit,
/// so no blob moves. The version before the `+` still counts. Returns each version masked,
/// as UTF-8 before and after: fsc also pickles the assembly's attributes into its F#
/// signature data, where `resourceContent` masks the same text.
let private maskBuildMetadata (reader: MetadataReader) (blobs: byte[]) : (byte[] * byte[]) list =
    let isVersion (attribute: CustomAttribute) =
        attribute.Parent.Kind = HandleKind.AssemblyDefinition
        && attributeTypeName reader attribute = Some InformationalVersion

    // Scanned, not looked up by parent: on fsc's output `AssemblyDefinition.GetCustomAttributes`
    // returns none of the assembly's attributes (measured on an SDK 10.0.401 build).
    [ for handle in reader.CustomAttributes do
          let attribute = reader.GetCustomAttribute handle

          if isVersion attribute then
              let mutable value = reader.GetBlobReader attribute.Value
              value.ReadUInt16() |> ignore
              let text = value.ReadSerializedString()
              let plus = if isNull text then -1 else text.IndexOf '+'

              if plus >= 0 then
                  let utf8 = Encoding.UTF8.GetBytes text
                  let metadata = utf8.Length - Encoding.UTF8.GetByteCount(text.Substring(0, plus))
                  // The string ends where the reader stopped; its bytes run back from there.
                  let start = value.Offset - metadata
                  let length = reader.GetBlobBytes(attribute.Value).Length
                  // The blob's own compressed length precedes it in the heap.
                  let prefix =
                      if length < 0x80 then 1
                      elif length < 0x4000 then 2
                      else 4

                  Array.fill blobs (MetadataTokens.GetHeapOffset attribute.Value + prefix + start) metadata 0uy
                  let masked = Array.copy utf8
                  Array.fill masked (utf8.Length - metadata) metadata 0uy
                  yield utf8, masked ]

/// `data` with every occurrence of each original replaced by its same-length mask.
let private maskAll (versions: (byte[] * byte[]) list) (data: byte[]) =
    let data = Array.copy data

    for original, masked in versions do
        for at in indicesOf data original do
            Array.blit masked 0 data at masked.Length

    data

let private positionTables =
    set [ TableIndex.MethodDef; TableIndex.FieldRva; TableIndex.ManifestResource ]

let private inflate (bytes: byte[]) =
    use input = new MemoryStream(bytes)
    use deflate = new DeflateStream(input, CompressionMode.Decompress)
    use output = new MemoryStream()
    deflate.CopyTo output
    output.ToArray()

let private isFSharpResource (name: string) =
    name.StartsWith("FSharpSignature", StringComparison.Ordinal)
    || name.StartsWith("FSharpOptimization", StringComparison.Ordinal)

/// What a resource of the assembly's own contributes: an F# one inflated where it is
/// compressed and normalized under each root, longest first; any other as it is.
let private resourceContent (roots: byte[] list) (versions: (byte[] * byte[]) list) (name: string) (raw: byte[]) =
    if not (isFSharpResource name) then
        raw
    else
        let data =
            if name.Contains("Compressed", StringComparison.Ordinal) then
                inflate raw
            else
                raw

        roots
        |> List.fold (fun data root -> normalizeResource root data) data
        |> maskAll versions

let private appendLabelled (hash: IncrementalHash) (label: string) (bytes: ReadOnlySpan<byte>) =
    hash.AppendData(Encoding.UTF8.GetBytes $"\n%s{label}:%d{bytes.Length}:")
    hash.AppendData bytes

/// The checkout-independent identity of a managed assembly's bytes, with `roots` the
/// spellings of the checkout the paths inside it start with; None for bytes that are not
/// one. The longest spelling is replaced first, so a shorter one inside it (`/var/x` in
/// `/private/var/x`) is never replaced in part.
let ofBytes (roots: string list) (bytes: byte[]) : string option =
    try
        use pe = new PEReader(Collections.Immutable.ImmutableArray.Create<byte> bytes)

        if not pe.HasMetadata then
            None
        else
            let reader = pe.GetMetadataReader()
            let metadata = pe.GetMetadata().GetContent()

            let slice (offset: int) (length: int) =
                metadata.AsSpan().Slice(offset, length).ToArray()

            use hash = IncrementalHash.CreateHash HashAlgorithmName.SHA256
            hash.AppendData(Encoding.UTF8.GetBytes Schema)

            for table in Enum.GetValues<TableIndex>() |> Array.distinct do
                let rows = reader.GetTableRowCount table

                if rows > 0 then
                    let rowSize = reader.GetTableRowSize table
                    let offset = reader.GetTableMetadataOffset table
                    let content = slice offset (rows * rowSize)

                    if positionTables.Contains table then
                        for row in 0 .. rows - 1 do
                            Array.fill content (row * rowSize) 4 0uy

                    appendLabelled hash $"table %O{table} %d{rowSize}" (ReadOnlySpan content)

            let heap (index: HeapIndex) =
                slice (reader.GetHeapMetadataOffset index) (reader.GetHeapSize index)

            appendLabelled hash "strings" (ReadOnlySpan(heap HeapIndex.String))
            let blobs = heap HeapIndex.Blob
            let versions = maskBuildMetadata reader blobs
            appendLabelled hash "blob" (ReadOnlySpan blobs)
            let guids = heap HeapIndex.Guid
            let mvid = reader.GetModuleDefinition().Mvid

            if not mvid.IsNil then
                let index = MetadataTokens.GetHeapOffset(GuidHandle.op_Implicit mvid)
                Array.fill guids ((index - 1) * 16) 16 0uy

            appendLabelled hash "guid" (ReadOnlySpan guids)

            let rootBytes =
                roots
                |> List.map (fun root -> Encoding.UTF8.GetBytes(root.TrimEnd(Path.DirectorySeparatorChar)))
                |> List.distinct
                |> List.sortByDescending _.Length

            let resources = pe.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress

            for handle in reader.ManifestResources do
                let resource = reader.GetManifestResource handle
                let name = reader.GetString resource.Name

                let content =
                    if resource.Implementation.IsNil then
                        let mutable blob = pe.GetSectionData(resources + int resource.Offset).GetReader()
                        resourceContent rootBytes versions name (blob.ReadBytes(blob.ReadInt32()))
                    else
                        [||]

                appendLabelled hash $"resource %s{name}" (ReadOnlySpan content)

            Some(Schema + ":" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant())
    with
    | :? BadImageFormatException
    | :? InvalidOperationException
    | :? InvalidDataException
    | :? ArgumentException
    | :? IOException -> None

/// Identities already computed, by the plain content hash of the bytes and the root they
/// were read under: a walk names each output once per project that references it.
let private known = ConcurrentDictionary<struct (string * string), string>()

[<Literal>]
let private KnownLimit = 4096

/// The spellings a build may have embedded `root` by: as the daemon names it, and with every
/// symbolic link resolved. A build's working directory is the resolved one (`getcwd`), so
/// on macOS a checkout under `/var` or `/tmp` is embedded as `/private/var` or `/private/tmp`.
let private spellingsOf (root: string) =
    [ yield root
      match RepositoryIdentity.canonicalize root with
      | Ok canonical -> yield canonical.Value.TrimEnd(Path.DirectorySeparatorChar)
      | Error _ -> () ]

/// The identity a check record names `path` by: `ofBytes` of its bytes under `repoRoot`
/// when it is a managed assembly whose bytes are the ones `hashFile` names, else
/// `hashFile`'s own answer. Without a root there is no checkout to be independent of.
let identity (repoRoot: string option) (hashFile: string -> string) (path: string) : string =
    let plain = hashFile path

    match repoRoot with
    | None -> plain
    | Some _ when plain = "missing" || plain = "unreadable" -> plain
    | Some root ->
        let root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)

        match known.TryGetValue(struct (plain, root)) with
        | true, named -> named
        | false, _ ->
            let bytes =
                try
                    Some(File.ReadAllBytes path)
                with
                | :? IOException
                | :? UnauthorizedAccessException -> None

            match bytes with
            // Moved since `hashFile` read it: name it by the plain hash, as before.
            | Some bytes when Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant() = plain ->
                match ofBytes (spellingsOf root) bytes with
                | Some named ->
                    if known.Count >= KnownLimit then
                        known.Clear()

                    known[struct (plain, root)] <- named
                    named
                | None -> plain
            | _ -> plain

/// The build metadata (`+…`) of an informational-version argument in F# source.
let private buildMetadataArgument =
    Regex("""(AssemblyInformationalVersion(?:Attribute)?\s*\(\s*"[^"+]*)\+[^"]*(")""", RegexOptions.Compiled)

let private generatedKnown = ConcurrentDictionary<string, string>()

/// The identity a check record's chain names a build-generated compile item by
/// (AUTOMATION-1480). On a git checkout the SDK's SourceLink writes the commit id into the
/// generated AssemblyInfo (`AssemblyInformationalVersion("1.0.0+<commit id>")`), and F#
/// compiles that file before every source of the project, so the id would re-key every record
/// of every project at every commit. No check can see it: the file declares nothing a later
/// file can name, and a string argument cannot change its own diagnostics. So its content is
/// hashed with that build metadata removed. Anything else in the file still counts, and a file
/// with no such argument keeps `hashFile`'s answer.
let generatedSource (hashFile: string -> string) (path: string) : string =
    let plain = hashFile path

    match generatedKnown.TryGetValue plain with
    | true, named -> named
    | false, _ ->
        let bytes =
            try
                Some(File.ReadAllBytes path)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> None

        match bytes with
        // Moved since `hashFile` read it: name it by the plain hash, as before.
        | Some bytes when Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant() = plain ->
            let text = Encoding.UTF8.GetString bytes
            let canonical = buildMetadataArgument.Replace(text, "$1$2")

            let named =
                if canonical = text then
                    plain
                else
                    "generated-v1:"
                    + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes canonical)).ToLowerInvariant()

            if generatedKnown.Count >= KnownLimit then
                generatedKnown.Clear()

            generatedKnown[plain] <- named
            named
        | _ -> plain
