/// Whether a daemon owns its process or is a session of a repository host is decided
/// in exactly one place: `src/FsHotWatch/DaemonHosting.fs`.
///
/// Both modes run one Daemon lifecycle, and hosting subtracts a named set of seams from
/// it (`HostingSeams`). A branch on the mode anywhere else is a second path: the next
/// feature added beside it lands on one side and is silently skipped in the other. So
/// the rest of `src/` asks the seam a question, and this test refuses any line that
/// names a mode case, matches on a hosting value, or forks on whether a CLI invocation
/// has a host link.
module FsHotWatch.Tests.HostingSeamTests

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The seam, repo-relative.
let private seam = "src/FsHotWatch/DaemonHosting.fs"

/// A line of code with its comment removed. A `//` preceded by `:` is a URL.
let private codeOf (line: string) =
    let m = Regex.Match(line, @"(?<!:)//")
    if m.Success then line.Substring(0, m.Index) else line

/// Naming a hosting or resource-scope case, matching on a hosting value, or forking on
/// an optional host link.
let private modeBranch =
    Regex(
        @"\b(Hosting\.(Standalone|Hosted)|ResourceScope\.(Process|Host))\b"
        + @"|\bmatch\b.*\b(hosting|Hosting)\b.*\bwith\b"
        + @"|\blink\.Is(Some|None)\b|\bmatch\s+link\s+with\b|\bHostLink\s+option\b"
    )

/// Every line outside the seam that branches on the mode, as `file:line: code`.
let private strayBranches (root: string) =
    Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> relative <> seam && not (relative.Contains "/obj/"))
    |> Seq.collect (fun (relative, path) ->
        File.ReadAllLines path
        |> Seq.mapi (fun i line -> i + 1, line)
        |> Seq.filter (fun (_, line) -> modeBranch.IsMatch(codeOf line))
        |> Seq.map (fun (n, line) -> $"%s{relative}:%d{n}: %s{line.Trim()}"))
    |> List.ofSeq

[<Fact>]
let ``the seam exists and names every mode case`` () =
    // PRESENT, not merely absent: a guard over a renamed or deleted seam would pass by
    // finding nothing to refuse.
    let source = File.ReadAllText(Path.Combine(repoRoot (), seam))
    test <@ source.Contains "| Standalone" && source.Contains "| Hosted" @>
    test <@ source.Contains "| Process" && source.Contains "| Host" @>

[<Fact>]
let ``no code outside the seam branches on hosting`` () =
    let stray = strayBranches (repoRoot ())

    if not (List.isEmpty stray) then
        Assert.Fail(
            "Hosting is branched on outside "
            + seam
            + ". Ask the seam (`DaemonHosting.seams`) instead, or add the difference to \
               `HostingSeams` there:\n"
            + String.Join("\n", stray)
        )

[<Theory>]
[<InlineData("            | Hosting.Hosted shared -> shared")>]
[<InlineData("        match opts.Hosting with")>]
[<InlineData("    scope = ResourceScope.Process && forceGcEnabled getEnv")>]
[<InlineData("            if link.IsNone && ipc.IsRunning pipeName then")>]
[<InlineData("        match link with")>]
[<InlineData("    (link: HostLink option)")>]
let ``the scan recognises a branch on the mode`` (line: string) =
    test <@ modeBranch.IsMatch(codeOf line) @>

[<Theory>]
[<InlineData("        let seams = DaemonHosting.seams opts.Hosting")>]
[<InlineData("                  Scope = ctx.Seams.ResourceScope")>]
[<InlineData("        // a Hosting.Hosted session never clears process caches")>]
[<InlineData("            link.Ensure()")>]
let ``the scan leaves questions to the seam alone`` (line: string) =
    test <@ not (modeBranch.IsMatch(codeOf line)) @>

// ---------------------------------------------------------------------------
// The checker
//
// A hosted session checks through a checker it shares with its partition's sibling
// sessions. Two things follow, and each is a guard over `src/`: nothing builds a
// checker outside the one constructor the seam hands out, and nothing drops the whole
// of a checker unless the seam has said this daemon owns it.
// ---------------------------------------------------------------------------

/// The one place a checker is built.
let private checkerConstructor = "src/FsHotWatch/Daemon.fs"

let private buildsAChecker = Regex(@"\bFSharpChecker\.Create\b")

/// Removing entries from a checker's caches. Checks already under way go on asking
/// for them, and one that recomputes a removed entry, or takes another check's
/// recomputation, sees two copies of a file's types.
let private clearsCheckerCaches =
    Regex(
        @"\.InvalidateAll\s*\(|\.ClearCaches\s*\(|\.InvalidateConfiguration\s*\(|\bClearLanguageServiceRootCaches\w*\s*\("
    )

/// The lines of `src/` whose code matches `pattern`, as (file, 1-based line, all lines).
let private matchesIn (root: string) (pattern: Regex) =
    Directory.EnumerateFiles(Path.Combine(root, "src"), "*.fs", SearchOption.AllDirectories)
    |> Seq.map (fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
    |> Seq.filter (fun (relative, _) -> not (relative.Contains "/obj/"))
    |> Seq.collect (fun (relative, path) ->
        let lines = File.ReadAllLines path

        lines
        |> Seq.mapi (fun i line -> i, line)
        |> Seq.filter (fun (_, line) -> pattern.IsMatch(codeOf line))
        |> Seq.map (fun (i, _) -> relative, i, lines))
    |> List.ofSeq

[<Fact>]
let ``nothing outside the daemon's constructor builds a checker`` () =
    let found = matchesIn (repoRoot ()) buildsAChecker
    // PRESENT: a guard over a renamed constructor would pass by finding nothing.
    test <@ found |> List.exists (fun (file, _, _) -> file = checkerConstructor) @>

    let stray =
        found
        |> List.filter (fun (file, _, _) -> file <> checkerConstructor)
        |> List.map (fun (file, i, lines) -> $"%s{file}:%d{i + 1}: %s{lines[i].Trim()}")

    if not (List.isEmpty stray) then
        Assert.Fail(
            "A checker is built outside "
            + checkerConstructor
            + ". Ask the seam (`HostingSeams.Checker`) for one instead:\n"
            + String.Join("\n", stray)
        )

[<Fact>]
let ``nothing removes entries from a checker's caches`` () =
    let found =
        matchesIn (repoRoot ()) clearsCheckerCaches
        |> List.map (fun (file, i, lines) -> $"%s{file}:%d{i + 1}: %s{lines[i].Trim()}")

    if not (List.isEmpty found) then
        Assert.Fail(
            "These remove checker cache entries that checks already under way may still \
             ask for. Move the project to a new generation instead \
             (`ProjectSnapshots.invalidate`):\n"
            + String.Join("\n", found)
        )

[<Fact>]
let ``the cache-removing guard sees each way of removing entries`` () =
    test <@ clearsCheckerCaches.IsMatch "checker.InvalidateAll()" @>
    test <@ clearsCheckerCaches.IsMatch "checker.ClearCaches()" @>
    test <@ clearsCheckerCaches.IsMatch "checker.InvalidateConfiguration(identity)" @>
    test <@ clearsCheckerCaches.IsMatch "checker.ClearLanguageServiceRootCachesAndCollectAndFinalizeAllTransients()" @>
    test <@ not (clearsCheckerCaches.IsMatch(codeOf "    // checker.InvalidateAll() would race")) @>
