/// Typed outcomes and the typed miss (AUTOMATION-1635, T1 of AUTOMATION-1632).
///
/// An `Outcome<'Kind>` is what an action of kind `'Kind` concluded, under the key it ran
/// with. Its kind is in its type, so it can only be paired with a key of the same kind.
/// A store files the kind-erased `RecordedOutcome`; `Outcome.recall` is the one way back,
/// and it answers a record of another kind, another schema version or another key with a
/// typed `Miss` naming which, never with a hit.
namespace FsHotWatch.Core

/// An outcome with its kind erased: the form a store files.
type RecordedOutcome =
    {
        /// The key, erased; carries the kind and schema version it was recorded under.
        RecordedKey: KeyRef
        /// Whether the outcome is a function of its key.
        Purity: Purity
        /// What it concluded.
        Verdict: Verdict
    }

/// What an action of kind `'Kind` concluded.
type Outcome<'Kind when 'Kind :> IRecordKind> =
    {
        /// The key the action ran under.
        Key: ActionKey<'Kind>
        /// Whether the outcome is a function of that key.
        Purity: Purity
        /// What it concluded.
        Verdict: Verdict
    }

/// Why a recorded outcome does not answer a typed key.
[<RequireQualifiedAccess>]
type MissReason =
    /// It was recorded for another kind of record, named here.
    | OtherKind of recordedKind: string
    /// It was recorded under another schema version of this kind.
    | SchemaChanged of recorded: int * current: int
    /// It is this kind and schema, but for different inputs.
    | OtherKey

/// The answer to looking a typed key up.
[<RequireQualifiedAccess>]
type Lookup<'Kind when 'Kind :> IRecordKind> =
    /// The record answers the key.
    | Hit of Outcome<'Kind>
    /// It does not, and why.
    | Miss of MissReason

/// Records and recalls outcomes.
[<RequireQualifiedAccess>]
module Outcome =
    /// The outcome with its kind erased, for a store.
    let record (outcome: Outcome<'Kind>) : RecordedOutcome =
        { RecordedKey = ActionKey.ref outcome.Key
          Purity = outcome.Purity
          Verdict = outcome.Verdict }

    /// `recorded` as an outcome of `key`'s kind, when it was recorded for exactly that key
    /// under the current schema; otherwise a typed miss. Kind is checked before schema, so a
    /// record of another kind is never reported as a schema change.
    let recall (key: ActionKey<'Kind>) (recorded: RecordedOutcome) : Lookup<'Kind> =
        let current = ActionKey.ref key

        if recorded.RecordedKey.Kind <> current.Kind then
            Lookup.Miss(MissReason.OtherKind recorded.RecordedKey.Kind)
        elif recorded.RecordedKey.SchemaVersion <> current.SchemaVersion then
            Lookup.Miss(MissReason.SchemaChanged(recorded.RecordedKey.SchemaVersion, current.SchemaVersion))
        elif recorded.RecordedKey.Canonical <> current.Canonical then
            Lookup.Miss MissReason.OtherKey
        else
            Lookup.Hit
                { Key = key
                  Purity = recorded.Purity
                  Verdict = recorded.Verdict }
