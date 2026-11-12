/// Typed action keys (AUTOMATION-1635, T1 of AUTOMATION-1632).
///
/// A key's record KIND and that kind's SCHEMA VERSION live in its type, not in a string
/// field: `ActionKey<'Kind>` where `'Kind` implements `IRecordKind`. So:
/// - a check key cannot be handed to anything typed for analyzer records; that mistake is
///   a compile error (FS0001), not a lookup that happens to miss or, worse, hit;
/// - bumping a kind's `SchemaVersion` changes every key of that kind, and a record written
///   under the old version comes back as a typed miss (`Outcome.recall`), never as a value
///   decoded with the new schema.
///
/// A key's inputs are only the T2 types: digests by role, and `RepoRelativePath`. There is
/// no string input, so nothing machine-local (an absolute path, a timestamp) can enter.
namespace FsHotWatch.Core

#nowarn "3535" // Declaring an interface with static abstract members is the point here.

open System
open System.Text

/// A kind of record the outcome store keeps. Implemented by a sealed marker type that is
/// never instantiated; it exists only to be a type argument.
type IRecordKind =
    /// The kind's stable name. Non-empty, no control characters.
    static abstract Name: string
    /// The schema version of this kind's records. Bump it whenever what a record of this
    /// kind means or holds changes: every older record then misses.
    static abstract SchemaVersion: int

/// One input folded into a key.
[<RequireQualifiedAccess>]
type KeyInput =
    /// A source file's bytes.
    | Content of path: RepoRelativePath * digest: ContentDigest
    /// A file's public signature.
    | Signature of path: RepoRelativePath * digest: SignatureDigest
    /// An artifact another action produced, consumed by this one.
    | Output of path: RepoRelativePath * digest: OutputDigest
    /// The whole source tree.
    | Tree of digest: TreeDigest
    /// The toolchain.
    | Toolchain of digest: ToolchainDigest

/// Why a set of inputs is not a key.
[<RequireQualifiedAccess>]
type KeyError =
    /// Two inputs name the same subject (e.g. two content digests for one path). Refused,
    /// not merged: which digest a key "really" stands for is not a question it may leave open.
    | DuplicateSubject of subject: string
    /// The kind's `Name` is empty or contains a control character, so it cannot be a
    /// line of the canonical text.
    | InvalidKindName of name: string

    override this.ToString() =
        match this with
        | DuplicateSubject subject -> $"two inputs name '{subject}'"
        | InvalidKindName name -> $"'{name}' cannot name a record kind"

/// A key with its kind ERASED: what a store files and what a `Missing` verdict lists, so
/// keys of different kinds can sit in one list. Only `ActionKey.ref` makes one from a key;
/// a store may rebuild one from disk, and `Outcome.recall` checks it against a typed key.
type KeyRef =
    {
        /// The kind's name.
        Kind: string
        /// The kind's schema version when the key was made.
        SchemaVersion: int
        /// The key's canonical text (see `ActionKey`).
        Canonical: string
    }

/// A key for an action whose record is of kind `'Kind`. Made only by `ActionKey.create`.
///
/// Canonical text, one field per line (LF):
///   "fshw-core-key-v1", the kind's name, its schema version, then one line per input,
///   "subject TAB hex", ordered by subject (ordinal). Subjects are "content:PATH",
///   "signature:PATH", "output:PATH", "tree" and "toolchain".
/// Unambiguous because neither a path nor a kind name may contain a control character,
/// and a digest is hex; order-independent because the inputs are sorted.
type ActionKey<'Kind when 'Kind :> IRecordKind> =
    private
        {
            /// The canonical text above: equal keys have equal text, and only they do.
            Canonical: string
        }

/// Makes and reads typed keys.
[<RequireQualifiedAccess>]
module ActionKey =
    /// Names the canonical-text recipe. Any change to what or how a key is folded must
    /// change it.
    [<Literal>]
    let Recipe = "fshw-core-key-v1"

    let private subjectOf (input: KeyInput) : string * string =
        match input with
        | KeyInput.Content(path, digest) -> "content:" + path.Text, digest.Hex
        | KeyInput.Signature(path, digest) -> "signature:" + path.Text, digest.Hex
        | KeyInput.Output(path, digest) -> "output:" + path.Text, digest.Hex
        | KeyInput.Tree digest -> "tree", digest.Hex
        | KeyInput.Toolchain digest -> "toolchain", digest.Hex

    /// The kind's name.
    let kindName (_: ActionKey<'Kind>) : string = 'Kind.Name

    /// The kind's schema version.
    let schemaVersion (_: ActionKey<'Kind>) : int = 'Kind.SchemaVersion

    /// A key of kind `'Kind` over `inputs`, in any order.
    let create<'Kind when 'Kind :> IRecordKind> (inputs: KeyInput list) : Result<ActionKey<'Kind>, KeyError> =
        let name = 'Kind.Name
        let pairs = inputs |> List.map subjectOf |> List.sortBy fst

        let duplicate =
            pairs
            |> List.pairwise
            |> List.tryFind (fun ((a, _), (b, _)) -> String.Equals(a, b, StringComparison.Ordinal))

        if String.IsNullOrEmpty name || Seq.exists Char.IsControl name then
            Error(KeyError.InvalidKindName name)
        else
            match duplicate with
            | Some((subject, _), _) -> Error(KeyError.DuplicateSubject subject)
            | None ->
                let text = StringBuilder()
                text.Append(Recipe).Append('\n').Append(name).Append('\n') |> ignore
                text.Append('Kind.SchemaVersion) |> ignore

                for subject, hex in pairs do
                    text.Append('\n').Append(subject).Append('\t').Append(hex) |> ignore

                Ok { Canonical = text.ToString() }

    /// The key with its kind erased, for filing and for `Missing` lists.
    let ref (key: ActionKey<'Kind>) : KeyRef =
        { Kind = 'Kind.Name
          SchemaVersion = 'Kind.SchemaVersion
          Canonical = key.Canonical }
