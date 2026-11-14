/// The files a rewriting preprocessor may be handed: the repository's own (AUTOMATION-1680
/// follow-up). A path is parsed once, at the host's boundary, into a `RepoFile`, and a
/// preprocessor receives only those, so no preprocessor can be offered a file it must not
/// touch and none has to guard against one.
///
/// A project compiles in files that are not the repository's: a package's `_content`
/// source in the shared NuGet cache (xunit.v3.core.mtp-v2's `_content/DefaultResultWriters.fs`),
/// a file behind a symbolic link that points out of the tree (`.devenv` links into the nix
/// store), build output, and ignored files. The format preprocessor rewrote such a package
/// source in place, in the cache every checkout on the machine shares.
module FsHotWatch.RepoFiles

open System.IO

/// A file of the repository: under its root with every symbolic link resolved, not ignored
/// by the checkout's `.gitignore` files, and not build output (`obj/`, `bin/`). Built only by
/// `parse`. The path is kept as the batch spelled it, which is the form the watcher reports.
type RepoFile = private RepoFile of string

/// Why a path is not a `RepoFile`.
[<RequireQualifiedAccess>]
type Excluded =
    /// Outside the repository root, as written or once symbolic links are resolved.
    | OutsideRoot
    /// Ignored by the checkout's `.gitignore` files, or under an ignored directory.
    | Ignored
    /// Under an `obj/` or `bin/` directory of the repository.
    | BuildOutput

/// The path, as the batch spelled it.
let path (RepoFile file) : string = file

/// Parse one path against `repoRoot`, with `ignores` read from the same checkout.
let parseWith (ignores: PathFilter.GitIgnores) (repoRoot: string) (file: string) : Result<RepoFile, Excluded> =
    // A relative path is the repository's own spelling: read it against the root, not
    // against wherever the process happens to run.
    let absolute =
        if Path.IsPathRooted file then
            file
        else
            Path.GetFullPath(Path.Combine(repoRoot, file))

    // Compared with every link resolved on both sides: the watcher may spell a batch under
    // the real root while `repoRoot` is spelled through a link to it (`CommandPreprocessor`),
    // and a link inside the tree may point out of it.
    if PathFilter.resolvesOutsideRepo repoRoot absolute then
        Error Excluded.OutsideRoot
    elif PathFilter.isGeneratedUnder repoRoot absolute then
        Error Excluded.BuildOutput
    elif
        ignores.IsIgnored(absolute, false)
        || ignores.IsUnderIgnored(Path.GetDirectoryName absolute)
    then
        Error Excluded.Ignored
    else
        Ok(RepoFile file)

/// The repository's files among `files`, in order, each once. Everything else is dropped.
let parseAll (repoRoot: string) (files: string list) : RepoFile list =
    let ignores = PathFilter.GitIgnores repoRoot

    files
    |> List.distinct
    |> List.choose (fun file ->
        match parseWith ignores repoRoot file with
        | Ok repoFile -> Some repoFile
        | Error _ -> None)
