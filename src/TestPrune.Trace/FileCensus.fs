/// The file census: do traces see the repository files tests depend on? Run the woven app
/// in the repository and note which tests' traces hold a repository file input; run a
/// copy of it from outside the repository and note which tests break there, and which
/// still reach the repository from there (an absolute path such as `__SOURCE_DIRECTORY__`
/// does). The two sets should agree: a test that depends on the repository but read
/// nothing depends on a file the recorder missed, and a test that read the repository but
/// passes outside without reaching it may be over-recorded. Self-contained: it needs no
/// trace store.
module TestPrune.Trace.FileCensus

open System
open System.IO
open System.Text
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder

/// The two sets and how far apart they are, on normalised `Class.Method` names.
type FileCensusReport =
    {
        /// Tests that failed outside the repository and not inside it.
        FailOutside: Set<string>
        /// Tests that passed outside the repository yet read a file inside it from there.
        ReachOutside: Set<string>
        /// Tests whose own or inherited scopes hold a repository file input.
        ReadsRepo: Set<string>
        /// Tests that failed in the repository (traced) too: broken, not file-dependent.
        FailInRepo: Set<string>
        SymmetricDifference: Set<string>
        /// Tests that depend on the repository (they fail outside it or reach it from
        /// there) or read it, but not both.
        /// |SymmetricDifference| / |dependents ∪ ReadsRepo|; 0 when both are empty.
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

/// Tests whose own scope, any fixture, collection, assembly or pool scope reachable through
/// parents and links, or any static-init scope they inherit (`RunScopes.staticInheritance`)
/// recorded a file read, existence probe or directory listing.
let readers (manifest: Manifest) (dumps: ProcessDump list) : Set<string> =
    let scopes = RunScopes.merged dumps
    let inherited = RunScopes.inheritedBy manifest scopes

    scopes
    |> Map.toList
    |> List.choose (fun (key, s) ->
        s.Test
        |> Option.filter (fun _ -> inherited [ key ] |> Set.exists (fun k -> not scopes.[k].Inputs.IsEmpty))
        |> Option.map (fun t -> normalise (t.Class + "." + t.Method)))
    |> Set.ofList

/// Tests that failed or ended otherwise; skipped and passed tests did not break.
let failures (outcomes: TestOutcome list) : Set<string> =
    outcomes
    |> List.filter (fun o -> o.Outcome = Failed || o.Outcome = OtherOutcome)
    |> List.map (fun o -> normalise o.Name)
    |> Set.ofList

/// The report from the tests failing in the repository, those failing outside it, those
/// reaching the repository from outside it, and the tests that read the repository.
let summarize
    (failInRepo: Set<string>)
    (failedOutside: Set<string>)
    (reachedFromOutside: Set<string>)
    (readsRepo: Set<string>)
    : FileCensusReport =
    let outside = Set.difference failedOutside failInRepo
    let reach = Set.difference (Set.difference reachedFromOutside failInRepo) outside
    let dependents = Set.union outside reach
    let union = Set.union dependents readsRepo

    let diff = Set.difference union (Set.intersect dependents readsRepo)

    { FailOutside = outside
      ReachOutside = reach
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
        $"file-census  %s{verdict}  ratio %.3f{r.Ratio} (bar <= %g{RatioBar})  fail-outside %d{r.FailOutside.Count}  reach-outside %d{r.ReachOutside.Count}  reads-repo %d{r.ReadsRepo.Count}  fail-in-repo %d{r.FailInRepo.Count}"

    // Lists, not sets: enumerating a Set compiles a disposal null check no input takes.
    for t in Set.difference r.FailOutside r.ReadsRepo |> Set.toList do
        line $"  fails outside, reads nothing  %s{t}"

    for t in Set.difference r.ReachOutside r.ReadsRepo |> Set.toList do
        line $"  reaches the repo from outside, reads nothing  %s{t}"

    for t in
        Set.difference r.ReadsRepo (Set.union r.FailOutside r.ReachOutside)
        |> Set.toList do
        line $"  reads the repo, passes outside  %s{t}"

    sb.ToString()

/// Copy every file under `source` to the same relative path under `destination`; nothing
/// when `source` does not exist (a run that ended before writing it).
let internal copyTree (source: string) (destination: string) =
    if Directory.Exists source then
        for f in Directory.GetFiles(source, "*", SearchOption.AllDirectories) do
            let dst = Path.Combine(destination, Path.GetRelativePath(source, f))
            Directory.CreateDirectory(Path.GetDirectoryName dst) |> ignore
            File.Copy(f, dst, true)

/// Prepare the project; run the woven app in the repository with CTRF; copy (never link)
/// the woven app under the temp directory and run it there, traced into its own dump
/// directory against the same repository root, so a read that still reaches the
/// repository from outside it is recorded. The outside run's dumps and CTRF report are
/// copied to `file-census/outside/` under the run directory before the temp directory is
/// deleted. `Error` when the project cannot be prepared or a run wrote no CTRF report.
let run
    (req: TraceSession.PrepareRequest)
    (appArgs: string list)
    (timeout: TimeSpan)
    : Result<FileCensusReport, string> =
    TraceSession.prepareProject req
    |> Result.bind (fun launch ->
        let resultsIn = Path.Combine(req.RunDir, "file-census", "in")

        Ctrf.run "in the repository" launch.Apphost appArgs launch.Env req.RepoRoot resultsIn timeout
        |> Result.bind (fun inRepo ->
            let manifest = launch.Shadow.Manifest
            let dumps, _ = DumpReader.readDirectory launch.DumpDir
            let tfm = Path.GetFileName launch.Shadow.Dir
            let outside = Directory.CreateTempSubdirectory("testprune-file-census-").FullName

            try
                let copy = Path.Combine(outside, tfm)
                let outsideDumps = Path.Combine(outside, "traces")

                copyTree launch.Shadow.Dir copy

                let env =
                    launch.Env
                    |> List.map (fun (k, v) -> k, (if k = Contract.OutEnv then outsideDumps else v))

                Ctrf.run
                    "outside the repository"
                    (Path.Combine(copy, Path.GetFileName launch.Apphost))
                    appArgs
                    env
                    copy
                    (Path.Combine(outside, "results"))
                    timeout
                |> Result.map (fun outsideOutcomes ->
                    let reached = readers manifest (fst (DumpReader.readDirectory outsideDumps))

                    summarize (failures inRepo) (failures outsideOutcomes) reached (readers manifest dumps))
            finally
                let kept = Path.Combine(req.RunDir, "file-census", "outside")

                for sub in [ "traces"; "results" ] do
                    copyTree (Path.Combine(outside, sub)) (Path.Combine(kept, sub))

                Directory.Delete(outside, true)))
