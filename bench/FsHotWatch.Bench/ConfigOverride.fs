/// Changing a bench worktree's `.fshw.json` for one run, and proving the daemon took it.
///
/// `--strip <key>` removes top-level keys; `--set <dotted.path>=<json>` writes a value,
/// creating intermediate objects. Both apply only to the throwaway bench worktrees, strip
/// first, then each set in order. Every record carries what was applied.
///
/// A config edit the daemon ignores (a misspelt key, an old binary) would otherwise let a
/// run be scored under a setting it never had. So for the sections whose daemon echoes
/// its effective values at startup (`[config] <section>: key=value …`), a record is
/// invalid unless the echo shows exactly the value that was set.
module FsHotWatch.Bench.ConfigOverride

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// One `--set`.
type Set =
    {
        Path: string list
        /// The value, as compact JSON.
        Json: string
    }

/// Dotted path as written on the command line.
let pathText (s: Set) = String.concat "." s.Path

/// Parse `dotted.path=<json>`. Only the first `=` splits. A JSON `null` is refused:
/// removing a key is `--strip`'s job, and a null would make the echo check ambiguous.
let parseSet (arg: string) : Result<Set, string> =
    let at = arg.IndexOf('=')

    if at <= 0 then
        Error $"--set %s{arg}: expected <dotted.path>=<json>"
    else
        let path = arg.Substring(0, at).Split('.') |> Array.toList
        let raw = arg.Substring(at + 1)

        if path |> List.exists String.IsNullOrWhiteSpace then
            Error $"--set %s{arg}: empty path segment"
        else
            try
                match JsonNode.Parse(raw) with
                | null -> Error $"--set %s{arg}: null is not a value (use --strip to remove a key)"
                | node ->
                    Ok
                        { Path = path
                          Json = node.ToJsonString() }
            with ex ->
                Error $"--set %s{arg}: value is not JSON (%s{ex.Message}); quote strings, e.g. key=\"text\""

/// Strip `strip` keys, then apply `sets` in order, to a config document's text.
let apply (strip: string list) (sets: Set list) (configText: string) : Result<string, string> =
    let root = JsonNode.Parse(configText).AsObject()

    for k in strip do
        root.Remove(k) |> ignore

    let rec place (node: JsonObject) (path: string list) (value: JsonNode) (whole: Set) =
        match path with
        | [] -> Ok()
        | [ leaf ] ->
            node.[leaf] <- value
            Ok()
        | head :: rest ->
            match node.[head] with
            | null ->
                let child = JsonObject()
                node.[head] <- child
                place child rest value whole
            | :? JsonObject as child -> place child rest value whole
            | other ->
                let kind = other.GetValueKind()
                Error $"--set %s{pathText whole}: '%s{head}' is a %A{kind}, not an object"

    sets
    |> List.fold (fun acc s -> acc |> Result.bind (fun () -> place root s.Path (JsonNode.Parse(s.Json)) s)) (Ok())
    |> Result.map (fun () -> root.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

/// `apply` to a worktree's config text, or to the defaults (an empty object) when the
/// repository has no `.fshw.json`: a sweep must not need a config file to exist first.
let applyTo (existing: string option) (strip: string list) (sets: Set list) : Result<string, string> =
    apply strip sets (existing |> Option.defaultValue "{}")

let private pair =
    Regex(@"^(?<k>[A-Za-z][A-Za-z0-9_]*)=(?<v>\S+)$", RegexOptions.Compiled)

/// The daemon's `[config] <section>: key=value key=value` lines in a log window, as
/// `section.key → value`. Lines whose body is not ALL key=value pairs are prose, not an
/// echo, and are skipped; relayed nested-daemon lines never count.
let echo (window: string list) : (string * string) list =
    window
    |> List.collect (fun line ->
        match DaemonLog.tryTagged line with
        | Some("config", body) ->
            let colon = body.IndexOf(": ", StringComparison.Ordinal)

            if colon <= 0 then
                []
            else
                let section = body.Substring(0, colon)

                let tokens =
                    body.Substring(colon + 2).Split(' ', StringSplitOptions.RemoveEmptyEntries)

                let matches = tokens |> Array.map pair.Match

                if
                    Regex.IsMatch(section, @"^[A-Za-z][A-Za-z0-9_]*$")
                    && not (Array.isEmpty matches)
                    && matches |> Array.forall _.Success
                then
                    [ for m in matches ->
                          let key = m.Groups.["k"].Value
                          $"%s{section}.%s{key}", m.Groups.["v"].Value ]
                else
                    []
        | _ -> [])

/// Sections whose daemon echoes its effective values, so a `--set` under them can be
/// proven. Others are recorded as applied but cannot be checked.
let echoedSections = [ "checker" ]

/// The value as the echo prints it: strings unquoted, everything else as JSON.
let private echoForm (json: string) =
    match JsonNode.Parse(json) with
    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
    | node -> node.ToJsonString()

/// Why the echo does not prove the sets; empty when it does.
let echoProblems (sets: Set list) (echoed: (string * string) list) : string list =
    [ for s in sets do
          if List.contains s.Path.Head echoedSections then
              let key = pathText s
              let want = echoForm s.Json

              match echoed |> List.tryFindBack (fst >> (=) key) with
              | None -> yield $"--set %s{key}=%s{s.Json} but the daemon echoed no %s{key}"
              | Some(_, got) when got <> want -> yield $"--set %s{key}=%s{s.Json} but the daemon echoed %s{got}"
              | Some _ -> () ]
