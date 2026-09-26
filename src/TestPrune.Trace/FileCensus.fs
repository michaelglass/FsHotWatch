/// The file census: do traces see the repository files tests depend on? Run the woven app
/// in the repository and note which tests' traces hold a repository file input; run an
/// untraced copy of the build output from outside the repository and note which tests
/// break there. The two sets should agree: a test that breaks outside but read nothing
/// depends on a file the recorder missed, and a test that read the repository but passes
/// outside may be over-recorded. Self-contained: it needs no trace store.
module TestPrune.Trace.FileCensus

open System
open System.IO
open System.Text
open TestPrune.Trace.Model

/// The two sets and how far apart they are, on normalised `Class.Method` names.
type FileCensusReport =
    {
        /// Tests that failed outside the repository and not inside it.
        FailOutside: Set<string>
        /// Tests whose own or inherited scopes hold a repository file input.
        ReadsRepo: Set<string>
        /// Tests that failed in the repository (traced) too: broken, not file-dependent.
        FailInRepo: Set<string>
        SymmetricDifference: Set<string>
        /// |FailOutside Δ ReadsRepo| / |FailOutside ∪ ReadsRepo|; 0 when both are empty.
        Ratio: float
    }

/// The bar on `Ratio`.
[<Literal>]
let RatioBar = 0.05

/// `Class.Method` of a test or CTRF name: nested classes dotted, theory arguments dropped.
let normalise (name: string) : string =
    let dotted = name.Replace('+', '.')

    match dotted.IndexOf '(' with
    | -1 -> dotted
    | i -> dotted.Substring(0, i)

/// Scopes that belong to the run, never to a test that lists them.
let private runOnly = set [ "S:static-init"; "A:ambient" ]

/// Tests whose own scope, or any fixture, collection, assembly or pool scope reachable
/// through parents and links, recorded a file read, existence probe or directory listing.
let readers (dumps: ProcessDump list) : Set<string> =
    let scopes = Audit.merged dumps

    let rec reads (seen: Set<string>) (keys: string list) =
        match keys with
        | [] -> false
        | k :: rest when seen.Contains k || runOnly.Contains k || not (scopes.ContainsKey k) -> reads seen rest
        | k :: rest ->
            let s = scopes.[k]
            not s.Inputs.IsEmpty || reads (seen.Add k) (s.Parents @ s.Links @ rest)

    scopes
    |> Map.toList
    |> List.choose (fun (key, s) ->
        s.Test
        |> Option.filter (fun _ -> reads Set.empty [ key ])
        |> Option.map (fun t -> normalise (t.Class + "." + t.Method)))
    |> Set.ofList

/// Tests that failed or ended otherwise; skipped and passed tests did not break.
let failures (outcomes: TestOutcome list) : Set<string> =
    outcomes
    |> List.filter (fun o -> o.Outcome = Failed || o.Outcome = OtherOutcome)
    |> List.map (fun o -> normalise o.Name)
    |> Set.ofList

/// The report from the tests failing in the repository, those failing outside it, and
/// the tests that read the repository.
let summarize (failInRepo: Set<string>) (failedOutside: Set<string>) (readsRepo: Set<string>) : FileCensusReport =
    let outside = Set.difference failedOutside failInRepo
    let union = Set.union outside readsRepo

    let diff = Set.difference union (Set.intersect outside readsRepo)

    { FailOutside = outside
      ReadsRepo = readsRepo
      FailInRepo = failInRepo
      SymmetricDifference = diff
      Ratio =
        if union.IsEmpty then
            0.0
        else
            float diff.Count / float union.Count }

/// The bar: the two sets differ by at most 5 % of their union.
let passes (r: FileCensusReport) = r.Ratio <= RatioBar

/// The human report: the verdict, then each side of the difference.
let render (r: FileCensusReport) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.Append(s).Append('\n') |> ignore
    let verdict = if passes r then "PASS" else "FAIL"

    line
        $"file-census  %s{verdict}  ratio %.3f{r.Ratio} (bar <= %g{RatioBar})  fail-outside %d{r.FailOutside.Count}  reads-repo %d{r.ReadsRepo.Count}  fail-in-repo %d{r.FailInRepo.Count}"

    // Lists, not sets: enumerating a Set compiles a disposal null check no input takes.
    for t in Set.difference r.FailOutside r.ReadsRepo |> Set.toList do
        line $"  fails outside, reads nothing  %s{t}"

    for t in Set.difference r.ReadsRepo r.FailOutside |> Set.toList do
        line $"  reads the repo, passes outside  %s{t}"

    sb.ToString()

/// Run `exe` with a CTRF report into `resultsDir` and return the report's outcomes.
let private ctrfRun (where: string) exe appArgs env workDir resultsDir timeout =
    let args =
        appArgs
        @ [ "--report-xunit-ctrf"
            "--report-xunit-ctrf-filename"
            "census.ctrf.json"
            "--results-directory"
            resultsDir ]

    let code, output = Launch.run exe args env workDir timeout
    let report = Path.Combine(resultsDir, "census.ctrf.json")

    if File.Exists report then
        Ok(Ctrf.parse (File.ReadAllText report))
    else
        // The last lines usually say why: a test that ended the process, a crash at startup.
        let tail =
            output.Split '\n'
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.toList
            |> List.rev
            |> List.truncate 5
            |> List.rev
            |> String.concat "\n"

        Error $"no CTRF report from the run %s{where} (exit %d{code}); its output ends:\n%s{tail}"

/// Prepare the project; run the woven app in the repository with CTRF; copy (never link)
/// the original `bin/Debug/<tfm>/` under the temp directory and run it there untraced.
/// `Error` when the project cannot be prepared or a run wrote no CTRF report.
let run
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (timeout: TimeSpan)
    : Result<FileCensusReport, string> =
    TraceSession.prepareProject req
    |> Result.bind (fun launch ->
        let resultsIn = Path.Combine(req.RunDir, "file-census", "in")

        ctrfRun "in the repository" launch.Apphost appArgs launch.Env req.RepoRoot resultsIn timeout
        |> Result.bind (fun inRepo ->
            let dumps, _ = DumpReader.readDirectory launch.DumpDir
            let tfm = Path.GetFileName launch.Shadow.Dir
            let source = Path.Combine(req.ProjectDir, "bin", "Debug", tfm)
            let outside = Directory.CreateTempSubdirectory("testprune-file-census-").FullName

            try
                let copy = Path.Combine(outside, tfm)

                for f in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
                    let dst = Path.Combine(copy, Path.GetRelativePath(source, f))
                    Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
                    File.Copy(f, dst)

                ctrfRun
                    "outside the repository"
                    (Path.Combine(copy, Path.GetFileName launch.Apphost))
                    appArgs
                    Overhead.untracedEnv
                    copy
                    (Path.Combine(outside, "results"))
                    timeout
                |> Result.map (fun outsideOutcomes ->
                    summarize (failures inRepo) (failures outsideOutcomes) (readers dumps))
            finally
                Directory.Delete(outside, true)))
