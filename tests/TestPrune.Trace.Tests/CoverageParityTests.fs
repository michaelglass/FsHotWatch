/// Coverage of a woven app must report the same branch points as the untraced app: a traced
/// full run is also the run that collects coverage, and its cobertura feeds consumers'
/// branch-rate ratchets.
module TestPrune.Trace.Tests.CoverageParityTests

open System
open System.IO
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private timeout = TimeSpan.FromMinutes 5.0

/// Per (file, line) of the files under `dir`: whether the line was hit and its
/// `condition-coverage` ("" without branches), merged over the classes that report it.
let internal lineCoverage (cobertura: string) (dir: string) : Map<string * int, bool * string> =
    XDocument.Load(cobertura).Descendants(XName.Get "class")
    |> Seq.filter (fun c -> c.Attribute(XName.Get "filename").Value.Contains dir)
    |> Seq.collect (fun c ->
        let file = Path.GetFileName(c.Attribute(XName.Get "filename").Value)
        c.Descendants(XName.Get "line") |> Seq.map (fun l -> file, l))
    |> Seq.map (fun (file, l) ->
        let conditions =
            match l.Attribute(XName.Get "condition-coverage") with
            | null -> ""
            | a -> a.Value

        (file, int (l.Attribute(XName.Get "number").Value)),
        (int (l.Attribute(XName.Get "hits").Value) > 0, conditions))
    |> Seq.groupBy fst
    |> Seq.map (fun (line, xs) -> line, xs |> Seq.map snd |> Seq.max)
    |> Map.ofSeq

/// Run `exe` with MTP code coverage into `<dir>/<name>.cobertura.xml`; the path.
let private covered (exe: string) env (dir: string) (name: string) =
    let output = Path.Combine(dir, name + ".cobertura.xml")

    let code, log =
        Launch.run
            exe
            [ "--coverage"
              "--coverage-output-format"
              "cobertura"
              "--coverage-output"
              output ]
            env
            Fixtures.repoRoot
            timeout

    if code <> 0 || not (File.Exists output) then
        failwith $"coverage run %s{name} exited %d{code}:\n%s{log}"

    output

[<Fact>]
let ``a woven app's coverage keeps the branch points of union matches, field comparisons and type tests`` () =
    let projectDir = ShadowBinTests.scratchProject ()

    try
        let req: TraceSession.PrepareRequest =
            { RepoRoot = Fixtures.repoRoot
              ProjectDir = projectDir
              AssemblyName = "FxTests"
              TestProject = "FxTests"
              WeaveTests = SitesOnly
              RunDir = Directory.CreateTempSubdirectory("tp-coverage-").FullName
              VerifyTimeout = TimeSpan.FromMinutes 2.0 }

        let launch = TraceSession.prepareProject req |> Result.defaultWith failwith

        let original =
            Path.Combine(projectDir, "bin", "Debug", "net10.0", Path.GetFileName launch.Apphost)

        // FxTests reads a repository file only when the repo-root variable is set, which a
        // traced launch always sets. Nothing reads it untraced but that test, so set it there
        // too: both runs then execute the same code.
        let untracedEnv =
            Overhead.untracedEnv
            |> List.map (fun (k, v) ->
                if k = Recorder.Contract.RepoRootEnv then
                    k, req.RepoRoot
                else
                    k, v)

        let untraced =
            lineCoverage (covered original untracedEnv req.RunDir "untraced") "/FxLib/"

        let woven =
            lineCoverage (covered launch.Apphost launch.Env req.RunDir "woven") "/FxLib/"

        // Logic.fs's branching lines, each with a probe between its sequence point and
        // its branch: `match shape` on a two-case union (isinst), a record-field comparison
        // (ldfld), a type test, and `match sel` testing a case (isinst) and then, under a
        // later hidden point after calls, its list. `if s.IsCase` calls the case getter, and
        // MS CodeCoverage reports no branch after a call even untraced, so there is nothing
        // to keep there.
        let branching =
            untraced
            |> Map.filter (fun _ (_, conditions) -> conditions <> "")
            |> Map.keys
            |> Set.ofSeq

        test <@ Set.isSubset (set [ for l in [ 6; 11; 42; 52; 76 ] -> "Logic.fs", l ]) branching @>
        test <@ woven = untraced @>
    finally
        ShadowBinTests.deleteProject projectDir
