/// One content key for every unit of verifiable work (ADR-059 §1).
///
/// An action — a build, a plugin's run, a test project's run — is keyed by a digest of its
/// kind and its LABELLED input digests. Every input is a named digest (a file's
/// `ContentHash`, a tree digest, or another action's key), so a miss can name the input
/// that moved, and the key is a merkle key over everything it was folded from.
///
/// RECIPE (a documented contract):
///   "fshw-action-v1" LF kind LF
///   for each (label, digest), ordered by label (ordinal):
///       len(label) ":" label "|" len(digest) ":" digest "|"
///   value = "ak1:" + lowerhex(sha256(utf8(that)))
///
/// Length prefixes, not separators: no label/value split can make two input sets share a
/// byte stream. A duplicate label is refused rather than merged, because which of two
/// digests a key "really" stands for is not a question a key may leave open. Nothing about
/// time enters a key (ADR-008).
///
/// Part of the correctness core (L0, re-architecture Phase 1, AUTOMATION-1695): compiled into
/// FsHotWatch.Core, which references no FCS, under the module name it always had. ADR-059 §9
/// item 5 re-expresses its users as typed `FsHotWatch.Core.ActionKey` kinds.
module FsHotWatch.ActionKey

open System
open System.Security.Cryptography
open System.Text

/// Names the recipe. Any change to WHAT or HOW a key is folded must change it.
[<Literal>]
let Version = "fshw-action-v1"

/// The prefix of every key value, so a key can be told from any other digest.
[<Literal>]
let Prefix = "ak1:"

/// Is `value` a key this recipe could have produced?
let isValue (value: string) : bool =
    not (isNull value)
    && value.Length = Prefix.Length + 64
    && value.StartsWith(Prefix, StringComparison.Ordinal)
    && value
       |> Seq.skip Prefix.Length
       |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

/// A key's value, parsed: text `isValue` accepts, and nothing else. A lookup that is handed
/// one never asks again whether it names a key.
/// A reference type, not a struct: since the move to FsHotWatch.Core every reader is in another
/// assembly, where `key.Value.Text` on a struct copies it (FS0052, an error at warning level 5).
type KeyValue =
    private
    | KeyValue of string

    /// The value as the store files it.
    member this.Text =
        let (KeyValue text) = this
        text

module KeyValue =
    /// The key value `text` is; `None` for text no key could have.
    let tryParse (text: string) : KeyValue option =
        if isValue text then Some(KeyValue text) else None

/// A key: its value, the kind of action, and the labelled digests it was folded from,
/// sorted by label.
type ActionKey =
    { Kind: string
      Value: KeyValue
      Inputs: (string * string) list }

/// SHA-256 of `text`'s UTF-8 bytes, lowercase hex: `ContentHash.ofText`'s recipe, which this
/// assembly cannot reference.
let private sha256Hex (text: string) : string =
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes text))

let private byLabel (inputs: (string * string) list) =
    inputs |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

/// The byte stream a key is the digest of — the pure half of the recipe.
let encode (kind: string) (inputs: (string * string) list) : string =
    let sb = StringBuilder()
    sb.Append(Version).Append('\n').Append(kind).Append('\n') |> ignore

    for (label, digest) in byLabel inputs do
        sb
            .Append(label.Length)
            .Append(':')
            .Append(label)
            .Append('|')
            .Append(digest.Length)
            .Append(':')
            .Append(digest)
            .Append('|')
        |> ignore

    sb.ToString()

/// The key of an action of `kind` over `inputs`. Throws `ArgumentException` for an empty
/// kind, a kind containing a line break, or a label given twice.
let create (kind: string) (inputs: (string * string) list) : ActionKey =
    if String.IsNullOrEmpty kind || kind.Contains '\n' then
        invalidArg (nameof kind) $"an action kind must be non-empty and on one line: \"%s{kind}\""

    match inputs |> List.countBy fst |> List.filter (fun (_, n) -> n > 1) with
    | [] -> ()
    | duplicated ->
        let labels = duplicated |> List.map fst |> String.concat ", "
        invalidArg (nameof inputs) $"an input label may appear once: %s{labels}"

    { Kind = kind
      Value = KeyValue(Prefix + sha256Hex (encode kind inputs))
      Inputs = byLabel inputs }

/// The labels whose digest differs between `stored` and `current`: first the current
/// labels that moved or are new, in label order, then the labels only `stored` has.
let moved (stored: (string * string) list) (current: (string * string) list) : string list =
    let storedMap = Map.ofList stored
    let currentMap = Map.ofList current

    let fromCurrent =
        byLabel current
        |> List.filter (fun (label, digest) -> Map.tryFind label storedMap <> Some digest)
        |> List.map fst

    let onlyStored =
        byLabel stored
        |> List.map fst
        |> List.filter (fun label -> not (Map.containsKey label currentMap))

    fromCurrent @ onlyStored
