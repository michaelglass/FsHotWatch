/// A repository host's referenced assemblies, stored once by content.
///
/// FCS identifies a referenced assembly by its path and last-write time: its IL reader
/// cache is keyed so, and so is a checker that shares imported assemblies between
/// projects. A dll inside a worktree has a different path in every worktree, so two
/// worktrees holding the same bytes import it twice. Placed here, those bytes have one
/// path, named by their hash, and one write time, derived from it: equal bytes are one
/// assembly to FCS, and different bytes can never be.
///
/// An entry is a byte-for-byte copy under its original file name (FCS resolves an
/// assembly's references by simple name), so whatever FCS reads from it is what it
/// would have read at the original path.
module FsHotWatch.ImportStore

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Security.Cryptography

let private isDigest (hash: string) =
    hash.Length = 64 && hash |> Seq.forall Uri.IsHexDigit

let private hashOf (path: string) =
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()

/// The write time of the entry for bytes hashing to `hash`: a whole second in this
/// century, a range every file system stores exactly (`ProjectSnapshots.contentStamp`
/// spans every `DateTime`, which a file system clamps). Equal hashes give equal times;
/// different bytes are told apart by the path, which names the whole hash.
let stampOf (hash: string) : DateTime =
    let digest = SHA256.HashData(Text.Encoding.UTF8.GetBytes hash)
    let seconds = BitConverter.ToUInt64(digest, 0) % uint64 (100L * 365L * 86400L)
    DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(float seconds)

/// The store under `root`. `hashFile` is the SHA-256 of a file's bytes, in lower-case
/// hex, as the daemon's content hasher gives it.
type Store(root: string, hashFile: string -> string) =
    /// Entries this process has verified, by store path.
    let entries = ConcurrentDictionary<string, Lazy<bool>>()

    /// Make `target` hold `source`'s bytes, which hash to `hash`. An entry already there
    /// is verified first: one whose bytes no longer match is replaced, never served.
    /// A copy whose bytes do not match `hash` (the source moved while it was read) is
    /// discarded, and nothing is placed.
    let materialize (source: string) (hash: string) (target: string) =
        let stamp = stampOf hash

        try
            if File.Exists target && hashOf target = hash then
                // Restored if anything touched it: FCS keys the entry by its write time.
                File.SetLastWriteTimeUtc(target, stamp)
                true
            else
                Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
                let nonce = Guid.NewGuid().ToString "N"
                let temporary = $"%s{target}.%s{nonce}.tmp"

                try
                    File.Copy(source, temporary)

                    if hashOf temporary = hash then
                        File.SetLastWriteTimeUtc(temporary, stamp)
                        File.Move(temporary, target, true)
                        true
                    else
                        false
                finally
                    if File.Exists temporary then
                        File.Delete temporary
        with ex ->
            Logging.debug "imports" $"Could not store %s{source}: %s{ex.Message}"
            false

    /// The store path holding `path`'s bytes, placed there on first ask; `None` when
    /// `path` cannot be read or did not hold still while it was copied.
    member _.Place(path: string) : string option =
        let hash = hashFile path

        if not (isDigest hash) then
            None
        else
            let target = Path.Combine(root, hash, Path.GetFileName path)

            let placed = entries.GetOrAdd(target, fun _ -> lazy (materialize path hash target))

            if placed.Value then
                Some target
            else
                // Not kept as a failure: the next ask may find the file readable.
                entries.TryRemove(KeyValuePair(target, placed)) |> ignore
                None

    /// `Place`, except for a type provider, which is read where it is: FCS loads its
    /// design-time assemblies from beside it.
    member this.PlaceReference(path: string) : string option =
        if FrameExclusions.isTypeProviderAssembly path then
            None
        else
            this.Place path
