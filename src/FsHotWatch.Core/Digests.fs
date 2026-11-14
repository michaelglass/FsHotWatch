/// Digest newtypes (AUTOMATION-1635, T2 of AUTOMATION-1632).
///
/// A digest is EXACTLY 64 lowercase hexadecimal digits: the SHA-256 width, and the same
/// text form every digest fshw already files (`ActionKey`'s "ak1:" value, `ContentHash`).
/// One fixed width and one case means two digests of the same bytes are the same string,
/// so ordinal equality IS digest equality; uppercase is refused rather than folded, because
/// a producer emitting it has a bug worth finding.
///
/// Each ROLE is its own type, so a file's content digest cannot be passed where a tree
/// digest is expected. Every constructor is private: a value of one of these types exists
/// only because `parse` accepted its text or `ofBytes` hashed bytes into it (the SHA-256 as
/// lowercase hex, which is that text by construction); there is no conversion from string.
namespace FsHotWatch.Core

open System

/// Why text is not a digest.
[<RequireQualifiedAccess>]
type DigestError =
    /// The text was null.
    | Null
    /// The text was not 64 characters long; carries the length it had.
    | WrongLength of length: int
    /// The character at this index is not one of 0-9 or a-f.
    | NotLowercaseHex of index: int

    override this.ToString() =
        match this with
        | Null -> "a digest cannot be null"
        | WrongLength length ->
            "a digest is 64 characters, not "
            + length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        | NotLowercaseHex index ->
            "a digest is lowercase hex; character "
            + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " is not"

/// The validation every digest role shares.
module internal DigestText =
    /// The width of every digest: lowercase hex of a SHA-256.
    [<Literal>]
    let Length = 64

    let private isLowerHex (c: char) =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

    /// `text`, when it is a digest; otherwise why not.
    let validate (text: string) : Result<string, DigestError> =
        if isNull text then
            Error DigestError.Null
        elif text.Length <> Length then
            Error(DigestError.WrongLength text.Length)
        else
            match Seq.tryFindIndex (isLowerHex >> not) text with
            | Some index -> Error(DigestError.NotLowercaseHex index)
            | None -> Ok text

    /// The SHA-256 of `bytes` as lowercase hex: a valid digest text by construction.
    let sha256 (bytes: byte array) : string =
        Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData bytes)

/// The digest of one file's bytes.
[<Struct>]
type ContentDigest =
    private
    | ContentDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (ContentDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses content digests.
[<RequireQualifiedAccess>]
module ContentDigest =
    /// `text` as a content digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<ContentDigest, DigestError> =
        DigestText.validate text |> Result.map ContentDigest

    /// The digest of `bytes`.
    let ofBytes (bytes: byte array) : ContentDigest = ContentDigest(DigestText.sha256 bytes)

/// The digest of a file's public signature: what a dependent's compile can observe, so an
/// edit that leaves it unchanged need not rebuild dependents.
[<Struct>]
type SignatureDigest =
    private
    | SignatureDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (SignatureDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses signature digests.
[<RequireQualifiedAccess>]
module SignatureDigest =
    /// `text` as a signature digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<SignatureDigest, DigestError> =
        DigestText.validate text |> Result.map SignatureDigest

/// The digest of a whole source tree (the set of paths and their content digests).
[<Struct>]
type TreeDigest =
    private
    | TreeDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (TreeDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses tree digests.
[<RequireQualifiedAccess>]
module TreeDigest =
    /// `text` as a tree digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<TreeDigest, DigestError> =
        DigestText.validate text |> Result.map TreeDigest

/// The digest of the toolchain an action ran under (SDK, compiler, tool and package pins).
[<Struct>]
type ToolchainDigest =
    private
    | ToolchainDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (ToolchainDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses toolchain digests.
[<RequireQualifiedAccess>]
module ToolchainDigest =
    /// `text` as a toolchain digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<ToolchainDigest, DigestError> =
        DigestText.validate text |> Result.map ToolchainDigest

/// The digest of an artifact an action produced (an assembly, a report).
[<Struct>]
type OutputDigest =
    private
    | OutputDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (OutputDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses output digests.
[<RequireQualifiedAccess>]
module OutputDigest =
    /// `text` as an output digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<OutputDigest, DigestError> =
        DigestText.validate text |> Result.map OutputDigest

    /// The digest of `bytes`.
    let ofBytes (bytes: byte array) : OutputDigest = OutputDigest(DigestText.sha256 bytes)

/// The digest of an oracle's answer: what the environment said when an action asked it a
/// question (AUTOMATION-1636). Its own role, so an answer cannot stand in for a file's bytes.
[<Struct>]
type AnswerDigest =
    private
    | AnswerDigest of string

    /// The 64 lowercase hex digits.
    member this.Hex =
        let (AnswerDigest hex) = this
        hex

    override this.ToString() = this.Hex

/// Parses and makes answer digests.
[<RequireQualifiedAccess>]
module AnswerDigest =
    /// `text` as an answer digest, when it is 64 lowercase hex digits.
    let parse (text: string) : Result<AnswerDigest, DigestError> =
        DigestText.validate text |> Result.map AnswerDigest

    /// The digest of an answer's canonical `bytes`.
    let ofBytes (bytes: byte array) : AnswerDigest = AnswerDigest(DigestText.sha256 bytes)
