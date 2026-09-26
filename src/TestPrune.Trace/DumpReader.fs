/// Parses the NDJSON dumps a traced process writes (see `Contract.DumpFormat`).
module TestPrune.Trace.DumpReader

open System
open System.IO
open System.Text.Json
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

let private strOpt (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private str (e: JsonElement) (name: string) = e.GetProperty(name).GetString()

let private items (e: JsonElement) (name: string) (f: JsonElement -> 'T) : 'T list =
    e.GetProperty(name).EnumerateArray() |> Seq.map f |> List.ofSeq

let private inputKind =
    function
    | "read" -> FileRead
    | "exists" -> ExistenceProbe
    | "list" -> DirectoryListing
    | other -> failwith $"unknown input kind %s{other}"

let private scopeOf (e: JsonElement) : RecordedScope =
    let test =
        match e.GetProperty "test" with
        | t when t.ValueKind = JsonValueKind.Object ->
            Some
                { Class = str t "class"
                  Method = str t "method"
                  Display = str t "display" }
        | _ -> None

    { Key = str e "key"
      Test = test
      Parents = items e "parents" (fun v -> v.GetString())
      Links = items e "links" (fun v -> v.GetString())
      Ids = items e "ids" (fun v -> v.GetInt32()) |> Array.ofList
      Inputs =
        items e "inputs" (fun v ->
            { Kind = inputKind (str v "kind")
              Path = str v "path" })
      Children =
        items e "children" (fun v ->
            { Pid = v.GetProperty("pid").GetInt32()
              FileName = str v "file"
              EnvInjected = v.GetProperty("env").GetBoolean() }) }

let private parseLine (f: JsonElement -> 'T) (line: string) : 'T =
    using (JsonDocument.Parse line) (fun d -> f d.RootElement)

let private isEnd (e: JsonElement) =
    match e.TryGetProperty "end" with
    | true, v -> v.ValueKind = JsonValueKind.True
    | _ -> false

let private dumpOf (h: JsonElement) (scopeLines: string list) : Result<ProcessDump, string> =
    if str h "format" <> Contract.DumpFormat then
        Error "unknown format"
    else
        let c = h.GetProperty "counters"
        let n (k: string) = c.GetProperty(k).GetInt64()

        Ok
            { Pid = h.GetProperty("pid").GetInt32()
              ParentScope = strOpt h "parentScope"
              Runtime = str h "runtime"
              Os = str h "os"
              Arch = str h "arch"
              IdCount = h.GetProperty("ids").GetInt32()
              CpuMs = h.GetProperty("cpuMs").GetInt64()
              Counters =
                { Test = n "test"
                  Class = n "class"
                  Collection = n "collection"
                  Assembly = n "assembly"
                  Override = n "override"
                  StaticInit = n "staticInit"
                  Ambient = n "ambient"
                  Overflow = n "overflow" }
              Scopes = List.map (parseLine scopeOf) scopeLines }

/// Read one dump. A dump without its `{"end":true}` line is `Error "truncated"`; any
/// malformed content is an `Error` with the parser's message, never an exception.
let readFile (path: string) : Result<ProcessDump, string> =
    try
        let lines =
            File.ReadAllLines path
            |> List.ofArray
            |> List.filter (String.IsNullOrWhiteSpace >> not)

        match lines with
        | [] -> Error "empty"
        | _ when not (parseLine isEnd (List.last lines)) -> Error "truncated"
        | [ _ ] -> Error "no header"
        | header :: rest -> parseLine (fun h -> dumpOf h (List.take (rest.Length - 1) rest)) header
    with ex ->
        Error ex.Message

/// Read every `trace-*.ndjson` in `dir` (never the `.tmp` files of unfinished writes),
/// sorted by path, each with its file. A missing directory is empty.
let readEach (dir: string) : (string * Result<ProcessDump, string>) list =
    if not (Directory.Exists dir) then
        []
    else
        Directory.GetFiles(dir, "trace-*.ndjson")
        |> List.ofArray
        |> List.sort
        |> List.map (fun f -> f, readFile f)

/// Read every `trace-*.ndjson` in `dir` (never the `.tmp` files of unfinished writes):
/// the good dumps, and a `(file, reason)` for each rejected one. A missing directory is empty.
let readDirectory (dir: string) : ProcessDump list * (string * string) list =
    let results = readEach dir

    (results |> List.choose (fun (_, r) -> Result.toOption r)),
    (results
     |> List.choose (fun (f, r) ->
         match r with
         | Error e -> Some(f, e)
         | Ok _ -> None))
