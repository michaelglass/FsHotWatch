/// Versioned project-model observation shared by RPC and persisted verdicts.
module FsHotWatch.ProjectModelWire

open System.Text.Json
open FsHotWatch.ProjectModel

[<Literal>]
let Schema = "fshw-project-model-v1"

[<Literal>]
let ErrorCode = 523

let payload (observation: Observation) : obj =
    let status, generation, counts, reason =
        match observation with
        | Observation.Unobserved -> "unobserved", None, None, None
        | Observation.Rediscovering generation -> "rediscovering", Some generation, None, None
        | Observation.Available snapshot -> "available", Some snapshot.Generation, Some snapshot.Counts, None
        | Observation.Unavailable(snapshot, reason) ->
            "unavailable", Some snapshot.Generation, Some snapshot.Counts, Some(reasonCode reason)

    let countPayload =
        counts
        |> Option.map (fun counts ->
            box
                {| discovered = counts.Discovered
                   loaded = counts.Loaded
                   optionsMapped = counts.OptionsMapped
                   registered = counts.Registered |})
        |> Option.defaultValue null

    box
        {| schema = Schema
           status = status
           generation = generation |> Option.map box |> Option.defaultValue null
           counts = countPayload
           reasonCode = reason |> Option.map box |> Option.defaultValue null |}

/// A malformed or unknown payload does not establish an available model.
let tryRead (root: JsonElement) : Observation option =
    let field name =
        match root.TryGetProperty(name: string) with
        | true, value -> Some value
        | _ -> None

    let text name =
        field name
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                Some(value.GetString())
            else
                None)

    let number name =
        field name
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.Number then
                match value.TryGetInt64() with
                | true, number -> Some number
                | _ -> None
            else
                None)

    if root.ValueKind <> JsonValueKind.Object || text "schema" <> Some Schema then
        None
    else
        match text "status", number "generation" with
        | Some "unobserved", None -> Some Observation.Unobserved
        | Some "rediscovering", Some generation when generation >= 0L -> Some(Observation.Rediscovering generation)
        | Some status, Some generation when status = "available" || status = "unavailable" ->
            match field "counts" with
            | Some counts when counts.ValueKind = JsonValueKind.Object ->
                let count name =
                    match counts.TryGetProperty(name: string) with
                    | true, value when value.ValueKind = JsonValueKind.Number ->
                        match value.TryGetInt32() with
                        | true, number -> Some number
                        | _ -> None
                    | _ -> None

                match count "discovered", count "loaded", count "optionsMapped", count "registered" with
                | Some discovered, Some loaded, Some mapped, Some registered ->
                    let observation =
                        ofCompleted
                            generation
                            { Discovered = discovered
                              Loaded = loaded
                              OptionsMapped = mapped
                              Registered = registered }

                    match status, observation with
                    | "available", Observation.Available _ -> Some observation
                    | "unavailable", Observation.Unavailable(_, reason) when text "reasonCode" = Some(reasonCode reason) ->
                        Some observation
                    | _ -> None
                | _ -> None
            | _ -> None
        | _ -> None
