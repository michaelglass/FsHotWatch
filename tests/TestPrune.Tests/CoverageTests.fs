module TestPrune.Tests.CoverageTests

open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Database
open TestPrune.Coverage
open TestPrune.Tests.TestHelpers

let private openRawConnection (dbPath: string) =
    let conn = new SqliteConnection($"Data Source=%s{dbPath}")
    conn.Open()
    conn

/// Insert a single symbol spanning [lineStart, lineEnd] in the given file with the
/// given content hash, driving the REAL RebuildProjects path.
let private seedSymbolWithHash
    (db: Database)
    (fullName: string)
    (sourceFile: string)
    (lineStart: int)
    (lineEnd: int)
    (contentHash: string)
    =
    let result =
        AnalysisResult.Create(
            [ { FullName = fullName
                Kind = Function
                SourceFile = sourceFile
                LineStart = lineStart
                LineEnd = lineEnd
                ContentHash = contentHash
                IsExtern = false } ],
            [],
            []
        )

    db.RebuildProjects([ result ])

/// Insert a single symbol spanning [lineStart, lineEnd] in the given file.
let private seedSymbol (db: Database) (fullName: string) (sourceFile: string) (lineStart: int) (lineEnd: int) =
    seedSymbolWithHash db fullName sourceFile lineStart lineEnd ""

module ``Coverage is symbol-relative`` =

    [<Fact>]
    let ``coverage derives absolute line from symbol start`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            db.RecordCoverage("Foo.fs", 15, 3)

            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 3) ] @>)

    // Bug A — THE KEY TEST. The stored offset (15 - 10 = 5) is unchanged when the symbol
    // moves; the absolute line is re-derived from the new line_start (18 + 5 = 23).
    [<Fact>]
    let ``Bug A — moved symbol: coverage follows`` () =
        withDbPath (fun path db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            db.RecordCoverage("Foo.fs", 15, 3)

            // Simulate an edit that pushed the symbol down 8 lines.
            use conn = openRawConnection path
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "UPDATE symbols SET line_start = 18 WHERE full_name = 'Foo.bar'"
            cmd.ExecuteNonQuery() |> ignore

            test <@ db.GetFileCoverage "Foo.fs" = [ (23, 3) ] @>)

module ``Max-merge on record`` =

    [<Fact>]
    let ``recording the same line max-merges hits`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            db.RecordCoverage("Foo.fs", 15, 1)
            db.RecordCoverage("Foo.fs", 15, 5)
            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 5) ] @>

            // A later partial run with fewer hits must not lower the stored count.
            db.RecordCoverage("Foo.fs", 15, 2)
            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 5) ] @>)

module ``Purge`` =

    [<Fact>]
    let ``purge removes a symbol's coverage`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            db.RecordCoverage("Foo.fs", 15, 3)
            db.RecordCoverage("Foo.fs", 16, 4)

            match db.FindSymbolContainingLine("Foo.fs", 15) with
            | Some(symbolId, _) -> db.PurgeCoverageForSymbol symbolId
            | None -> failwith "expected to find the seeded symbol"

            test <@ db.GetFileCoverage "Foo.fs" |> List.isEmpty @>)

module ``Cobertura ingest`` =

    /// Build a minimal Cobertura document from a sequence of `<class>` blocks, each
    /// given as `(filename, (lineNumber, hits) list)`. Multiple entries with the same
    /// filename produce multiple `<class>` blocks for that file (F#-closure shape).
    let private cobertura (classes: (string * (int * int) list) list) =
        let lineXml (n, h) =
            sprintf "<line number=\"%d\" hits=\"%d\" />" n h

        let classXml (file, lines) =
            let body = lines |> List.map lineXml |> String.concat ""
            sprintf "<class filename=\"%s\" name=\"%s\"><lines>%s</lines></class>" file file body

        let body = classes |> List.map classXml |> String.concat ""

        sprintf
            "<?xml version=\"1.0\"?><coverage><packages><package name=\"p\"><classes>%s</classes></package></packages></coverage>"
            body

    // (a) parse yields every tuple, including a duplicate line number across two <class> blocks.
    [<Fact>]
    let ``parseCobertura returns every line tuple with no dedup`` () =
        let xml =
            cobertura
                [ "Foo.fs", [ (10, 0); (11, 3) ]
                  "Bar.fs", [ (5, 2) ]
                  // Foo.fs line 11 appears again in a second class block — must NOT be deduped.
                  "Foo.fs", [ (11, 7) ] ]

        let parsed = parseCobertura xml

        test <@ parsed = [ ("Foo.fs", 10, 0); ("Foo.fs", 11, 3); ("Bar.fs", 5, 2); ("Foo.fs", 11, 7) ] @>

    // (b) round-trip: seed a symbol, ingest covered + uncovered lines, read them back.
    [<Fact>]
    let ``ingest round-trips covered and uncovered lines`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            let xml = cobertura [ "Foo.fs", [ (12, 3); (15, 0) ] ]
            let summary = ingestCobertura db None xml

            test <@ summary = {| Ingested = 2; Skipped = 0 |} @>
            test <@ db.GetFileCoverage "Foo.fs" = [ (12, 3); (15, 0) ] @>)

    // (c) a line with no containing symbol is skipped, not crashed.
    [<Fact>]
    let ``ingest skips lines outside any symbol`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            let xml = cobertura [ "Foo.fs", [ (12, 1); (999, 4) ] ]
            let summary = ingestCobertura db None xml

            test <@ summary.Ingested = 1 @>
            test <@ summary.Skipped >= 1 @>
            // Only the in-range line was stored.
            test <@ db.GetFileCoverage "Foo.fs" = [ (12, 1) ] @>)

    // (d) max-merge through ingest: the same line in two <class> blocks (hits 1 and 4)
    //     resolves to the maximum.
    [<Fact>]
    let ``ingest max-merges duplicate lines across class blocks`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            let xml = cobertura [ "Foo.fs", [ (12, 1) ]; "Foo.fs", [ (12, 4) ] ]
            let summary = ingestCobertura db None xml

            test <@ summary.Ingested = 2 @>
            test <@ db.GetFileCoverage "Foo.fs" = [ (12, 4) ] @>)

module ``Cobertura emit`` =

    /// Build the same minimal Cobertura document shape as the ingest tests.
    let private cobertura (classes: (string * (int * int) list) list) =
        let lineXml (n, h) =
            sprintf "<line number=\"%d\" hits=\"%d\" />" n h

        let classXml (file, lines) =
            let body = lines |> List.map lineXml |> String.concat ""
            sprintf "<class filename=\"%s\" name=\"%s\"><lines>%s</lines></class>" file file body

        let body = classes |> List.map classXml |> String.concat ""

        sprintf
            "<?xml version=\"1.0\"?><coverage><packages><package name=\"p\"><classes>%s</classes></package></packages></coverage>"
            body

    /// Normalize a parsed cobertura into a comparable (file, line, hits) set.
    let private asSet (rows: (string * int * int) list) = rows |> List.sort |> Set.ofList

    // (a) round-trip: ingest covered + uncovered lines across two files, emit, re-parse,
    //     and assert the SAME (file, line, hits) set comes back out.
    [<Fact>]
    let ``emit round-trips the ingested coverage`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            seedSymbol db "Bar.baz" "Bar.fs" 5 30

            let xml =
                cobertura [ "Foo.fs", [ (12, 3); (15, 0) ]; "Bar.fs", [ (7, 2); (8, 0); (20, 9) ] ]

            let ingested = parseCobertura xml |> asSet
            ingestCobertura db None xml |> ignore

            let emitted = parseCobertura (emitCobertura db) |> asSet

            test <@ emitted = ingested @>)

    // (b) THE PAYOFF — edit-resilience end-to-end through emit. Mirrors the Phase 1 Bug A
    //     test but the assertion runs against emitted+parsed cobertura, proving the DB emits
    //     current, non-stale lines after an edit with zero remap code.
    [<Fact>]
    let ``emit reports current lines after a symbol moves`` () =
        withDbPath (fun path db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            ingestCobertura db None (cobertura [ "Foo.fs", [ (15, 3) ] ]) |> ignore

            // Simulate an edit that pushed Foo.bar down 8 lines (10 -> 18).
            use conn = openRawConnection path
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "UPDATE symbols SET line_start = 18 WHERE full_name = 'Foo.bar'"
            cmd.ExecuteNonQuery() |> ignore

            let emitted = parseCobertura (emitCobertura db)

            // line 15 was offset 5 (15 - 10); after the move it derives to 18 + 5 = 23.
            test <@ emitted |> List.contains ("Foo.fs", 23, 3) @>
            test <@ emitted |> List.forall (fun (_, line, _) -> line <> 15) @>)

module ``Coverage summary`` =

    let private cobertura (classes: (string * (int * int) list) list) =
        let lineXml (n, h) =
            sprintf "<line number=\"%d\" hits=\"%d\" />" n h

        let classXml (file, lines) =
            let body = lines |> List.map lineXml |> String.concat ""
            sprintf "<class filename=\"%s\" name=\"%s\"><lines>%s</lines></class>" file file body

        let body = classes |> List.map classXml |> String.concat ""

        sprintf
            "<?xml version=\"1.0\"?><coverage><packages><package name=\"p\"><classes>%s</classes></package></packages></coverage>"
            body

    [<Fact>]
    let ``summary counts covered and total points`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            // 3 covered (hits > 0), 2 uncovered (hits = 0) => Covered 3, Total 5.
            let xml = cobertura [ "Foo.fs", [ (11, 1); (12, 0); (13, 4); (14, 0); (15, 7) ] ]

            ingestCobertura db None xml |> ignore

            let summary = fileCoverageSummary db "Foo.fs"
            test <@ summary = {| Covered = 3; Total = 5 |} @>)

// Phase 4 — edit-aware lifecycle wired into RebuildProjects (the live re-index path).
// Each test re-indexes through RebuildProjects (NOT a raw UPDATE), proving the real
// path purges stale coverage on a content change while preserving it on a pure move.
module ``Re-index lifecycle`` =

    // (a) MOVED keeps coverage. Same content_hash, shifted line_start: the offset is
    //     stable and the absolute line follows the new line_start (Bug A). Critically,
    //     a same-hash move must NOT trigger a purge.
    [<Fact>]
    let ``moved symbol (same hash) keeps coverage through RebuildProjects`` () =
        withDb (fun db ->
            seedSymbolWithHash db "Foo.bar" "Foo.fs" 10 20 "H"
            db.RecordCoverage("Foo.fs", 15, 3)

            // Re-index the SAME content (hash H) at lines 18-28 via the real path.
            seedSymbolWithHash db "Foo.bar" "Foo.fs" 18 28 "H"

            // offset 5 (15 - 10) follows to 18 + 5 = 23; hits survive.
            test <@ db.GetFileCoverage "Foo.fs" = [ (23, 3) ] @>)

    // (b) CHANGED purges. Different content_hash for the same full_name: the stored
    //     offsets are computed against the old body, so they're invalid and dropped,
    //     awaiting fresh re-ingest from the impact re-run.
    [<Fact>]
    let ``changed symbol (new hash) purges coverage through RebuildProjects`` () =
        withDb (fun db ->
            seedSymbolWithHash db "Foo.bar" "Foo.fs" 10 20 "H"
            db.RecordCoverage("Foo.fs", 15, 3)

            // Re-index the SAME symbol with a DIFFERENT content hash.
            seedSymbolWithHash db "Foo.bar" "Foo.fs" 10 20 "H2"

            test <@ db.GetFileCoverage "Foo.fs" |> List.isEmpty @>)

    // (c) REMOVED cascades. Re-indexing the file without the symbol orphan-deletes it,
    //     and its coverage_points cascade away via the ON DELETE CASCADE FK.
    [<Fact>]
    let ``removed symbol cascades its coverage away through RebuildProjects`` () =
        withDb (fun db ->
            // Seed BOTH symbols in one re-index so neither orphan-deletes the other.
            let mkSym fullName lineStart lineEnd hash =
                { FullName = fullName
                  Kind = Function
                  SourceFile = "Foo.fs"
                  LineStart = lineStart
                  LineEnd = lineEnd
                  ContentHash = hash
                  IsExtern = false }

            db.RebuildProjects(
                [ AnalysisResult.Create([ mkSym "Foo.bar" 10 20 "H"; mkSym "Foo.baz" 30 40 "G" ], [], []) ]
            )

            db.RecordCoverage("Foo.fs", 15, 3)
            db.RecordCoverage("Foo.fs", 35, 7)

            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 3); (35, 7) ] @>

            // Re-index the file with only Foo.baz — Foo.bar is removed.
            seedSymbolWithHash db "Foo.baz" "Foo.fs" 30 40 "G"

            // Foo.bar's coverage cascaded away; Foo.baz's survives (same hash, unmoved).
            test <@ db.GetFileCoverage "Foo.fs" = [ (35, 7) ] @>)

// Edge / empty-path branches: the "no row", "no symbol", malformed-input, and
// repoRoot-relativization arms that the happy-path tests above never exercise.
module ``Edge cases`` =

    /// Minimal single-class cobertura with an explicit filename (which may be absolute —
    /// the shared per-module helpers only emit the verbatim name, so this one lets the
    /// repoRoot tests feed an absolute path the way a real MS run does).
    let private coberturaFor (filename: string) (lines: (int * int) list) =
        let body =
            lines
            |> List.map (fun (n, h) -> sprintf "<line number=\"%d\" hits=\"%d\" />" n h)
            |> String.concat ""

        sprintf
            "<?xml version=\"1.0\"?><coverage><packages><package name=\"p\"><classes><class filename=\"%s\" name=\"%s\"><lines>%s</lines></class></classes></package></packages></coverage>"
            filename
            filename
            body

    // --- parseCobertura input edges ---

    [<Fact>]
    let ``parseCobertura on empty or whitespace input is empty`` () =
        test <@ parseCobertura "" |> List.isEmpty @>
        test <@ parseCobertura "   " |> List.isEmpty @>

    [<Fact>]
    let ``parseCobertura skips rows with missing or non-numeric attributes`` () =
        // A bare <line/> (no attributes) and a <line number="x" hits="y"/> (non-numeric)
        // are both dropped; only the well-formed row survives.
        let xml =
            "<?xml version=\"1.0\"?><coverage><packages><package name=\"p\"><classes>"
            + "<class filename=\"Foo.fs\" name=\"Foo.fs\"><lines>"
            + "<line /><line number=\"x\" hits=\"y\" /><line number=\"12\" hits=\"3\" />"
            + "</lines></class></classes></package></packages></coverage>"

        test <@ parseCobertura xml = [ ("Foo.fs", 12, 3) ] @>

    // --- normalizeFilename arms via ingestCobertura's repoRoot ---

    [<Fact>]
    let ``ingest with repoRoot relativizes an absolute cobertura path to match a symbol`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            // Absolute path as a real MS run emits; repoRoot relativizes it to "Foo.fs".
            let summary =
                ingestCobertura db (Some "/repo") (coberturaFor "/repo/Foo.fs" [ (15, 3) ])

            test <@ summary = {| Ingested = 1; Skipped = 0 |} @>
            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 3) ] @>)

    [<Fact>]
    let ``ingest with repoRoot leaves an already-relative path verbatim`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20

            // Relative filename + Some repoRoot: IsPathRooted is false, so it's used as-is.
            let summary = ingestCobertura db (Some "/repo") (coberturaFor "Foo.fs" [ (15, 3) ])

            test <@ summary = {| Ingested = 1; Skipped = 0 |} @>
            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 3) ] @>)

    // --- Database coverage members: the "no row" / None branches ---

    [<Fact>]
    let ``FindSymbolContainingLine returns None when no symbol spans the line`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            test <@ db.FindSymbolContainingLine("Foo.fs", 999) = None @>
            test <@ db.FindSymbolContainingLine("Other.fs", 15) = None @>)

    [<Fact>]
    let ``RecordCoverage on a line with no containing symbol is a no-op`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            db.RecordCoverage("Foo.fs", 999, 5)
            test <@ db.GetFileCoverage "Foo.fs" |> List.isEmpty @>)

    [<Fact>]
    let ``GetFileCoverage for a file with no coverage is empty`` () =
        withDb (fun db ->
            seedSymbol db "Foo.bar" "Foo.fs" 10 20
            test <@ db.GetFileCoverage "Foo.fs" |> List.isEmpty @>
            test <@ db.GetFileCoverage "Unknown.fs" |> List.isEmpty @>)

    [<Fact>]
    let ``GetCoveredFiles on an empty database is empty`` () =
        withDb (fun db -> test <@ db.GetCoveredFiles() |> List.isEmpty @>)

    // --- purge pre-pass skips extern symbols (the `not sym.IsExtern` guard) ---

    [<Fact>]
    let ``re-index including an extern symbol skips the purge for it`` () =
        withDb (fun db ->
            let normal =
                { FullName = "Foo.bar"
                  Kind = Function
                  SourceFile = "Foo.fs"
                  LineStart = 10
                  LineEnd = 20
                  ContentHash = "H"
                  IsExtern = false }

            let ext =
                { normal with
                    FullName = "Ext.ern"
                    IsExtern = true
                    ContentHash = "E" }

            db.RebuildProjects([ AnalysisResult.Create([ normal; ext ], [], []) ])
            db.RecordCoverage("Foo.fs", 15, 3)

            // Re-index with the extern symbol's hash changed: its content differs but the
            // `not IsExtern` guard skips the purge, and the normal symbol (unchanged) keeps
            // its coverage — exercising both arms of the guard.
            let ext2 = { ext with ContentHash = "E2" }
            db.RebuildProjects([ AnalysisResult.Create([ normal; ext2 ], [], []) ])

            test <@ db.GetFileCoverage "Foo.fs" = [ (15, 3) ] @>)
