/// fixtures: the repository's own house-rules analyzer, relocated into
/// temp checkouts and byte-patched to look like what fsc produces there.
///
/// A second `dotnet build` per test is what these stand in for. What fsc changes
/// between two checkouts of identical source is ONE thing — the PDB path it writes
/// into the DLL's CodeView entry — so `saltCodeViewPath` rewrites exactly that, in
/// place and at equal length. What fsc changes when a SOURCE changes is that
/// document's checksum in the PDB, so `rewriteSource` writes the new source and
/// patches exactly that blob. Both patches are unique-match, so a fixture that no
/// longer matches the compiler's output fails loudly instead of testing nothing.
module FsHotWatch.Tests.AnalyzerFixtures

open System
open System.Collections.Immutable
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Security.Cryptography

let repoRoot = RepoTasks.repoRoot ()

/// Repository-relative location of the house-rules build output, as `.fshw.json`
/// `analyzers.paths` names it.
let rulesBinRel =
    Path.Combine("analyzers", "FsHotWatch.Rules", "bin", "Debug", "net10.0")

let rulesProjectRel =
    Path.Combine("analyzers", "FsHotWatch.Rules", "FsHotWatch.Rules.fsproj")

let rulesSourceRel =
    Path.Combine("analyzers", "FsHotWatch.Rules", "ConventionAnalyzers.fs")

/// The built house-rules DLL. `analyzers/FsHotWatch.Rules` is in the solution, so the
/// `dotnet build` that produced this test host produced it too.
let rulesDll =
    let dll = Path.Combine(repoRoot, rulesBinRel, "FsHotWatch.ConventionAnalyzers.dll")

    if not (File.Exists dll) then
        failwith $"house-rules analyzer not built at %s{dll}: run `mise run build-analyzers`"

    dll

let rulesPdb = Path.ChangeExtension(rulesDll, ".pdb")

let sha256Lower (bytes: byte array) =
    SHA256.HashData bytes |> Convert.ToHexStringLower

/// Every (recorded path, recorded checksum) in a portable PDB's Document table.
let pdbDocuments (pdb: string) : (string * byte array) list =
    use stream = File.OpenRead pdb
    use provider = MetadataReaderProvider.FromPortablePdbStream stream
    let reader = provider.GetMetadataReader()

    reader.Documents
    |> Seq.map (fun handle ->
        let document = reader.GetDocument handle
        reader.GetString document.Name, reader.GetBlobBytes document.Hash)
    |> Seq.toList

/// The recorded documents that lie under the repository root — the ones the
/// identity is built from and the ones a relocated copy must carry.
let inRepoDocuments (pdb: string) : string list =
    pdbDocuments pdb
    |> List.map fst
    |> List.filter (fun path ->
        path.StartsWith(repoRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal))

let private copyTo (target: string) (source: string) =
    Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
    File.Copy(source, target, true)

/// Rebuild the house-rules layout under `root`: DLL + PDB in the bin directory, the
/// producer fsproj, and every in-repo document at its repository-relative path — the
/// shape a second checkout has after building the same source. Timestamps are set
/// explicitly so the copy cannot trip the mtime refusal by accident. Returns the
/// relocated DLL's path.
let relocate (root: string) : string =
    let relTo (absolute: string) =
        Path.GetRelativePath(repoRoot, absolute)

    for source in
        [ rulesDll; rulesPdb; Path.Combine(repoRoot, rulesProjectRel) ]
        @ inRepoDocuments rulesPdb do
        copyTo (Path.Combine(root, relTo source)) source

    let dll = Path.Combine(root, relTo rulesDll)
    let stamp = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    File.SetLastWriteTimeUtc(Path.Combine(root, rulesProjectRel), stamp)
    File.SetLastWriteTimeUtc(dll, stamp.AddHours 1.0)
    dll

/// Replace the single occurrence of `needle` in `file` with `replacement` (equal
/// length, so every offset in the file survives). Anything but exactly one match is
/// a fixture that has stopped describing the compiler's output.
let private patchUnique (file: string) (needle: byte array) (replacement: byte array) =
    if needle.Length <> replacement.Length then
        failwith "a patch must keep its length"

    let bytes = File.ReadAllBytes file
    let span = ReadOnlySpan<byte>(bytes)
    let first = span.IndexOf(ReadOnlySpan<byte>(needle))

    if first < 0 then
        failwith $"pattern not found in %s{file}"

    if span.Slice(first + 1).IndexOf(ReadOnlySpan<byte>(needle)) >= 0 then
        failwith $"pattern is not unique in %s{file}"

    Array.blit replacement 0 bytes first replacement.Length
    File.WriteAllBytes(file, bytes)

/// Make `dll` what fsc would have written in ANOTHER checkout: the same code, the
/// same PDB id, a different absolute PDB path in the CodeView entry (case-flipped
/// letters keep the UTF-8 length). The bytes differ; the receipt does not.
let saltCodeViewPath (dll: string) =
    let recorded =
        use pe =
            new PEReader(ImmutableArray.Create<byte>(File.ReadAllBytes dll: byte array))

        pe.ReadDebugDirectory()
        |> Seq.find (fun entry -> entry.Type = DebugDirectoryEntryType.CodeView)
        |> pe.ReadCodeViewDebugDirectoryData
        |> fun cv -> cv.Path

    let salted =
        recorded
        |> String.map (fun c ->
            if Char.IsUpper c then Char.ToLowerInvariant c
            elif Char.IsLower c then Char.ToUpperInvariant c
            else c)

    let utf8 (text: string) = Text.Encoding.UTF8.GetBytes text
    patchUnique dll (Array.append (utf8 recorded) [| 0uy |]) (Array.append (utf8 salted) [| 0uy |])

/// Make the relocated checkout under `root` one whose house rules were BUILT from
/// `newSource`: write the source and record its checksum where fsc would have — the
/// document's hash blob in the PDB.
let rewriteSource (root: string) (newSource: string) =
    let source = Path.Combine(root, rulesSourceRel)
    let pdb = Path.Combine(root, rulesBinRel, "FsHotWatch.ConventionAnalyzers.pdb")

    let recorded =
        pdbDocuments pdb
        |> List.find (fun (path, _) -> path.EndsWith("ConventionAnalyzers.fs", StringComparison.Ordinal))
        |> snd

    let bytes = Text.Encoding.UTF8.GetBytes newSource
    File.WriteAllBytes(source, bytes)
    patchUnique pdb recorded (SHA256.HashData bytes)

/// Make the relocated checkout under `root` one whose PDB records its document
/// checksums under `algorithm` instead of SHA-256 — the compiler's `HashAlgorithm`
/// column, which lives in the PDB's GUID heap. fsc writes SHA-256 today; a receipt in
/// another algorithm is what an older or foreign toolchain leaves behind.
let recordChecksumAlgorithm (root: string) (algorithm: Guid) =
    let pdb = Path.Combine(root, rulesBinRel, "FsHotWatch.ConventionAnalyzers.pdb")
    let sha256 = Guid "8829d00f-11b8-4213-878b-770e8597ac16"
    patchUnique pdb (sha256.ToByteArray()) (algorithm.ToByteArray())
