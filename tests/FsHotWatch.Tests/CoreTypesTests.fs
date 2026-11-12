/// FsHotWatch.Core's foundation types (AUTOMATION-1635, T1–T3 of AUTOMATION-1632).
///
/// The point of these types is what they make UNREPRESENTABLE, so the tests are of two
/// shapes: runtime refusals (a parser turning text away, a lookup answering a typed miss)
/// and compile-time properties, which are stated as a comment naming the code that would
/// not compile, next to a positive test of the code that does.
module FsHotWatch.Tests.CoreTypesTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Core

// ---------------------------------------------------------------------------------------
// Record kinds used by the tests. Two kinds that share NOTHING but the shape of a key, and
// a second schema of the first, which is how a record written by an older build looks.

[<Sealed>]
type CheckKind private () =
    interface IRecordKind with
        static member Name = "check"
        static member SchemaVersion = 1

[<Sealed>]
type AnalyzerKind private () =
    interface IRecordKind with
        static member Name = "analyzer"
        static member SchemaVersion = 1

/// The check kind after a schema bump: same name, version 2.
[<Sealed>]
type CheckKindV2 private () =
    interface IRecordKind with
        static member Name = "check"
        static member SchemaVersion = 2

let private hexA = String('a', 64)
let private hexMixed = String.replicate 4 "0123456789abcdef"

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got Error %s{error.ToString()}"

let private path = RepoRelativePath.parse >> ok

// ---------------------------------------------------------------------------------------
// T2: digests

[<Fact>]
let ``every digest newtype accepts 64 lowercase hex digits and gives them back`` () =
    test <@ (ContentDigest.parse hexMixed |> ok).Hex = hexMixed @>
    test <@ (SignatureDigest.parse hexMixed |> ok).Hex = hexMixed @>
    test <@ (TreeDigest.parse hexMixed |> ok).Hex = hexMixed @>
    test <@ (ToolchainDigest.parse hexMixed |> ok).Hex = hexMixed @>
    test <@ (OutputDigest.parse hexMixed |> ok).Hex = hexMixed @>
    test <@ (ContentDigest.parse hexA |> ok).ToString() = hexA @>

[<Fact>]
let ``a digest of the wrong length is refused with the length it had`` () =
    test <@ ContentDigest.parse (String('a', 63)) = Error(DigestError.WrongLength 63) @>
    test <@ SignatureDigest.parse (String('a', 65)) = Error(DigestError.WrongLength 65) @>
    test <@ TreeDigest.parse "" = Error(DigestError.WrongLength 0) @>
    test <@ ToolchainDigest.parse null = Error DigestError.Null @>

[<Fact>]
let ``a digest with an uppercase or non-hex character is refused at that position`` () =
    let upper = "A" + String('a', 63)
    let nonHex = String('a', 10) + "g" + String('a', 53)
    test <@ OutputDigest.parse upper = Error(DigestError.NotLowercaseHex 0) @>
    test <@ ContentDigest.parse nonHex = Error(DigestError.NotLowercaseHex 10) @>

[<Fact>]
let ``two digests of the same text are equal, of different text are not`` () =
    test <@ ContentDigest.parse hexA = ContentDigest.parse hexA @>
    test <@ ContentDigest.parse hexA <> ContentDigest.parse hexMixed @>

// ---------------------------------------------------------------------------------------
// T2: repo-relative paths

[<Fact>]
let ``a repo-relative path is accepted and its separators normalised`` () =
    test <@ (path "src/A.fs").Text = "src/A.fs" @>
    test <@ (path "src//A.fs").Text = "src/A.fs" @>
    test <@ (path "./src/./A.fs").Text = "src/A.fs" @>
    test <@ (path "src/A/").Text = "src/A" @>
    test <@ (path "src/A.fs").ToString() = "src/A.fs" @>
    test <@ path "src//A.fs" = path "src/A.fs" @>

[<Fact>]
let ``a rooted path is refused`` () =
    test <@ RepoRelativePath.parse "/etc/passwd" = Error PathError.Rooted @>
    test <@ RepoRelativePath.parse "C:/repo/a.fs" = Error PathError.Rooted @>
    test <@ RepoRelativePath.parse "c:a.fs" = Error PathError.Rooted @>

[<Fact>]
let ``a parent segment is refused wherever it appears`` () =
    test <@ RepoRelativePath.parse ".." = Error PathError.ParentSegment @>
    test <@ RepoRelativePath.parse "a/../b" = Error PathError.ParentSegment @>
    test <@ RepoRelativePath.parse "a/.." = Error PathError.ParentSegment @>

[<Fact>]
let ``a backslash is refused rather than guessed at`` () =
    test <@ RepoRelativePath.parse @"src\A.fs" = Error PathError.Backslash @>

[<Fact>]
let ``empty, null and control-character paths are refused`` () =
    test <@ RepoRelativePath.parse null = Error PathError.Empty @>
    test <@ RepoRelativePath.parse "" = Error PathError.Empty @>
    test <@ RepoRelativePath.parse "./" = Error PathError.Empty @>
    test <@ RepoRelativePath.parse "a\nb" = Error PathError.ControlCharacter @>
    test <@ RepoRelativePath.parse "a\tb" = Error PathError.ControlCharacter @>

// ---------------------------------------------------------------------------------------
// T1: typed action keys

let private content p =
    KeyInput.Content(path p, ContentDigest.parse hexA |> ok)

let private tree = KeyInput.Tree(TreeDigest.parse hexMixed |> ok)
let private toolchain = KeyInput.Toolchain(ToolchainDigest.parse hexA |> ok)

[<Fact>]
let ``a key carries its kind's name and schema version`` () =
    let key: ActionKey<CheckKind> = ActionKey.create [ tree ] |> ok
    test <@ ActionKey.kindName key = "check" @>
    test <@ ActionKey.schemaVersion key = 1 @>

[<Fact>]
let ``a key does not depend on the order its inputs were given in`` () =
    let one: ActionKey<CheckKind> =
        ActionKey.create [ tree; content "src/A.fs"; toolchain; content "src/B.fs" ]
        |> ok

    let other: ActionKey<CheckKind> =
        ActionKey.create [ content "src/B.fs"; toolchain; content "src/A.fs"; tree ]
        |> ok

    test <@ one = other @>
    test <@ (ActionKey.ref one).Canonical = (ActionKey.ref other).Canonical @>

[<Fact>]
let ``every input role is folded into the key`` () =
    let signature =
        KeyInput.Signature(path "src/A.fs", SignatureDigest.parse hexA |> ok)

    let output = KeyInput.Output(path "bin/A.dll", OutputDigest.parse hexA |> ok)

    let key: ActionKey<CheckKind> =
        ActionKey.create [ content "src/A.fs"; signature; output; tree; toolchain ]
        |> ok

    let lines = (ActionKey.ref key).Canonical.Split('\n')

    test
        <@
            lines = [| "fshw-core-key-v1"
                       "check"
                       "1"
                       "content:src/A.fs\t" + hexA
                       "output:bin/A.dll\t" + hexA
                       "signature:src/A.fs\t" + hexA
                       "toolchain\t" + hexA
                       "tree\t" + hexMixed |]
        @>

[<Fact>]
let ``two digests for one subject are refused, not merged`` () =
    let second = KeyInput.Content(path "src/A.fs", ContentDigest.parse hexMixed |> ok)

    let refused: Result<ActionKey<CheckKind>, KeyError> =
        ActionKey.create [ content "src/A.fs"; second ]

    test <@ refused = Error(KeyError.DuplicateSubject "content:src/A.fs") @>

    let twoTrees: Result<ActionKey<CheckKind>, KeyError> =
        ActionKey.create [ tree; tree ]

    test <@ twoTrees = Error(KeyError.DuplicateSubject "tree") @>

[<Fact>]
let ``the same inputs under two kinds give two different keys`` () =
    // COMPILE-TIME PROPERTY: `checkKey = analyzerKey` does not compile (FS0001, the types
    // ActionKey<CheckKind> and ActionKey<AnalyzerKind> differ), so the only comparison
    // left is of the erased references, and those differ by kind.
    let checkKey: ActionKey<CheckKind> = ActionKey.create [ tree ] |> ok
    let analyzerKey: ActionKey<AnalyzerKind> = ActionKey.create [ tree ] |> ok
    test <@ ActionKey.ref checkKey <> ActionKey.ref analyzerKey @>
    test <@ (ActionKey.ref analyzerKey).Kind = "analyzer" @>

// ---------------------------------------------------------------------------------------
// T1: typed outcomes, and the typed miss

let private checkKey: ActionKey<CheckKind> = ActionKey.create [ tree ] |> ok

let private greenOutcome =
    { Key = checkKey
      Purity = Purity.Pure
      Verdict = Verdict.Green }

[<Fact>]
let ``a recorded outcome is recalled by the key it was recorded under`` () =
    let recorded = Outcome.record greenOutcome
    test <@ Outcome.recall checkKey recorded = Lookup.Hit greenOutcome @>

[<Fact>]
let ``a record of an older schema is a typed miss, never a hit`` () =
    let recorded = Outcome.record greenOutcome
    let v2Key: ActionKey<CheckKindV2> = ActionKey.create [ tree ] |> ok
    test <@ Outcome.recall v2Key recorded = Lookup.Miss(MissReason.SchemaChanged(1, 2)) @>

[<Fact>]
let ``a record of another kind is a typed miss`` () =
    // COMPILE-TIME PROPERTY: `let o: Outcome<AnalyzerKind> = { greenOutcome with ... }`
    // and `Outcome.recall analyzerKey greenOutcome` do not compile: a check key cannot be
    // given to anything typed for analyzer records. Only the ERASED form can cross kinds,
    // and recall turns it away.
    let analyzerKey: ActionKey<AnalyzerKind> = ActionKey.create [ tree ] |> ok
    let recorded = Outcome.record greenOutcome
    test <@ Outcome.recall analyzerKey recorded = Lookup.Miss(MissReason.OtherKind "check") @>

[<Fact>]
let ``a record of the same kind under another key is a miss`` () =
    let otherKey: ActionKey<CheckKind> = ActionKey.create [ toolchain ] |> ok
    test <@ Outcome.recall otherKey (Outcome.record greenOutcome) = Lookup.Miss MissReason.OtherKey @>

// ---------------------------------------------------------------------------------------
// T3: verdict and purity

let private failure name =
    { Name = name
      Detail = "assertion failed" }

[<Fact>]
let ``a verdict is red when anything failed, even with keys missing`` () =
    let missing = ActionKey.ref checkKey

    test
        <@
            Verdict.ofParts [ failure "T1"; failure "T2" ] [ missing ] = Verdict.Red
                { Head = failure "T1"
                  Tail = [ failure "T2" ] }
        @>

[<Fact>]
let ``a verdict with nothing failed but keys missing is missing, not green`` () =
    let missing = ActionKey.ref checkKey
    test <@ Verdict.ofParts [] [ missing ] = Verdict.Missing { Head = missing; Tail = [] } @>

[<Fact>]
let ``a verdict with nothing failed and nothing missing is green`` () =
    test <@ Verdict.ofParts [] [] = Verdict.Green @>

[<Fact>]
let ``a non-empty list is built only from a list with something in it`` () =
    test <@ NonEmpty.ofList ([]: int list) = None @>
    test <@ NonEmpty.ofList [ 1; 2 ] = Some { Head = 1; Tail = [ 2 ] } @>
    test <@ NonEmpty.toList { Head = 1; Tail = [ 2 ] } = [ 1; 2 ] @>

[<Fact>]
let ``an impure outcome carries how long it stays valid`` () =
    let until = DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero)

    let impure =
        { greenOutcome with
            Purity = Purity.Impure(Validity.Until until) }

    test <@ Outcome.recall checkKey (Outcome.record impure) = Lookup.Hit impure @>

    let oracle =
        { greenOutcome with
            Purity = Purity.Oracle }

    test <@ Outcome.recall checkKey (Outcome.record oracle) = Lookup.Hit oracle @>
    test <@ Purity.Impure Validity.ThisRunOnly <> Purity.Impure(Validity.Until until) @>

// ---------------------------------------------------------------------------------------
// Edges: refusal text, the remaining hex boundary, short paths, unusable kind names.

[<Sealed>]
type UnnamedKind private () =
    interface IRecordKind with
        static member Name = ""
        static member SchemaVersion = 1

[<Sealed>]
type TabbedKind private () =
    interface IRecordKind with
        static member Name = "check\tv1"
        static member SchemaVersion = 1

[<Fact>]
let ``every refusal says what was wrong`` () =
    test <@ DigestError.Null.ToString() = "a digest cannot be null" @>
    test <@ (DigestError.WrongLength 3).ToString() = "a digest is 64 characters, not 3" @>
    test <@ (DigestError.NotLowercaseHex 7).ToString() = "a digest is lowercase hex; character 7 is not" @>
    test <@ PathError.Empty.ToString() = "a repository-relative path cannot be empty" @>
    test <@ PathError.Rooted.ToString() = "a repository-relative path cannot be rooted" @>
    test <@ PathError.ParentSegment.ToString() = "a repository-relative path cannot contain '..'" @>
    test <@ PathError.Backslash.ToString() = "a repository-relative path uses '/' only" @>

    test <@ PathError.ControlCharacter.ToString() = "a repository-relative path cannot contain control characters" @>

    test <@ (KeyError.DuplicateSubject "tree").ToString() = "two inputs name 'tree'" @>
    test <@ (KeyError.InvalidKindName "").ToString() = "'' cannot name a record kind" @>

[<Fact>]
let ``every digest role renders as its hex`` () =
    test <@ (SignatureDigest.parse hexA |> ok).ToString() = hexA @>
    test <@ (TreeDigest.parse hexA |> ok).ToString() = hexA @>
    test <@ (ToolchainDigest.parse hexA |> ok).ToString() = hexA @>
    test <@ (OutputDigest.parse hexA |> ok).ToString() = hexA @>

[<Fact>]
let ``a character below '0' is not hex either`` () =
    let punct = String('a', 5) + "!" + String('a', 58)
    test <@ TreeDigest.parse punct = Error(DigestError.NotLowercaseHex 5) @>

[<Fact>]
let ``a one-character path is a path, not a drive`` () = test <@ (path "a").Text = "a" @>

[<Fact>]
let ``a kind whose name cannot be a canonical line makes no key`` () =
    let unnamed: Result<ActionKey<UnnamedKind>, KeyError> = ActionKey.create [ tree ]
    let tabbed: Result<ActionKey<TabbedKind>, KeyError> = ActionKey.create [ tree ]
    test <@ unnamed = Error(KeyError.InvalidKindName "") @>
    test <@ tabbed = Error(KeyError.InvalidKindName "check\tv1") @>
