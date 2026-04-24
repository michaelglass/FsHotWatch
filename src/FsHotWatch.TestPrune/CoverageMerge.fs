/// Pure coverlet-JSON coverage merging, with a minimal Cobertura XML emitter.
///
/// Coverlet's real JSON shape is considerably richer — each file carries
/// `Lines`, `Branches`, `Methods`, `Classes` and other sub-objects. We only
/// need per-line hit counts for partial-run merging, so this module parses
/// the module → file → line → hits projection and ignores everything else.
/// When a file node carries a nested `"Lines": { "10": n, ... }` sub-object
/// (the actual coverlet format) we pull the lines from there; the "flat"
/// shape `file: { "10": n, ... }` is also accepted so tests can stay compact.
///
/// This is deliberately a plugin-local concern: TestPrune is the only caller,
/// and the merge is a file operation, not an impact-analysis primitive.
module FsHotWatch.TestPrune.CoverageMerge

open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

/// `module → file → (lineNumber → hitCount)`. Maps give stable iteration order
/// and cheap structural merge at the leaves.
type CoverageData = Map<string, Map<string, Map<int, int>>>

/// Canonical filenames for coverage artifacts. Centralized so the daemon,
/// CLI refresh command, and tests stay in lockstep.
[<Literal>]
let BaselineJsonName = "coverage.baseline.json"

[<Literal>]
let PartialJsonName = "coverage.partial.json"

[<Literal>]
let CoberturaName = "coverage.cobertura.xml"

let private tryParseInt (s: string) =
    match System.Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | false, _ -> None

/// Extract a `lineNumber → hits` map from a file-node JsonObject. Supports both
/// `{ "10": 1, "Lines": ... }` (ignore non-numeric siblings) and the real
/// coverlet shape where lines live under a nested `"Lines"` object.
let private readLineMap (fileNode: JsonObject) : Map<int, int> =
    let lineContainer: JsonObject =
        if fileNode.ContainsKey("Lines") then
            match fileNode["Lines"] with
            | :? JsonObject as inner -> inner
            | _ -> fileNode
        else
            fileNode

    lineContainer
    |> Seq.choose (fun (kvp: System.Collections.Generic.KeyValuePair<string, JsonNode>) ->
        match tryParseInt kvp.Key with
        | None -> None
        | Some line ->
            match kvp.Value with
            | :? JsonValue as v ->
                try
                    Some(line, v.GetValue<int>())
                with _ ->
                    None
            | _ -> None)
    |> Map.ofSeq

let parse (json: string) : CoverageData =
    let doc = JsonNode.Parse(json)

    match doc with
    | :? JsonObject as root ->
        root
        |> Seq.choose (fun modEntry ->
            match modEntry.Value with
            | :? JsonObject as files ->
                let fileMap =
                    files
                    |> Seq.choose (fun fileEntry ->
                        match fileEntry.Value with
                        | :? JsonObject as fileNode -> Some(fileEntry.Key, readLineMap fileNode)
                        | _ -> None)
                    |> Map.ofSeq

                Some(modEntry.Key, fileMap)
            | _ -> None)
        |> Map.ofSeq
    | _ -> Map.empty

let serialize (data: CoverageData) : string =
    let root = JsonObject()

    for KeyValue(modName, files) in data do
        let modObj = JsonObject()

        for KeyValue(file, lines) in files do
            let fileObj = JsonObject()

            for KeyValue(line, hits) in lines do
                fileObj[string<int> line] <- JsonValue.Create(hits)

            modObj[file] <- fileObj

        root[modName] <- modObj

    root.ToJsonString()

let hits (data: CoverageData) (moduleName: string) (file: string) (line: int) : int =
    data
    |> Map.tryFind moduleName
    |> Option.bind (Map.tryFind file)
    |> Option.bind (Map.tryFind line)
    |> Option.defaultValue 0

/// Union-merge at each level; at the leaf keep the larger hit count.
let mergePerLineMax (baseline: CoverageData) (partial: CoverageData) : CoverageData =
    let mergeLines (a: Map<int, int>) (b: Map<int, int>) =
        let keys = Set.union (a |> Map.keys |> Set.ofSeq) (b |> Map.keys |> Set.ofSeq)

        keys
        |> Seq.map (fun k ->
            let av = Map.tryFind k a |> Option.defaultValue 0
            let bv = Map.tryFind k b |> Option.defaultValue 0
            k, max av bv)
        |> Map.ofSeq

    let mergeFiles a b =
        let keys = Set.union (a |> Map.keys |> Set.ofSeq) (b |> Map.keys |> Set.ofSeq)

        keys
        |> Seq.map (fun k ->
            let av = Map.tryFind k a |> Option.defaultValue Map.empty
            let bv = Map.tryFind k b |> Option.defaultValue Map.empty
            k, mergeLines av bv)
        |> Map.ofSeq

    let keys =
        Set.union (baseline |> Map.keys |> Set.ofSeq) (partial |> Map.keys |> Set.ofSeq)

    keys
    |> Seq.map (fun k ->
        let av = Map.tryFind k baseline |> Option.defaultValue Map.empty
        let bv = Map.tryFind k partial |> Option.defaultValue Map.empty
        k, mergeFiles av bv)
    |> Map.ofSeq

let private escapeXml (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

let private fmtRate (covered: int) (total: int) =
    if total = 0 then
        "0"
    else
        let r = float covered / float total
        r.ToString("0.####", CultureInfo.InvariantCulture)

/// Emit a minimal Cobertura XML document. Contains enough to satisfy downstream
/// line-rate-based consumers (coverageratchet). Branches/methods are omitted.
let toCobertura (data: CoverageData) : string =
    let sb = StringBuilder()

    let allLines =
        data
        |> Map.toSeq
        |> Seq.collect (fun (_, files) -> files |> Map.toSeq |> Seq.collect (fun (_, lines) -> lines |> Map.toSeq))
        |> Seq.toList

    let totalLines = allLines.Length
    let coveredLines = allLines |> List.filter (fun (_, h) -> h > 0) |> List.length

    sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n") |> ignore

    sb
        .Append("<coverage line-rate=\"")
        .Append(fmtRate coveredLines totalLines)
        .Append("\" version=\"1.9\" timestamp=\"0\" lines-covered=\"")
        .Append(coveredLines)
        .Append("\" lines-valid=\"")
        .Append(totalLines)
        .Append("\">\n")
    |> ignore

    sb.Append("  <packages>\n") |> ignore

    for KeyValue(modName, files) in data do
        let modLines =
            files
            |> Map.toSeq
            |> Seq.collect (fun (_, lines) -> lines |> Map.toSeq)
            |> Seq.toList

        let modTotal = modLines.Length
        let modCovered = modLines |> List.filter (fun (_, h) -> h > 0) |> List.length

        sb
            .Append("    <package name=\"")
            .Append(escapeXml modName)
            .Append("\" line-rate=\"")
            .Append(fmtRate modCovered modTotal)
            .Append("\">\n")
        |> ignore

        sb.Append("      <classes>\n") |> ignore

        for KeyValue(file, lines) in files do
            let fTotal = lines.Count
            let fCovered = lines |> Map.toSeq |> Seq.filter (fun (_, h) -> h > 0) |> Seq.length

            sb
                .Append("        <class name=\"")
                .Append(escapeXml file)
                .Append("\" filename=\"")
                .Append(escapeXml file)
                .Append("\" line-rate=\"")
                .Append(fmtRate fCovered fTotal)
                .Append("\">\n")
            |> ignore

            sb.Append("          <lines>\n") |> ignore

            for KeyValue(line, hits) in lines do
                sb.Append("            <line number=\"").Append(line).Append("\" hits=\"").Append(hits).Append("\"/>\n")
                |> ignore

            sb.Append("          </lines>\n") |> ignore
            sb.Append("        </class>\n") |> ignore

        sb.Append("      </classes>\n") |> ignore
        sb.Append("    </package>\n") |> ignore

    sb.Append("  </packages>\n") |> ignore
    sb.Append("</coverage>\n") |> ignore

    sb.ToString()
