/// Repository-relative paths (AUTOMATION-1635, T2 of AUTOMATION-1632).
///
/// A key that names a file must name the SAME file on every machine and in every
/// workspace, so it names it relative to the repository root, in one spelling:
/// - `/` is the only separator. A backslash is REFUSED, not converted: on Unix it is a
///   legal file-name character, so converting it would guess.
/// - Rooted paths (`/x`, `C:/x`, `c:x`) are refused: they name a machine, not a repo.
/// - A `..` segment is refused anywhere: a key may not reach outside the repository, and
///   resolving one against the file system would make the key depend on it.
/// - Control characters are refused, so a path can never contain the tab or newline that
///   separate fields in a key's canonical text (see `ActionKey`).
/// - Normalisation: empty segments (`a//b`), `.` segments and a trailing `/` are dropped,
///   so `./src//A.fs` and `src/A.fs` are one path. Case is kept as given.
namespace FsHotWatch.Core

open System

/// Why text is not a repository-relative path.
[<RequireQualifiedAccess>]
type PathError =
    /// Null, empty, or nothing left once `.` and empty segments are dropped.
    | Empty
    /// Starts at a root or a drive.
    | Rooted
    /// Contains a `..` segment.
    | ParentSegment
    /// Contains a backslash.
    | Backslash
    /// Contains a control character (tab, newline, NUL, ...).
    | ControlCharacter

    override this.ToString() =
        match this with
        | Empty -> "a repository-relative path cannot be empty"
        | Rooted -> "a repository-relative path cannot be rooted"
        | ParentSegment -> "a repository-relative path cannot contain '..'"
        | Backslash -> "a repository-relative path uses '/' only"
        | ControlCharacter -> "a repository-relative path cannot contain control characters"

/// A normalised path relative to the repository root; see the module comment for the rules.
[<Struct>]
type RepoRelativePath =
    private
    | RepoRelativePath of string

    /// The normalised path, `/`-separated.
    member this.Text =
        let (RepoRelativePath text) = this
        text

    override this.ToString() = this.Text

/// Parses repository-relative paths.
[<RequireQualifiedAccess>]
module RepoRelativePath =
    let private hasDrive (text: string) =
        text.Length >= 2 && Char.IsAsciiLetter text[0] && text[1] = ':'

    /// `text` as a normalised repository-relative path, or the first rule it breaks.
    let parse (text: string) : Result<RepoRelativePath, PathError> =
        if String.IsNullOrEmpty text then
            Error PathError.Empty
        elif text.Contains '\\' then
            Error PathError.Backslash
        elif Seq.exists Char.IsControl text then
            Error PathError.ControlCharacter
        elif text[0] = '/' || hasDrive text then
            Error PathError.Rooted
        else
            let segments =
                text.Split('/') |> Array.filter (fun segment -> segment <> "" && segment <> ".")

            if Array.contains ".." segments then
                Error PathError.ParentSegment
            elif Array.isEmpty segments then
                Error PathError.Empty
            else
                Ok(RepoRelativePath(String.Join('/', segments)))
