/// The content hasher, and the fail-closed policy for a file that cannot be read.
///
/// An unreadable file must hash to something that will NOT match the hash of the same
/// file readable, so a claim made over a tree we could not fully see is detectably
/// inapplicable. Both plausible alternatives fail OPEN: skipping the file leaves the
/// hash matching, so the claim silently covers a file nobody looked at; hashing the
/// empty string collides every unreadable file with every other and with a genuinely
/// empty one. Hence one sentinel, in one place, used by everyone.
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
/// It is deterministic, but determinism alone decides nothing: two callers share this
/// value and correctly reach OPPOSITE conclusions. `DaemonIdentity` asks "should I
/// restart the daemon?" and treats two unhashable binaries as a MATCH — a random
/// sentinel would restart on every command and thrash the warm FCS cache. `Verdict`
/// asks "does this claim apply?" and treats an unhashable producer as never applicable.
/// What must not differ is the hash and the sentinel value: two hashers with two
/// sentinels is how a claim silently covers a file nobody looked at.
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
/// Never throws — otherwise every caller would have to wrap it.
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
