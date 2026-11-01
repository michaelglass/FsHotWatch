/// The constructor arguments of an indexed attribute, as stored in
/// `symbol_attributes.args_json` and read back by every consumer of the attribute index.
///
/// The stored form is a JSON array written by a JSON writer, so every string — one holding
/// a backslash, a quote or a comma included — reads back as exactly itself. It used to be
/// assembled by hand with no escaping, which the readers then parsed three different ways;
/// a path such as `data\cases.json` produced a payload no JSON reader accepts, and its
/// `DependsOnFile` declaration silently seeded nothing (AUTOMATION-1452).
module TestPrune.AttributeArgs

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json

/// One constructor argument of a stored attribute.
type AttributeArg =
    /// A string argument, exactly as written in source.
    | Text of string
    /// Any other constant, as its JSON text: a number, `true`/`false`, `null`, or — for a
    /// value JSON has no literal for (a char, an enum, a type, an array) — an object
    /// `{"value": "<invariant text>"}`.
    | Constant of json: string

let private writeValue (writer: Utf8JsonWriter) (value: obj) =
    match value with
    | null -> writer.WriteNullValue()
    | :? string as s -> writer.WriteStringValue s
    | :? bool as b -> writer.WriteBooleanValue b
    | :? int8
    | :? int16
    | :? int32
    | :? int64
    | :? uint8
    | :? uint16
    | :? uint32
    | :? uint64
    | :? decimal -> writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture))
    | :? double as d when Double.IsFinite d -> writer.WriteNumberValue d
    | :? single as f when Single.IsFinite f -> writer.WriteNumberValue f
    | other ->
        writer.WriteStartObject()
        writer.WriteString("value", Convert.ToString(other, CultureInfo.InvariantCulture))
        writer.WriteEndObject()

/// The stored form of an attribute's constructor argument values, in order.
let serialize (values: obj seq) : string =
    use stream = new MemoryStream()

    (use writer = new Utf8JsonWriter(stream)
     writer.WriteStartArray()

     for value in values do
         writeValue writer value

     writer.WriteEndArray())

    Encoding.UTF8.GetString(stream.ToArray())

/// Read a stored payload back. A payload that is not a JSON array is an Error naming it:
/// the index holds only what `serialize` wrote, so an unreadable one is damage to report,
/// never a declaration to drop in silence.
let parse (argsJson: string) : Result<AttributeArg list, string> =
    try
        use document = JsonDocument.Parse(argsJson)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Array then
            Error $"attribute arguments are not a JSON array: %s{argsJson}"
        else
            [ for element in root.EnumerateArray() ->
                  match element.ValueKind with
                  | JsonValueKind.String -> Text(element.GetString())
                  | _ -> Constant(element.GetRawText()) ]
            |> Ok
    with :? JsonException as ex ->
        Error $"attribute arguments are not valid JSON (%s{ex.Message}): %s{argsJson}"

/// The string arguments, in order; every other constant is skipped.
let strings (args: AttributeArg list) : string list =
    args
    |> List.choose (function
        | Text s -> Some s
        | Constant _ -> None)

/// Every argument as text: a string as itself, any other constant as its JSON text.
let texts (args: AttributeArg list) : string list =
    args
    |> List.map (function
        | Text s -> s
        | Constant json -> json)
