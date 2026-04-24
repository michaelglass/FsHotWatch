/// Per-line max coverage merging for MTP's Cobertura output.
///
/// MTP (Microsoft Testing Platform) only supports `coverage | xml | cobertura`
/// output formats — coverlet's native JSON isn't an option through MTP's
/// `--coverage` flag. We parse Cobertura XML into a `package → file → line → hits`
/// projection, merge per-line by max, and emit Cobertura back out.
///
/// This is deliberately a plugin-local concern: TestPrune is the only caller,
/// and the merge is a file operation, not an impact-analysis primitive.
module FsHotWatch.TestPrune.CoverageMerge

open System.Globalization
open System.Text
open System.Xml.Linq

/// `package → file → (lineNumber → hitCount)`. Maps give stable iteration order
/// and cheap structural merge at the leaves.
type CoverageData = Map<string, Map<string, Map<int, int>>>

/// Canonical filenames for coverage artifacts. Centralized so the daemon,
/// CLI refresh command, and tests stay in lockstep.
[<Literal>]
let BaselineName = "coverage.baseline.cobertura.xml"

[<Literal>]
let PartialName = "coverage.partial.cobertura.xml"

[<Literal>]
let CoberturaName = "coverage.cobertura.xml"

let private tryParseInt (s: string) =
    match System.Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | false, _ -> None

let private xn (s: string) = XName.Get s

let private attrValue (name: string) (el: XElement) =
    let a = el.Attribute(xn name)
    if isNull a then "" else a.Value

/// Parse a Cobertura XML document into the `package → file → line → hits`
/// projection. Non-numeric / malformed entries are skipped silently so one
/// bad row doesn't nuke the merge.
let parse (xml: string) : CoverageData =
    if System.String.IsNullOrWhiteSpace(xml) then
        Map.empty
    else
        let doc = XDocument.Parse(xml)
        let root = doc.Root

        if isNull root then
            Map.empty
        else
            root.Descendants(xn "package")
            |> Seq.map (fun pkg ->
                let pkgName = attrValue "name" pkg

                let fileMap =
                    pkg.Descendants(xn "class")
                    |> Seq.groupBy (fun c -> attrValue "filename" c)
                    |> Seq.map (fun (fn, classes) ->
                        let lineMap =
                            classes
                            |> Seq.collect (fun c -> c.Descendants(xn "line"))
                            |> Seq.choose (fun l ->
                                match tryParseInt (attrValue "number" l), tryParseInt (attrValue "hits" l) with
                                | Some n, Some h -> Some(n, h)
                                | _ -> None)
                            // A file's lines can appear in multiple <class> blocks (inner types,
                            // method groupings). Take max across all occurrences of the same line.
                            |> Seq.groupBy fst
                            |> Seq.map (fun (line, hits) -> line, hits |> Seq.map snd |> Seq.max)
                            |> Map.ofSeq

                        fn, lineMap)
                    |> Map.ofSeq

                pkgName, fileMap)
            |> Map.ofSeq

/// Round-trip check helper: emit a CoverageData back to a minimal Cobertura
/// string. Useful for tests; production callers use `toCobertura`.
let serialize (data: CoverageData) : string =
    let sb = StringBuilder()
    sb.Append("<?xml version=\"1.0\"?><coverage><packages>") |> ignore

    for KeyValue(modName, files) in data do
        sb.Append("<package name=\"").Append(modName).Append("\"><classes>") |> ignore

        for KeyValue(file, lines) in files do
            sb.Append("<class filename=\"").Append(file).Append("\" name=\"").Append(file).Append("\"><lines>")
            |> ignore

            for KeyValue(line, hits) in lines do
                sb.Append("<line number=\"").Append(line).Append("\" hits=\"").Append(hits).Append("\" />")
                |> ignore

            sb.Append("</lines></class>") |> ignore

        sb.Append("</classes></package>") |> ignore

    sb.Append("</packages></coverage>") |> ignore
    sb.ToString()

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
