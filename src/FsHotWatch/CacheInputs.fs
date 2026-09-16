/// Cache-key inputs that live OUTSIDE the file a plugin is judging, but decide its verdict.
///
/// A per-file key that names only the file's own bytes replays across two kinds of
/// change it cannot see: configuration a tool discovers by walking up
/// from the file (`.editorconfig`, `fsharplint.json`), and the other sources a type
/// check of the file read. Both are gathered here as CONTENT named repo-relatively, so
/// two checkouts of the same repository still share an entry.
module FsHotWatch.CacheInputs

open System
open System.IO
open FSharp.Compiler.CodeAnalysis

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

/// Content of one source, or None when it cannot be read — the caller then has no key.
let private tryHashSource (path: string) : string option =
    try
        Some(CheckCache.sha256Hex (File.ReadAllText path))
    with _ ->
        None

/// Collect `Some` values in order, or None if any is None.
let private allSome (items: 'a option list) : 'a list option =
    List.foldBack (fun item acc -> Option.map2 (fun x xs -> x :: xs) item acc) items (Some [])

/// A hash of every source a type check of `file` under `options` can depend on:
///
///  • the project's compiler options, named repo-relatively;
///  • each of the project's sources BEFORE `file`, in compile order — F# resolves names
///    only backwards, so a later file cannot change this file's check. `file` itself is
///    not read: the source that was checked is already the caller's own key input, and
///    re-reading it from disk could only describe a later edit;
///  • for every referenced F# project, transitively, its options and ALL its sources.
///
/// CONTENT is hashed, never a referenced project's output DLL: fsc writes the absolute
/// PDB path into the assembly, so DLL bytes differ between checkouts of identical source
///A non-F# reference contributes only its repo-relative output path.
/// Generated sources under `obj/`/`bin/` are skipped, as the check pipeline skips them
/// for the project being checked. Returns None if any source cannot be read.
let dependencyClosureHash (repoRoot: string option) (options: FSharpProjectOptions) (file: string) : string option =
    let keyOf = CachePathIdentity.keyOf repoRoot

    let fullPath (path: string) =
        try
            Path.GetFullPath path
        with _ ->
            path

    let sourceEntries (sources: string seq) =
        sources
        |> Seq.filter (fun source -> not (PathFilter.isGeneratedPath source))
        |> Seq.map (fun source ->
            tryHashSource source
            |> Option.map (fun hash -> $"source %s{keyOf source} %s{hash}"))
        |> Seq.toList

    let projectEntry (projectOptions: FSharpProjectOptions) =
        $"project %s{keyOf projectOptions.ProjectFileName} %s{CheckCache.getProjectOptionsHashRelativeTo repoRoot projectOptions}"

    let rec referenceEntries (visited: Set<string>) (projectOptions: FSharpProjectOptions) =
        ((visited, []), projectOptions.ReferencedProjects)
        ||> Array.fold (fun (visited, acc) reference ->
            match reference with
            | FSharpReferencedProject.FSharpReference(_, referenced) when
                not (visited.Contains referenced.ProjectFileName)
                ->
                let visited = visited.Add referenced.ProjectFileName

                let visited, nested = referenceEntries visited referenced

                visited,
                acc
                @ [ Some(projectEntry referenced) ]
                @ sourceEntries referenced.SourceFiles
                @ nested
            | FSharpReferencedProject.FSharpReference _ -> visited, acc
            | other -> visited, acc @ [ Some $"reference %s{keyOf other.OutputFile}" ])

    let target = fullPath file

    let before =
        match
            options.SourceFiles
            |> Array.tryFindIndex (fun source -> fullPath source = target)
        with
        | Some index -> Array.take index options.SourceFiles
        // A file the options do not list: every source is a possible dependency.
        | None -> options.SourceFiles

    let _, references = referenceEntries (Set.singleton options.ProjectFileName) options

    [ Some(projectEntry options) ] @ sourceEntries before @ references
    |> allSome
    |> Option.map (String.concat "\n" >> CheckCache.sha256Hex)
