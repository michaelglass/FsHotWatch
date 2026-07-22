/// THE content hasher, and THE fail-closed policy for a file that cannot be read.
///
/// The only question that matters: what do you hash when you CANNOT READ the file?
/// There is exactly one safe answer, and it must be the same everywhere. An
/// unreadable file must hash to something that will NOT match the hash of the same
/// file readable — so a claim made over a tree we could not fully see is DETECTABLY
/// inapplicable, rather than quietly identical to one made over a tree we could.
///
/// The unsafe answers, both of which look reasonable in isolation:
///   * SKIP the file — the hash matches, and the claim silently covers a file nobody
///     looked at. Fails OPEN.
///   * hash the empty string — every unreadable file collides with every other, and
///     with a genuinely empty file. Fails OPEN, more subtly.
///
/// So: a sentinel, in one place, used by everyone.
module FsHotWatch.ContentHash

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// Hashed in place of a file's bytes when the file cannot be read.
///
/// Deliberately NOT a valid hex digest: a consumer eyeballing a manifest can see that
/// a file was unhashable, rather than being handed a plausible-looking hash of nothing.
///
/// It is also DETERMINISTIC, and that matters — but note carefully that determinism
/// alone decides nothing. Two callers can share this value and still, correctly, reach
/// OPPOSITE conclusions from it:
///
///   * `DaemonIdentity` asks *"should I restart the daemon?"* and treats two
///     unhashable binaries as a MATCH — a random sentinel (or a refusal) would restart
///     the daemon on every command and thrash the warm FCS cache forever.
///
///   * `Verdict` asks *"does this claim apply?"* and treats an unhashable producer as
///     NEVER applicable — a verdict whose provenance we could not establish must not
///     read as current.
///
/// Both are right, because they are different questions. What must NOT differ — and
/// what this module exists to make impossible — is the HASH and the SENTINEL VALUE.
/// Two hashers with two sentinels is how a claim silently covers a file nobody looked
/// at. One hasher; each caller states its own conclusion, out loud.
[<Literal>]
let UnhashableContent = "unhashable"

let private toHex (bytes: byte array) : string =
    Convert.ToHexString(bytes).ToLowerInvariant()

/// SHA-256 of some bytes, lowercase hex.
let ofBytes (bytes: byte array) : string = toHex (SHA256.HashData(bytes))

/// SHA-256 of a string's UTF-8 bytes, lowercase hex.
let ofText (text: string) : string = ofBytes (Encoding.UTF8.GetBytes(text))

/// SHA-256 of a file's bytes (streamed — a fixture or a generated source can be
/// large), or `UnhashableContent` if it cannot be read.
///
/// NEVER throws: a hasher that can fault is a hasher every caller must wrap, and one
/// of them will forget.
let ofFile (path: string) : string =
    try
        use fs = File.OpenRead(path)
        toHex (SHA256.HashData(fs))
    with
    | :? IOException
    | :? UnauthorizedAccessException -> UnhashableContent

/// True when `hash` is a real digest rather than the "I could not hash it" sentinel.
/// Callers that need to REFUSE (rather than merely record) an unhashable input ask
/// this instead of string-matching the sentinel themselves.
let isReadable (hash: string) : bool =
    not (String.Equals(hash, UnhashableContent, StringComparison.Ordinal))
