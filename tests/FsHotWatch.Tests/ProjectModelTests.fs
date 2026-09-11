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
[<InlineData(1, -1, 1, 1, "invalid-snapshot")>]
[<InlineData(1, 1, -1, 1, "invalid-snapshot")>]
[<InlineData(1, 1, 1, -1, "invalid-snapshot")>]
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

[<Fact>]
let ``versioned wire preserves completed and transient observations without claiming availability`` () =
    let observations =
        [ Observation.Unobserved
          Observation.Rediscovering 23L
          ofCompleted 19L { Discovered = 2; Loaded = 2; OptionsMapped = 2; Registered = 2 }
          ofCompleted 7L { Discovered = 1; Loaded = 0; OptionsMapped = 0; Registered = 0 }
          ofCompleted 8L { Discovered = 1; Loaded = 1; OptionsMapped = 0; Registered = 0 }
          ofCompleted 9L { Discovered = 1; Loaded = 1; OptionsMapped = 1; Registered = 0 } ]
    for observation in observations do
        let json = FsHotWatch.ProjectModelWire.payload observation |> System.Text.Json.JsonSerializer.Serialize
        use document = System.Text.Json.JsonDocument.Parse json
        Assert.Equal(Some observation, FsHotWatch.ProjectModelWire.tryRead document.RootElement)
        let refusal = UnavailableException observation
        Assert.Equal(observation, refusal.Observation)
        match failure observation with
        | Some reason -> Assert.Equal(reason, refusal.Message)
        | None -> Assert.Contains("requested model could not be observed", refusal.Message)

[<Theory>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("{\"schema\":42}")>]
[<InlineData("{\"schema\":\"unknown-version\"}")>]
[<InlineData("{\"schema\":\"fshw-project-model-v1\",\"status\":\"new-status\",\"generation\":1}")>]
[<InlineData("{\"schema\":\"fshw-project-model-v1\",\"status\":\"rediscovering\",\"generation\":-1}")>]
[<InlineData("{\"schema\":\"fshw-project-model-v1\",\"status\":\"available\",\"generation\":\"1\"}")>]
[<InlineData("{\"schema\":\"fshw-project-model-v1\",\"status\":\"available\",\"generation\":1.5}")>]
[<InlineData("{\"schema\":\"fshw-project-model-v1\",\"status\":\"available\",\"generation\":9223372036854775808}")>]
let ``unknown or malformed model envelope fails closed`` (json: string) =
    use document = System.Text.Json.JsonDocument.Parse json
    Assert.Equal(None, FsHotWatch.ProjectModelWire.tryRead document.RootElement)

[<Theory>]
[<InlineData("null")>]
[<InlineData("[]")>]
[<InlineData("{}")>]
[<InlineData("{\"discovered\":1,\"loaded\":1,\"optionsMapped\":1}")>]
[<InlineData("{\"discovered\":1,\"loaded\":1,\"optionsMapped\":1,\"registered\":\"1\"}")>]
[<InlineData("{\"discovered\":1,\"loaded\":1,\"optionsMapped\":1,\"registered\":2147483648}")>]
[<InlineData("{\"discovered\":1,\"loaded\":1,\"optionsMapped\":1,\"registered\":0}")>]
let ``available wire requires complete integral positive stage evidence`` (counts: string) =
    let json = "{\"schema\":\"fshw-project-model-v1\",\"status\":\"available\",\"generation\":1,\"counts\":" + counts + "}"
    use document = System.Text.Json.JsonDocument.Parse json
    Assert.Equal(None, FsHotWatch.ProjectModelWire.tryRead document.RootElement)

[<Fact>]
let ``unavailable wire cannot mislabel a loader failure as registration failure`` () =
    let observation = ofCompleted 7L { Discovered = 1; Loaded = 0; OptionsMapped = 0; Registered = 0 }
    let json = FsHotWatch.ProjectModelWire.payload observation |> System.Text.Json.JsonSerializer.Serialize
    use valid = System.Text.Json.JsonDocument.Parse json
    Assert.Equal(Some observation, FsHotWatch.ProjectModelWire.tryRead valid.RootElement)
    use mislabeled = System.Text.Json.JsonDocument.Parse(json.Replace("loading-failed", "registration-failed"))
    Assert.Equal(None, FsHotWatch.ProjectModelWire.tryRead mislabeled.RootElement)
