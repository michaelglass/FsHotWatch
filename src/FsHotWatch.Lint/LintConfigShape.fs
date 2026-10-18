/// The keys of an fsharplint.json that FSharpLint's own loader binds to nothing.
///
/// FSharpLint deserializes the file into its `Configuration` record and DROPS every
/// property no field of that record names, without a word. A rule configured under a
/// dropped key never runs, and the lint reports the file clean. The legacy group
/// wrapper (`"typography": { "enabled": true, "config": { ... } }`) is that case: the
/// group record has no `enabled` or `config` field, so every rule inside it is lost.
///
/// This module does not read the configuration: FSharpLint does. It walks the file's
/// JSON against the same record types FSharpLint deserializes into, matching names by
/// the same camel-case policy, and names each key with no field to land in. So it can
/// only ever list what the loader ignores; it can never disagree with it about a rule.
module FsHotWatch.Lint.LintConfigShape

open System
open System.Reflection
open System.Text.Json
open Microsoft.FSharp.Reflection

[<Literal>]
let private RecordFlags = BindingFlags.Public ||| BindingFlags.NonPublic

/// FSharpLint marks every optional setting `option`; none of them nests one in another.
let private unwrapOption (t: Type) =
    let definition = if t.IsGenericType then t.GetGenericTypeDefinition() else t

    if definition.Equals(typeof<option<obj>>.GetGenericTypeDefinition()) then
        t.GetGenericArguments().[0]
    else
        t

/// A key beginning with `_` annotates the file (`"_comment": "..."`). FSharpLint drops
/// it like any other unknown key, which is what an annotation wants.
let private isAnnotation (key: string) =
    key.StartsWith("_", StringComparison.Ordinal)

let rec private unbound (path: string) (target: Type) (element: JsonElement) : string list =
    let target = unwrapOption target

    if
        element.ValueKind = JsonValueKind.Object
        && FSharpType.IsRecord(target, RecordFlags)
    then
        let fields =
            FSharpType.GetRecordFields(target, RecordFlags)
            |> Array.map (fun field -> JsonNamingPolicy.CamelCase.ConvertName field.Name, field.PropertyType)
            |> dict

        let keysUnder (property: JsonProperty) =
            let key =
                if String.IsNullOrEmpty path then
                    property.Name
                else
                    $"%s{path}.%s{property.Name}"

            match fields.TryGetValue property.Name with
            | true, fieldType -> unbound key fieldType property.Value
            | false, _ when isAnnotation property.Name -> []
            | false, _ -> [ key ]

        element.EnumerateObject() |> Seq.collect keysUnder |> List.ofSeq
    else
        // A value that is not an object, or a type that is not a record (a list, a
        // union, a number): FSharpLint converts it as a whole, so there is no key to drop.
        []

/// Every key in `json` that FSharpLint's loader binds to nothing, as a dotted path
/// (`typography.config`). Empty when the loader would bind every key. Text that is not
/// JSON is left to the loader, which fails on it by name.
let unboundKeys (json: string) : string list =
    // Only the parse is guarded: text it cannot read is the loader's to reject.
    let parsed =
        try
            Some(JsonDocument.Parse json)
        with _ ->
            None

    match parsed with
    | None -> []
    | Some document ->
        try
            unbound "" typeof<FSharpLint.Framework.Configuration.Configuration> document.RootElement
        finally
            document.Dispose()

/// The failure a lint run reports for a config with unbound keys, or `None` when the
/// loader binds every key (or the file is absent, which the loader reports itself).
let check (configPath: string) : string option =
    if IO.File.Exists configPath then
        match unboundKeys (IO.File.ReadAllText configPath) with
        | [] -> None
        | keys ->
            let listed = keys |> String.concat ", "

            Some(
                $"%s{configPath} has keys FSharpLint binds to no rule, so the rules under them never run: %s{listed}. "
                + "Configure each rule at the root, as \"<rule>\": { \"enabled\": true, \"config\": { ... } }, "
                + "with FSharpLint's own field names (its default fsharplint.json lists them)."
            )
    else
        None
