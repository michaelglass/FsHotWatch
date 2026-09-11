/// The project model observed by a scan or verdict, separate from test impact.
/// Zero registered projects is unavailable evidence; zero affected tests on an
/// available model is an ordinary selection result.
module FsHotWatch.ProjectModel

/// Each count names a different stage. Never diagnose a registration failure as
/// a loader failure merely because both leave no registered projects.
type Counts =
    { Discovered: int
      Loaded: int
      OptionsMapped: int
      Registered: int }

type Snapshot = { Generation: int64; Counts: Counts }

[<RequireQualifiedAccess>]
type UnavailableReason =
    | InvalidSnapshot
    | NoProjectsDiscovered
    | LoadingFailed
    | MappingFailed
    | RegistrationFailed

[<RequireQualifiedAccess>]
type Observation =
    | Unobserved
    | Rediscovering of requestedGeneration: int64
    | Available of Snapshot
    | Unavailable of Snapshot * UnavailableReason

/// Classify one completed epoch. Availability requires evidence from every stage;
/// it never depends on an empty impact set or a plugin's idle status.
let ofCompleted (generation: int64) (counts: Counts) : Observation =
    let snapshot =
        { Generation = generation
          Counts = counts }

    let unavailable reason =
        Observation.Unavailable(snapshot, reason)

    if
        generation < 0L
        || ([ counts.Discovered; counts.Loaded; counts.OptionsMapped; counts.Registered ]
            |> List.exists (fun count -> count < 0))
    then
        unavailable UnavailableReason.InvalidSnapshot
    elif counts.Discovered = 0 then
        unavailable UnavailableReason.NoProjectsDiscovered
    elif counts.Loaded = 0 then
        unavailable UnavailableReason.LoadingFailed
    elif counts.OptionsMapped = 0 then
        unavailable UnavailableReason.MappingFailed
    elif counts.Registered = 0 then
        unavailable UnavailableReason.RegistrationFailed
    else
        Observation.Available snapshot

/// Stable reason tokens for the versioned observation wire format.
let reasonCode =
    function
    | UnavailableReason.InvalidSnapshot -> "invalid-snapshot"
    | UnavailableReason.NoProjectsDiscovered -> "no-projects-discovered"
    | UnavailableReason.LoadingFailed -> "loading-failed"
    | UnavailableReason.MappingFailed -> "mapping-failed"
    | UnavailableReason.RegistrationFailed -> "registration-failed"

let describeUnavailable (snapshot: Snapshot) (reason: UnavailableReason) =
    let counts = snapshot.Counts
    $"PROJECT MODEL UNAVAILABLE: {reasonCode reason} at discovery generation {snapshot.Generation}       ({counts.Discovered} discovered, {counts.Loaded} loaded, {counts.OptionsMapped} mapped, {counts.Registered} registered).       No available project model was observed; this is not an empty test selection."

let failure =
    function
    | Observation.Unavailable(snapshot, reason) -> Some(describeUnavailable snapshot reason)
    | Observation.Unobserved -> Some "PROJECT MODEL UNAVAILABLE: no completed discovery has been observed."
    | Observation.Rediscovering generation ->
        Some $"PROJECT MODEL UNAVAILABLE: discovery generation {generation} is still in progress."
    | Observation.Available _ -> None
