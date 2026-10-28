/// `fshw explain <file>` — why does editing a file select the tests it selects?
///
/// Reads the daemon's impact index directly, as `fshw dead-code` does (no IPC, no running
/// daemon), and answers with the selection a symbol-free edit to the file makes: the
/// classes covering every symbol the file holds, by project (`SymbolFreeEdit.explainFile`,
/// the same derivation `check` narrows with), the `alwaysClasses` every filtered run
/// carries, and which of the file's symbols pulls in the most tests. An edit that changes
/// a symbol selects only the classes covering that symbol, a subset of this answer.
module internal FsHotWatch.Cli.Explain

open System.IO
open System.Text.Json
open TestPrune.Database
open FsHotWatch.TestPrune
open FsHotWatch.TestPrune.TestPrunePlugin

/// What `check` selects within, from `.fshw.json`'s `tests`.
type TestsScope =
    {
        /// The configured test projects: tests elsewhere never run.
        Runnable: Set<string>
        /// Each project's `alwaysClasses`, carried by every filtered run of it.
        AlwaysClasses: Map<string, string list>
        /// `tests.selection`: the edges the impact walk follows.
        Selection: TestPrune.Domain.WalkPolicy
    }

module TestsScope =
    /// The scope `.fshw.json` declares under `repoRoot`; `None` when it configures no tests.
    let load (repoRoot: string) : TestsScope option =
        (DaemonConfig.loadConfig repoRoot).Tests
        |> Option.map (fun t ->
            { Runnable = t.Projects |> List.map _.Project |> Set.ofList
              AlwaysClasses =
                t.Projects
                |> List.filter (fun p -> not (List.isEmpty p.AlwaysClasses))
                |> List.map (fun p -> p.Project, p.AlwaysClasses)
                |> Map.ofList
              Selection = t.Selection })

/// Options for one `explain`.
type ExplainOptions = { File: string; Json: bool }

/// The answer for one file.
type Explanation =
    { File: string
      Selection: SymbolFreeEdit.FileSelection option
      AlwaysClasses: Map<string, string list> }

/// `file` as the index names it: repo-relative, `/`-separated.
let relativeTo (repoRoot: string) (file: string) : string =
    let full =
        Path.GetFullPath(
            if Path.IsPathRooted file then
                file
            else
                Path.Combine(repoRoot, file)
        )

    Path.GetRelativePath(Path.GetFullPath repoRoot, full).Replace('\\', '/')

/// Explain `rel` over an open index.
let explain (db: Database) (scope: TestsScope) (rel: string) : Explanation =
    let index: SymbolFreeEdit.Index =
        { SymbolsIn = fun rel -> db.GetSymbolsInFile rel |> List.map _.FullName |> List.distinct
          TestsIn = db.GetTestMethodsInFile
          Affected = db.QueryAffectedTests }

    let runnable project =
        Set.isEmpty scope.Runnable || Set.contains project scope.Runnable

    { File = rel
      Selection = SymbolFreeEdit.explainFile runnable index rel
      AlwaysClasses = scope.AlwaysClasses }

let private byProject (classes: Map<string, string list>) =
    classes
    |> Map.toList
    |> List.map (fun (project, classes) ->
        {| project = project
           classes = Array.ofList classes |})
    |> Array.ofList

/// The answer as the versioned JSON document `--json` prints.
let toJson (e: Explanation) : string =
    match e.Selection with
    | None ->
        JsonSerializer.Serialize
            {| file = e.File
               indexed = false
               alwaysClasses = byProject e.AlwaysClasses |}
    | Some s ->
        JsonSerializer.Serialize
            {| file = e.File
               indexed = true
               seeds = s.Seeds.Length
               coveringTests = s.CoveringTests
               classes = byProject s.Classes
               alwaysClasses = byProject e.AlwaysClasses
               seedFanout = SeedFanoutReading.wire s.Fanout |}

/// The answer as the lines a person reads.
let format (e: Explanation) : string list =
    let projectLines (classes: Map<string, string list>) =
        classes
        |> Map.toList
        |> List.map (fun (project, classes) -> $"""  %s{project}: %s{String.concat ", " classes}""")

    let always =
        if Map.isEmpty e.AlwaysClasses then
            []
        else
            "Every filtered run also carries the declared alwaysClasses:"
            :: projectLines e.AlwaysClasses

    match e.Selection with
    | None ->
        [ $"The impact index holds no symbol for %s{e.File}: check cannot narrow an edit to it, so every \
            project runs in full. (A file outside the build, or not yet scanned, reads this way.)" ]
    | Some s ->
        let classCount = s.Classes |> Map.values |> Seq.sumBy List.length

        let selected =
            if classCount = 0 then
                [ $"Editing %s{e.File} without changing a symbol selects no class: no configured test \
                    covers its %d{s.Seeds.Length} symbol(s)." ]
            else
                $"Editing %s{e.File} without changing a symbol selects %d{classCount} class(es) \
                  in %d{Map.count s.Classes} project(s), covering its %d{s.Seeds.Length} symbol(s) \
                  through %d{s.CoveringTests} indexed test method(s); the class filter runs every \
                  test in those classes:"
                :: projectLines s.Classes

        let fanout =
            match s.Fanout with
            | SeedFanoutReading.Top top when not (List.isEmpty top) ->
                "Its symbols that alone select the most tests:"
                :: (top |> List.map (fun f -> $"  %s{f.Seed}  %d{f.Tests}"))
            | SeedFanoutReading.OverBudget seeds ->
                [ $"Its %d{seeds} symbols exceed the per-symbol ranking budget; no symbol is ranked." ]
            | _ -> []

        selected
        @ always
        @ fanout
        @ [ "Changing a symbol selects only the classes covering that symbol, a subset of these." ]

/// Execute `fshw explain`. Injectable like `DeadCode.run`, so the refusals are testable
/// without a real index. Exit 0 with an answer; 1 when there is no index this build can
/// read, or no tests are configured.
let run
    (fileExists: string -> bool)
    (readVersion: string -> int)
    (loadScope: string -> TestsScope option)
    (print: string -> unit)
    (eprint: string -> unit)
    (repoRoot: string)
    (opts: ExplainOptions)
    : int =
    let dbPath = Path.Combine(repoRoot, DeadCode.DbRelativePath)

    if not (fileExists dbPath) then
        eprint DeadCode.missingDbMessage
        1
    else
        let version = readVersion dbPath

        if not (DeadCode.isCompatibleSchema version) then
            eprint (DeadCode.incompatibleSchemaMessage version)
            1
        else
            match loadScope repoRoot with
            | None ->
                eprint "No tests are configured in .fshw.json, so no edit selects any test."
                1
            | Some scope ->
                // Schema-compatible: `Database.create` opens it without recreating.
                let db = Database.create (dbPath, scope.Selection)
                let explained = explain db scope (relativeTo repoRoot opts.File)

                if opts.Json then
                    print (toJson explained)
                else
                    for line in format explained do
                        print line

                0

/// Default `run` wiring: real filesystem, schema probe and config, stdout/stderr.
let runDefault (repoRoot: string) (opts: ExplainOptions) : int =
    run File.Exists DeadCode.readSchemaVersion TestsScope.load (printfn "%s") (eprintfn "%s") repoRoot opts
