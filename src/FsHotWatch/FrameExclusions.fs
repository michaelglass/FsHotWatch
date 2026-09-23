/// Why a project must be checked at its own paths rather than under the virtual root.
///
/// Checking under the virtual root is correct when nothing FCS computes depends on where
/// the worktree is. A project that reads its own location does:
///
/// - `__SOURCE_DIRECTORY__` and `__SOURCE_FILE__` become string literals of the path the
///   file is checked under. They can reach a format string or a literal and change
///   what typechecks, and FCS's cache key does not include them.
/// - A `#line` directive renames ranges through a process-global table.
/// - A type provider resolves files against the project's directory.
/// - `--version:@file`, `--load`, `--use` and response files are read relative to the
///   project's directory.
///
/// Such a project, and through its output path every project that references it, is
/// checked where it is.
module FsHotWatch.FrameExclusions

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Text.RegularExpressions
open FSharp.Compiler.CodeAnalysis

/// A construct that keeps a project at its own paths, and where it was found.
[<NoComparison>]
type Exclusion = { Construct: string; Where: string }

module Exclusion =
    /// For the log: `__SOURCE_DIRECTORY__ in src/Build.fs`.
    let describe (exclusion: Exclusion) : string =
        $"%s{exclusion.Construct} in %s{exclusion.Where}"

let private sourceIdentifiers = [ "__SOURCE_DIRECTORY__"; "__SOURCE_FILE__" ]

/// `#line 12`, `# 12 "file"`: a line directive at the start of a line.
let private lineDirective =
    Regex(@"^[ \t]*#[ \t]*(line[ \t]+)?[0-9]+", RegexOptions.Multiline)

/// The first construct in `text` that makes a check depend on its path.
let constructIn (text: string) : string option =
    match
        sourceIdentifiers
        |> List.tryFind (fun token -> text.Contains(token, StringComparison.Ordinal))
    with
    | Some token -> Some token
    | None when lineDirective.IsMatch text -> Some "#line"
    | None -> None

/// Options read relative to the project's directory.
let private optionReadsFromProjectDirectory (option: string) =
    option.StartsWith("--version:@", StringComparison.Ordinal)
    || option.StartsWith("--load:", StringComparison.Ordinal)
    || option.StartsWith("--use:", StringComparison.Ordinal)
    || option.StartsWith("@", StringComparison.Ordinal)

/// Whether the assembly at `path` declares itself a type provider. Read from its
/// metadata, once per file and write time.
let private typeProviderCache = ConcurrentDictionary<string * DateTime, bool>()

/// The type an assembly-level attribute constructs, by name: `""` when its constructor
/// is not a reference to another assembly's type (the assembly defines the attribute
/// itself, or the type is a generic instantiation).
let private attributeTypeName (metadata: MetadataReader) (handle: CustomAttributeHandle) =
    let attribute = metadata.GetCustomAttribute handle

    match attribute.Constructor.Kind with
    | HandleKind.MemberReference ->
        let parent =
            metadata.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor).Parent

        match parent.Kind with
        | HandleKind.TypeReference ->
            metadata.GetString(metadata.GetTypeReference(TypeReferenceHandle.op_Explicit parent).Name)
        | _ -> ""
    | _ -> ""

let private isProviderAttribute (name: string) =
    String.Equals(name, "TypeProviderAssemblyAttribute", StringComparison.Ordinal)

let private isTypeProviderAssembly (path: string) =
    let read () =
        try
            using (File.OpenRead path) (fun stream ->
                using (new PEReader(stream)) (fun pe ->
                    // Throws for a file with no metadata, which is then no provider.
                    let metadata = pe.GetMetadataReader()

                    metadata.GetAssemblyDefinition().GetCustomAttributes()
                    |> List.ofSeq
                    |> List.map (attributeTypeName metadata)
                    |> List.exists isProviderAttribute))
        with _ ->
            false

    File.Exists path
    && typeProviderCache.GetOrAdd((path, File.GetLastWriteTimeUtc path), fun _ -> read ())

/// Why `options`' project must be checked at its own paths, if it must. `readSource`
/// reads a source file.
let exclusionFor (readSource: string -> string) (options: FSharpProjectOptions) : Exclusion option =
    let fromSources () =
        options.SourceFiles
        |> Seq.tryPick (fun path ->
            let text =
                try
                    readSource path
                with _ ->
                    ""

            constructIn text
            |> Option.map (fun construct -> { Construct = construct; Where = path }))

    let fromOptions () =
        options.OtherOptions
        |> Seq.tryFind optionReadsFromProjectDirectory
        |> Option.map (fun option ->
            { Construct = option.Split(':')[0]
              Where = options.ProjectFileName })

    let fromReferences () =
        options.OtherOptions
        |> Seq.filter (fun o -> o.StartsWith("-r:", StringComparison.Ordinal))
        |> Seq.map (fun o -> o.Substring 3)
        |> Seq.tryFind isTypeProviderAssembly
        |> Option.map (fun path ->
            { Construct = "a type provider"
              Where = path })

    fromSources ()
    |> Option.orElseWith fromOptions
    |> Option.orElseWith fromReferences
