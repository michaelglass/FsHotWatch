/// Field accessors over a parsed `JsonElement` that never throw.
///
/// `JsonElement.TryGetProperty` THROWS on a non-object element rather than returning
/// false, so a document whose field holds a bare string where an object was expected
/// (a hand edit, a future schema) would raise `InvalidOperationException` past a
/// reader's `JsonException` handler. Here, asking a non-object for a field answers "it
/// hasn't got one", and a field of the wrong kind or out of range reads as absent.
module FsHotWatch.Json

open System.Text.Json

/// The named field, when `el` is an object that has it.
let tryProp (el: JsonElement) (name: string) : JsonElement option =
    if el.ValueKind <> JsonValueKind.Object then
        None
    else
        match el.TryGetProperty(name) with
        | true, v -> Some v
        | _ -> None

/// The named field, when it is a JSON string.
let tryString (el: JsonElement) (name: string) : string option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// The named field, when it is a JSON number that fits an `int`.
let tryInt (el: JsonElement) (name: string) : int option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> Some n
        | _ -> None
    | _ -> None

/// The named field, when it is a JSON number that fits an `int64`.
let tryInt64 (el: JsonElement) (name: string) : int64 option =
    match tryProp el name with
    | Some v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt64() with
        | true, n -> Some n
        | _ -> None
    | _ -> None
