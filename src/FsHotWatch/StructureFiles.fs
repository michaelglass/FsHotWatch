/// THE files that decide WHAT IS COMPILED — one list, read by every cache key that
/// claims to have noticed a structural change.
///
/// AUTOMATION-303. A structural change (a new file and its `<Compile Include=…>`, a
/// deleted file, an item moved between projects) is the one class of change a
/// CONTENT merkle over source files cannot see: the file it must notice is a file it
/// has never seen. So every cache whose entry asserts something about "the tree"
/// folds a hash of these files into its key, and a structural change is a guaranteed
/// MISS.
///
/// The list lived in two places at once — `TestPrunePlugin.structureFilePatterns` had
/// three entries, `BuildPlugin.BuildInputsHasher` had the project files only — and the
/// two disagreed about `Directory.Build.props`, which is exactly the drift that lets one
/// cache miss while the other replays. ONE list, here, in the assembly both reference.
module FsHotWatch.StructureFiles

open System.IO

/// The project files themselves, as GLOB patterns: an F# source file only enters a
/// build because one of these names it, since F# has no globbed compile items
/// (compilation order is part of the language).
let projectPatterns = [ "*.fsproj"; "*.csproj" ]

/// MSBuild's IMPLICIT IMPORTS — the files MSBuild folds into a project without the
/// project naming them, found by walking UP from the project directory
/// (`GetPathOfFileAbove`). Each can add compile items to every project beneath it at
/// once, so each is as structural as the `.fsproj`.
///
/// `Directory.Build.props` was already here; `Directory.Build.targets` and
/// `Directory.Packages.props` were not, and the gap is not cosmetic — a `<Compile
/// Include=…>` in `Directory.Build.targets` adds a file to EVERY project in the repo
/// while moving neither the project files nor any source file that was already there.
let implicitImportNames =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props" ]

/// Every name/pattern a whole-repo walk must match to see the structure. The union of
/// the two lists above, so a name added to either is seen by every consumer.
let allPatterns = projectPatterns @ implicitImportNames

/// The implicit-import files that apply to a project, by MSBuild's OWN rule: for each
/// name, the NEAREST ancestor directory (starting at the project's own) that contains
/// it — `GetPathOfFileAbove` stops at the first hit and so does this.
///
/// Bounded by construction: the walk ends at the filesystem root, and each name
/// contributes at most one path. Returns absolute paths, sorted, so a caller's merkle
/// is reproducible.
///
/// Deliberately NOT "every such file above the project": MSBuild imports one, and
/// hashing files MSBuild ignores would make an unrelated repo-root edit invalidate a
/// build that could not have been affected by it.
let implicitImportsFor (projectPath: string) : string list =
    let startDir =
        match Path.GetDirectoryName(Path.GetFullPath projectPath) with
        | null
        | "" -> Path.GetFullPath "."
        | d -> d

    let rec nearest (name: string) (dir: string) : string option =
        let candidate = Path.Combine(dir, name)

        if File.Exists candidate then
            Some candidate
        else
            match Path.GetDirectoryName dir with
            | null
            | "" -> None
            | parent when System.String.Equals(parent, dir, System.StringComparison.Ordinal) -> None
            | parent -> nearest name parent

    implicitImportNames
    |> List.choose (fun name -> nearest name startDir)
    |> List.distinct
    |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))
