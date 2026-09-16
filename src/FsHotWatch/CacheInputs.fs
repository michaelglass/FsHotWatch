/// Cache-key inputs that live OUTSIDE the file a plugin is judging, but decide its verdict.
///
/// A per-file key that names only the file's own bytes replays across a change it cannot
/// see: configuration a tool discovers by walking up from the file
/// (`.editorconfig`, `fsharplint.json`). It is gathered here as CONTENT named
/// repo-relatively, so two checkouts of the same repository still share an entry.
module FsHotWatch.CacheInputs

open System
open System.IO

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
