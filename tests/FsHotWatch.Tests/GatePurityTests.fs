/// A gate must be a PURE FUNCTION OF THE TREE IT JUDGES.
///
/// A verdict is content-addressed by that tree (`TreeHash`), and `.fshw.json`
/// `verdictInputs.hashed` names the gate-deciding files the discovery walk does not
/// reach — the coverage floors among them, declared for exactly this reason: "Lower one
/// and a verdict earned under the higher floor must stop applying; hashing it is the
/// only thing that makes that true." A step INSIDE the gate that rewrites one of those
/// files therefore moves the subject of the claim the gate is making, and `fshw verdict`
/// answers `StaleTree` about this repository's own check.
///
/// `mise run check` used to depend on `coverage-ratchet`, which did exactly that — and
/// did something worse on the way. When a file had drifted BELOW its floor the wrapper
/// fell back to `coverageratchet loosen`, so the inner-loop gate answered a coverage
/// regression by lowering the threshold until it passed: its success was unconditional.
/// Measured on an UNMODIFIED tree on 2026-09-21, the first `mise run check` of a fresh
/// workspace printed "Coverage below threshold for: CheckPipeline.fs,
/// OperationWatchdog.fs" and then "Loosen complete: thresholds set to current coverage",
/// lowering `OperationWatchdog.fs` branch 100 -> 91 — against a `reason` field in that
/// very entry reading "Do not re-tighten from a single lucky run." It also RAISED
/// `DepsFreshness.fs` from one local measurement, which is the move that left a
/// 2026-08-07 repair note on 32 of the file's 131 floor entries.
///
/// Observing a violation and repairing it are two jobs, and only the first belongs
/// inside a check. The plugin already knew this: `CoveragePlugin` serves
/// `coverage-ratchet` as an explicit IPC command that takes the check's own exclusive
/// slot, "because a check READS the config the ratchet REWRITES". It was the task graph
/// that put the writer in the reader's path. These tests pin the separation to the file
/// so it cannot be undone by adding one `depends` entry.
module FsHotWatch.Tests.GatePurityTests

open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

/// The tasks that decide a merge: the local inner-loop gate, and the one CI runs. Both
/// are named deliberately — the defect reached `check` alone, and "local green, CI red"
/// is the shape it took.
let private gateTasks = [ "check"; "ci" ]

/// `coverageratchet` verbs that only READ the floors file. Everything else the tool
/// offers (`ratchet`, `loosen`, `loosen-from-ci`, `baseline-lines`, `refresh-baseline`)
/// writes, and writing is what a gate may not do to its own inputs.
let private readOnlyRatchetVerbs =
    Set.ofList [ "check"; "check-json"; "targets"; "gaps" ]

let private ratchetVerbsIn mise taskName =
    Regex.Matches(commandLines mise taskName, @"coverageratchet\s+(\S+)")
    |> Seq.map (fun m -> m.Groups[1].Value)
    |> Seq.toList

/// Shell redirection, or one of the in-place-write commands a mise task could plausibly
/// reach for, AIMED AT `path`. Text analysis, and it says so: see the test that uses it.
let private writesTo (line: string) (path: string) =
    line.Contains path
    && (Regex.IsMatch(line, @">>?\s*\S*" + Regex.Escape path)
        || Regex.IsMatch(line, @"\b(tee|sed\s+-i|mv|cp)\b[^\n]*" + Regex.Escape path))

[<Fact>]
let ``no task a gate depends on invokes a coverageratchet verb that writes`` () =
    let mise = miseToml (repoRoot ())

    for gate in gateTasks do
        let verbs =
            dependencyClosure mise gate |> Set.toList |> List.collect (ratchetVerbsIn mise)

        // PRESENT, not merely absent: a gate that consults coverage at all must be seen
        // to do it, so a graph that dropped the check entirely cannot pass this by
        // saying nothing.
        test <@ not (List.isEmpty verbs) @>

        let writing =
            verbs |> List.filter (fun v -> not (Set.contains v readOnlyRatchetVerbs))

        test <@ List.isEmpty writing @>

[<Fact>]
let ``the writing ratchet tasks exist, and no gate depends on them`` () =
    let mise = miseToml (repoRoot ())

    // Tightening and loosening did not disappear; they became things a person asks for,
    // in a commit that is reviewable and that re-earns a verdict — because the floors
    // file is a hashed verdict input, moving it invalidates the green earned under the
    // old floors, which is the behaviour the declaration was added for.
    let writers = [ "coverage-ratchet"; "coverage-loosen"; "loosen-from-ci" ]
    let declared = taskNames mise
    let missing = writers |> List.filter (fun w -> not (List.contains w declared))

    test <@ List.isEmpty missing @>

    for gate in gateTasks do
        let closure = dependencyClosure mise gate
        let reachable = writers |> List.filter (fun w -> Set.contains w closure)
        test <@ List.isEmpty reachable @>

[<Fact>]
let ``the tighten task cannot loosen`` () =
    // The old wrapper ran `ratchet`, read exit 2 — "a file is below its floor" — and
    // answered it with `loosen`. A command that repairs the thing it just measured
    // reports nothing.
    let mise = miseToml (repoRoot ())

    test <@ ratchetVerbsIn mise "coverage-ratchet" = [ "ratchet" ] @>

[<Fact>]
let ``the local gate consults coverage exactly the way CI does`` () =
    // `ci` runs the command GitHub Actions runs. While the two gates disagreed about
    // coverage the local one could go green on work CI rejects — and did: `check`
    // auto-loosened where `coverage-check` fails. One task, so they cannot drift.
    let mise = miseToml (repoRoot ())

    for gate in gateTasks do
        test <@ Set.contains "coverage-check" (dependencyClosure mise gate) @>

[<Fact>]
let ``no gate task redirects, tees or copies onto a declared verdict input`` () =
    // A tripwire for the NEXT writer, which will not be `coverageratchet`. Named for
    // exactly what it inspects and no wider: it reads the task TEXT for shell
    // redirection and for four in-place-write commands, aimed at a path `.fshw.json`
    // declares gate-deciding. It does NOT establish that the gate writes nothing — a
    // tool that rewrites a declared input from inside its own process is invisible to
    // it, which is why the one that did is pinned by name above rather than left to
    // this.
    let root = repoRoot ()
    let mise = miseToml root
    let declared = declaredVerdictInputs root

    test <@ not (List.isEmpty declared) @>

    let violations =
        [ for gate in gateTasks do
              for taskName in dependencyClosure mise gate do
                  for line in (commandLines mise taskName).Split('\n') do
                      for path in declared do
                          if writesTo line path then
                              yield $"%s{taskName}: %s{path} in %s{line.Trim()}" ]

    test <@ List.isEmpty violations @>
