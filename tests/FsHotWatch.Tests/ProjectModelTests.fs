module FsHotWatch.Tests.ProjectModelTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ProjectModel

[<Theory>]
[<InlineData(0, 0, 0, 0, "no-projects-discovered")>]
[<InlineData(1, 0, 0, 0, "loading-failed")>]
[<InlineData(1, 1, 0, 0, "mapping-failed")>]
[<InlineData(1, 1, 1, 0, "registration-failed")>]
[<InlineData(0, 1, 1, 1, "no-projects-discovered")>]
[<InlineData(1, 0, 1, 1, "loading-failed")>]
[<InlineData(1, 1, 0, 1, "mapping-failed")>]
[<InlineData(-1, 1, 1, 1, "invalid-snapshot")>]
let ``unavailable model retains stage counts and a distinct machine reason``
    (discovered: int, loaded: int, mapped: int, registered: int, expectedReason: string)
    =
    let counts =
        { Discovered = discovered
          Loaded = loaded
          OptionsMapped = mapped
          Registered = registered }

    let observation = ofCompleted 7L counts

    match observation with
    | Observation.Unavailable(snapshot, reason) ->
        test <@ snapshot.Generation = 7L @>
        test <@ snapshot.Counts = counts @>
        test <@ reasonCode reason = expectedReason @>

        test
            <@
                failure observation
                |> Option.exists (fun message -> message.Contains(expectedReason))
            @>
    | other -> failwithf "Expected unavailable observation, got %A" other

[<Fact>]
let ``available model preserves its epoch and all discovery stage counts`` () =
    let counts =
        { Discovered = 2
          Loaded = 2
          OptionsMapped = 2
          Registered = 2 }

    let observation = ofCompleted 19L counts
    test <@ observation = Observation.Available { Generation = 19L; Counts = counts } @>
    test <@ failure observation = None @>

[<Fact>]
let ``unknown and in-progress models cannot certify availability`` () =
    test <@ failure Observation.Unobserved |> Option.isSome @>
    test <@ failure (Observation.Rediscovering 23L) |> Option.isSome @>

[<Fact>]
let ``invalid discovery generation cannot certify an otherwise populated model`` () =
    let counts =
        { Discovered = 1
          Loaded = 1
          OptionsMapped = 1
          Registered = 1 }

    match ofCompleted -1L counts with
    | Observation.Unavailable(snapshot, UnavailableReason.InvalidSnapshot) -> test <@ snapshot.Generation = -1L @>
    | other -> failwithf "Expected invalid snapshot, got %A" other
