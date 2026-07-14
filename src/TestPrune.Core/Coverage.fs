/// Cobertura coverage ingest into the `coverage_points` table.
///
/// Microsoft's `dotnet test --coverage` (and MTP's `coverage | xml | cobertura`)
/// emit a Cobertura document whose `<line>` set equals the PDB's span-expanded
/// sequence points — deterministic and complete. We parse those line/hit pairs
/// and feed each into `Database.RecordCoverage`, which resolves the containing
/// symbol and stores the hit symbol-relative (so it survives source edits).
///
/// This lives in TestPrune.Core (NOT FsHotWatch) so the DB and its ingest stay
/// in one place; it deliberately does NOT depend on FsHotWatch.TestPrune's
/// `CoverageMerge` — that is a plugin-local, line-keyed merge being retired by
/// this design.
module TestPrune.Coverage

open System
open System.Globalization
open System.IO
open System.Xml.Linq
open TestPrune.Database

/// Outcome of one `ingestCobertura` pass. `Ingested` counts the coverage points that
/// resolved to a containing symbol and were stored; `Skipped` counts those that had no
/// preceding symbol to anchor to and were dropped.
///
/// Named (not anonymous) deliberately: an anonymous record has no stable, cross-build
/// name, so TestPrune's own AST impact analysis cannot see a caller's coupling to this
/// shape — the very blind spot `TestPrune.Analyzers` (TP001) exists to flag.
type CoverageIngestSummary = { Ingested: int; Skipped: int }

/// Per-file coverage tally. `Total` is every coverable point stored for the file
/// (covered or not); `Covered` is the subset with `hits > 0`.
///
/// Named for the same reason as `CoverageIngestSummary` — see TP001.
type FileCoverageSummary = { Covered: int; Total: int }

let private xn (s: string) = XName.Get s

let private attrValue (name: string) (el: XElement) =
    let a = el.Attribute(xn name)
    if isNull a then "" else a.Value

let private tryParseInt (s: string) =
    match Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | false, _ -> None

/// Parse a Cobertura XML document into EVERY `(filename, lineNumber, hits)` tuple
/// found under any `<class>`, with NO deduplication. A file's lines legitimately
/// appear across multiple `<class>` blocks (F# closures / inner method groupings)
/// and the same line number can recur with different hit counts — preserving all
/// of them lets the downstream max-merge in `RecordCoverage` pick the true maximum.
/// Malformed / non-numeric rows are skipped silently so one bad entry doesn't nuke
/// the whole ingest.
let parseCobertura (xml: string) : (string * int * int) list =
    if String.IsNullOrWhiteSpace xml then
        []
    else
        let doc = XDocument.Parse xml
        let root = doc.Root

        if isNull root then
            []
        else
            root.Descendants(xn "class")
            |> Seq.collect (fun cls ->
                let filename = attrValue "filename" cls

                cls.Descendants(xn "line")
                |> Seq.choose (fun ln ->
                    match tryParseInt (attrValue "number" ln), tryParseInt (attrValue "hits" ln) with
                    | Some n, Some h -> Some(filename, n, h)
                    | _ -> None))
            |> Seq.toList

/// Path normalization: `symbols.source_file` is stored REPO-RELATIVE
/// (AstAnalyzer.normalizeSymbolPaths runs `Path.GetRelativePath(repoRoot, _)`),
/// whereas Cobertura `filename` attributes are ABSOLUTE. To match a coverage row
/// to a stored symbol we convert the cobertura path back to the same repo-relative
/// form. When `repoRoot` is supplied we relativize against it; otherwise the
/// filename is assumed already repo-relative (or test paths chosen to match) and
/// is used verbatim. Either way separators are normalized to the DB's form.
let private normalizeFilename (repoRoot: string option) (filename: string) =
    // GetRelativePath emits the platform separator — the same convention
    // AstAnalyzer.normalizeSymbolPaths used when indexing, so the result matches.
    match repoRoot with
    | Some root when Path.IsPathRooted filename -> Path.GetRelativePath(root, filename)
    | _ -> filename

/// Ingest a Cobertura document into `db.coverage_points`. Parses every
/// `(file, line, hits)`, normalizes the path, and hands the whole batch to
/// `db.RecordCoverageBatch` (one connection + transaction), which max-merges each
/// line symbol-relative by `(symbol_id, line_offset)`. A line with no preceding
/// symbol is skipped (per-file fallback is a later phase). Returns how many rows
/// mapped to a symbol (`Ingested`) vs were skipped for lack of one (`Skipped`).
///
/// `repoRoot` (optional): when the cobertura `filename`s are absolute paths from a
/// real run, pass the repo root so they relativize to match `symbols.source_file`.
let ingestCobertura (db: Database) (repoRoot: string option) (xml: string) : CoverageIngestSummary =
    let rows =
        parseCobertura xml
        |> List.map (fun (file, line, hits) -> (normalizeFilename repoRoot file, line, hits))

    let ingested, skipped = db.RecordCoverageBatch rows

    { Ingested = ingested
      Skipped = skipped }

/// Emit a minimal, well-formed Cobertura document from the CURRENT coverage state
/// in `db`. One `<package>`/`<class>` per covered file; each `<line>`'s number is
/// `db.GetFileCoverage`'s DERIVED absolute line (`symbol.line_start + line_offset`),
/// so a symbol that moved since ingest is reported at its new position — the output
/// is never stale and needs no remap pass. The document round-trips cleanly through
/// `parseCobertura` (same `(file, line, hits)` set it was built from).
let emitCobertura (db: Database) : string =
    let classes =
        db.GetCoveredFiles()
        |> List.map (fun file ->
            let lines =
                db.GetFileCoverage file
                |> List.map (fun (line, hits) ->
                    XElement(
                        xn "line",
                        XAttribute(xn "number", line),
                        XAttribute(xn "hits", hits),
                        XAttribute(xn "branch", "false")
                    ))

            XElement(
                xn "package",
                XAttribute(xn "name", file),
                XElement(
                    xn "classes",
                    XElement(
                        xn "class",
                        XAttribute(xn "filename", file),
                        XAttribute(xn "name", file),
                        XElement(xn "lines", lines)
                    )
                )
            ))

    let doc = XDocument(XElement(xn "coverage", XElement(xn "packages", classes)))

    doc.ToString()

/// Per-file coverage tally read from the current DB state. `Total` is the number of
/// coverable points stored for the file (every ingested line, covered or not);
/// `Covered` is the subset with `hits > 0`.
let fileCoverageSummary (db: Database) (file: string) : FileCoverageSummary =
    let rows = db.GetFileCoverage file
    let total = List.length rows
    let covered = rows |> List.filter (fun (_, hits) -> hits > 0) |> List.length
    { Covered = covered; Total = total }
