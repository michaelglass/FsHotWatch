/// AUTOMATION-201 — the stale-artifact preflight's own units: the repair, the durable
/// heal ledger, and the circuit breaker that turns a repair which keeps firing into a
/// finding instead of a habit.
///
/// The end-to-end ORDER tests (a stale project refuses before any suite launches, and
/// the positive control proving the same path does launch them when fresh) live in
/// `TestPrunePluginTests`, next to the plugin harness that can start real processes.
module FsHotWatch.Tests.StaleArtifactPreflightTests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Swensen.Unquote
open FsHotWatch.TestPrune
open FsHotWatch.Tests.TestHelpers

let private writeAt (path: string) (contents: string) (mtime: DateTime) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, contents)
    File.SetLastWriteTimeUtc(path, mtime)

/// A one-test-project repo in the real MSBuild output layout: `Common` is referenced
/// by `Tests`, and `Common.dll` is copied into the test project's output dir. That
/// copy is the file that goes stale in the field.
type private Synth =
    { Root: string
      Target: ArtifactFreshness.RunnerTarget
      CommonDll: string
      CommonCopy: string
      TestsSrc: string
      BuiltAt: DateTime }

let private synth (root: string) : Synth =
    let builtAt = DateTime.UtcNow.AddHours(-1.0)
    let sourcedAt = builtAt.AddMinutes(-10.0)

    let commonDir = Path.Combine(root, "Common")
    let commonOut = Path.Combine(commonDir, "bin", "Debug", "net10.0")
    let testsDir = Path.Combine(root, "Tests")
    let testsOut = Path.Combine(testsDir, "bin", "Debug", "net10.0")

    writeAt (Path.Combine(commonDir, "Common.fsproj")) "<Project Sdk=\"Microsoft.NET.Sdk\" />" sourcedAt
    writeAt (Path.Combine(commonDir, "Common.fs")) "module Common" sourcedAt
    writeAt (Path.Combine(commonOut, "Common.dll")) "COMMON-CURRENT" builtAt

    writeAt
        (Path.Combine(testsDir, "Tests.fsproj"))
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"../Common/Common.fsproj\" />\n  </ItemGroup>\n</Project>"
        sourcedAt

    writeAt (Path.Combine(testsDir, "Tests.fs")) "module Tests" sourcedAt
    writeAt (Path.Combine(testsOut, "Tests.dll")) "" builtAt
    writeAt (Path.Combine(testsOut, "Common.dll")) "COMMON-CURRENT" builtAt

    { Root = root
      Target =
        { ProjectFile = Some(Path.Combine(testsDir, "Tests.fsproj"))
          ProjectDir = testsDir
          AssemblyName = "Tests"
          BinDir = Path.Combine(testsDir, "bin", "Debug") }
      CommonDll = Path.Combine(commonOut, "Common.dll")
      CommonCopy = Path.Combine(testsOut, "Common.dll")
      TestsSrc = Path.Combine(testsDir, "Tests.fs")
      BuiltAt = builtAt }

/// Invert the copy exactly as MSBuild's incremental copy leaves it: different bytes at
/// the SAME timestamp, so no plain build re-copies it.
let private invertCopy (s: Synth) =
    File.WriteAllText(s.CommonCopy, "COMMON-STALE")
    File.SetLastWriteTimeUtc(s.CommonCopy, s.BuiltAt)

let private targets (s: Synth) = [ "Tests", s.Target ]

let private runPreflight (s: Synth) =
    StaleArtifactPreflight.run s.Root DateTime.UtcNow (targets s)

// -----------------------------------------------------------------------------
// The repair, and its boundary.
// -----------------------------------------------------------------------------

/// POSITIVE CONTROL for every "it refused" assertion below: a fresh tree is passed
/// through untouched. A preflight that refused everything would satisfy the refusal
/// tests trivially, and repair nothing.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a fresh tree is neither repaired nor refused`` () =
    withTempDir "a201-fresh" (fun tmpDir ->
        let s = synth tmpDir
        let outcome = runPreflight s

        test <@ List.isEmpty outcome.Refusals @>
        test <@ List.isEmpty outcome.Healed @>
        // No ledger entry either: nothing happened, so nothing is recorded.
        test <@ not (File.Exists(StaleArtifactPreflight.ledgerPath tmpDir)) @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: an inverted copy is repaired to its origin's bytes and recorded`` () =
    withTempDir "a201-repair" (fun tmpDir ->
        let s = synth tmpDir
        invertCopy s

        let outcome = runPreflight s

        // Repaired, so the run may proceed ...
        test <@ List.isEmpty outcome.Refusals @>
        test <@ outcome.Healed = [ s.CommonCopy ] @>
        test <@ File.ReadAllText s.CommonCopy = File.ReadAllText s.CommonDll @>

        // ... and it is on the durable ledger, which is what the breaker reads.
        let ledger = StaleArtifactPreflight.loadLedger tmpDir
        test <@ ledger |> List.exists (fun r -> r.File = s.CommonCopy) @>)

/// A stale COMPILE is not repairable and must not pretend to be. There is no file on
/// disk holding the bytes that compile would produce, so the only honest answer is a
/// refusal that names the command which does produce them.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a stale compile refuses with a remedy instead of being 'repaired'`` () =
    withTempDir "a201-compile" (fun tmpDir ->
        let s = synth tmpDir
        File.SetLastWriteTimeUtc(s.TestsSrc, s.BuiltAt.AddMinutes 30.0)

        let outcome = runPreflight s

        test <@ List.isEmpty outcome.Healed @>
        test <@ outcome.Refusals.Length = 1 @>
        test <@ outcome.Refusals.Head.Project = "Tests" @>
        test <@ outcome.Refusals.Head.Reason.Contains "dotnet build" @>)

/// The remedy text is the whole point of the "diagnoses but does not prescribe" defect,
/// and one specific wrong answer must stay out of it: restarting the daemon. The task
/// cache is file-backed and survives a restart, so `fshw stop` clears nothing — it was
/// folk knowledge that happened to correlate with a rebuild.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: every remedy names a command that works, and none says 'stop the daemon'`` () =
    let cases =
        [ ArtifactFreshness.CopyDiffersFromOrigin("/o.dll", "/c.dll")
          ArtifactFreshness.AssemblyOlderThanSource("P", "/s.fs", DateTime.UtcNow, DateTime.UtcNow)
          ArtifactFreshness.InputsUndeterminable("P", "unreadable") ]

    for case in cases do
        let remedy = StaleArtifactPreflight.remedyFor case
        test <@ remedy.Contains "dotnet build" @>
        test <@ not (remedy.Contains "fshw stop") @>
        test <@ not (remedy.ToLowerInvariant().Contains "restart") @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-516: a stale copy reports build scope before exceptional incremental recovery`` () =
    let stale =
        ArtifactFreshness.CopyDiffersFromOrigin("/origin.dll", "/consumer/copy.dll")

    let remedy = StaleArtifactPreflight.remedyFor stale

    let breaker =
        StaleArtifactPreflight.Reason.breakerTripped "/repo" "/consumer/copy.dll" 10

    test <@ remedy.Contains "consumer" @>
    test <@ remedy.Contains "plain `dotnet build`" @>
    test <@ remedy.IndexOf("plain `dotnet build`") < remedy.IndexOf("--no-incremental") @>
    test <@ breaker.Contains "origins without their consumers" @>

    // CopyDiffersFromOrigin carries no timestamps. Neither surface may invent one.
    for text in [ remedy; breaker ] do
        test <@ not (text.Contains "timestamp") @>
        test <@ not (text.Contains "mtime") @>

/// Only the copy case is healable. Pinned as a fact about the DU rather than left to
/// the reader, so adding a stale case forces a decision here instead of silently
/// inheriting "not repairable".
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: exactly one stale case is repairable`` () =
    test
        <@
            StaleArtifactPreflight.repairFor (ArtifactFreshness.CopyDiffersFromOrigin("/o.dll", "/c.dll")) = Some(
                "/o.dll",
                "/c.dll"
            )
        @>

    test
        <@
            StaleArtifactPreflight.repairFor (
                ArtifactFreshness.AssemblyOlderThanSource("P", "/s.fs", DateTime.UtcNow, DateTime.UtcNow)
            ) = None
        @>

    test <@ StaleArtifactPreflight.repairFor (ArtifactFreshness.InputsUndeterminable("P", "why")) = None @>

/// A repair that cannot be made must REFUSE, never proceed. Proceeding on bytes the
/// gate could not certify is the silent degradation the gate exists to prevent.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a repair that fails refuses the run and says so`` () =
    withTempDir "a201-repair-fails" (fun tmpDir ->
        let s = synth tmpDir
        invertCopy s
        // Read-only destination: `File.Copy(overwrite = true)` cannot write it.
        File.SetAttributes(s.CommonCopy, FileAttributes.ReadOnly)

        try
            let outcome = runPreflight s

            test <@ List.isEmpty outcome.Healed @>
            test <@ outcome.Refusals.Length = 1 @>
            test <@ outcome.Refusals.Head.Reason.Contains "repair FAILED" @>
            test <@ outcome.Refusals.Head.Reason.Contains "dotnet build" @>
        finally
            File.SetAttributes(s.CommonCopy, FileAttributes.Normal))

/// Exhausting the repair budget REFUSES rather than proceeding. Driven with a budget of
/// zero, which is the same arm a pathological tree reaches after `MaxRepairRounds`.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: exhausting the repair budget refuses rather than running unverified bytes`` () =
    withTempDir "a201-budget" (fun tmpDir ->
        let s = synth tmpDir
        invertCopy s

        let outcome =
            StaleArtifactPreflight.runWithBudget 0 tmpDir DateTime.UtcNow (targets s)

        test <@ List.isEmpty outcome.Healed @>
        test <@ outcome.Refusals.Length = 1 @>
        test <@ outcome.Refusals.Head.Reason.Contains "STILL stale" @>
        // Untouched: a run that refuses repairs nothing.
        test <@ File.ReadAllText s.CommonCopy = "COMMON-STALE" @>)

// -----------------------------------------------------------------------------
// The ledger and the circuit breaker.
// -----------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: the ledger round-trips and drops records outside the window`` () =
    withTempDir "a201-ledger" (fun tmpDir ->
        let now = DateTime.UtcNow

        let records =
            [ { StaleArtifactPreflight.At = now.AddHours(-1.0)
                StaleArtifactPreflight.File = "/recent.dll" }
              { StaleArtifactPreflight.At = now - StaleArtifactPreflight.Window - TimeSpan.FromHours 1.0
                StaleArtifactPreflight.File = "/ancient.dll" } ]

        StaleArtifactPreflight.saveLedger tmpDir now records
        let loaded = StaleArtifactPreflight.loadLedger tmpDir

        // Pruned on write, so the file cannot grow without bound.
        test <@ loaded |> List.map (fun r -> r.File) = [ "/recent.dll" ] @>)

/// An unreadable heal ledger is deliberately EMPTY, not a blocked run — the opposite
/// of `PendingVerification`, and for a reason worth pinning. That sidecar records
/// outstanding test DEBT, so an unreadable one must widen. This one records repair
/// HISTORY, and the only thing losing it can do is delay a breaker trip. Failing closed
/// here would make a corrupt diagnostic file refuse every run — a brand-new wedge, in
/// the change that exists to delete one.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a corrupt heal ledger reads as empty rather than wedging the run`` () =
    withTempDir "a201-ledger-corrupt" (fun tmpDir ->
        let path = StaleArtifactPreflight.ledgerPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "[{\"at\": \"not-a-date\"")

        test <@ List.isEmpty (StaleArtifactPreflight.loadLedger tmpDir) @>

        // And the run still works: a corrupt ledger repairs and proceeds.
        let s = synth tmpDir
        invertCopy s
        let outcome = runPreflight s
        test <@ List.isEmpty outcome.Refusals @>)

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: healsInWindow counts only this file, only inside the window`` () =
    let now = DateTime.UtcNow

    let records =
        [ { StaleArtifactPreflight.At = now.AddHours(-1.0)
            StaleArtifactPreflight.File = "/a.dll" }
          { StaleArtifactPreflight.At = now.AddHours(-2.0)
            StaleArtifactPreflight.File = "/a.dll" }
          { StaleArtifactPreflight.At = now.AddHours(-3.0)
            StaleArtifactPreflight.File = "/b.dll" }
          { StaleArtifactPreflight.At = now - StaleArtifactPreflight.Window - TimeSpan.FromHours 1.0
            StaleArtifactPreflight.File = "/a.dll" } ]

    test <@ StaleArtifactPreflight.healsInWindow now records "/a.dll" = 2 @>
    test <@ StaleArtifactPreflight.healsInWindow now records "/b.dll" = 1 @>
    test <@ StaleArtifactPreflight.healsInWindow now records "/never.dll" = 0 @>

/// Repeated repair of ONE file is a finding, not a habit. Past the threshold the
/// preflight stops absorbing the inversion and says so — naming the file, the observed
/// count, and (required, not optional) how to get moving again.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: the breaker trips on a file repaired too often, and names its own reset`` () =
    withTempDir "a201-breaker" (fun tmpDir ->
        let s = synth tmpDir
        let now = DateTime.UtcNow

        // A history of repairs of this very copy, all inside the window.
        StaleArtifactPreflight.saveLedger
            tmpDir
            now
            [ for i in 1 .. StaleArtifactPreflight.Threshold ->
                  { StaleArtifactPreflight.At = now.AddHours(-(float i))
                    StaleArtifactPreflight.File = s.CommonCopy } ]

        invertCopy s
        let outcome = StaleArtifactPreflight.run tmpDir now (targets s)

        test <@ List.isEmpty outcome.Healed @>
        test <@ outcome.Refusals.Length = 1 @>
        let reason = outcome.Refusals.Head.Reason

        // Names the file and the count, so the reader knows WHAT keeps inverting ...
        test <@ reason.Contains s.CommonCopy @>
        test <@ reason.Contains(string StaleArtifactPreflight.Threshold) @>

        // ... and names the reset IN THE MESSAGE. A hard fail with no stated way out
        // would re-create the "re-run the identical command, get the identical failure"
        // defect this whole change is about.
        test <@ reason.Contains(StaleArtifactPreflight.ledgerPath tmpDir) @>

        // It really did refuse rather than repair.
        test <@ File.ReadAllText s.CommonCopy = "COMMON-STALE" @>)

/// Trip → reset → normal operation. The reset the message names must actually work.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: the documented reset clears a tripped breaker and repairs resume`` () =
    withTempDir "a201-breaker-reset" (fun tmpDir ->
        let s = synth tmpDir
        let now = DateTime.UtcNow

        StaleArtifactPreflight.saveLedger
            tmpDir
            now
            [ for i in 1 .. StaleArtifactPreflight.Threshold ->
                  { StaleArtifactPreflight.At = now.AddHours(-(float i))
                    StaleArtifactPreflight.File = s.CommonCopy } ]

        invertCopy s
        test <@ not (List.isEmpty (StaleArtifactPreflight.run tmpDir now (targets s)).Refusals) @>

        // The reset the refusal names: delete the ledger.
        File.Delete(StaleArtifactPreflight.ledgerPath tmpDir)

        let after = StaleArtifactPreflight.run tmpDir now (targets s)
        test <@ List.isEmpty after.Refusals @>
        test <@ after.Healed = [ s.CommonCopy ] @>)

/// The window ages out on its own — the second reset route the message promises, and
/// the one that needs no human at all.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a tripped breaker ages out of its own window`` () =
    withTempDir "a201-breaker-ages" (fun tmpDir ->
        let s = synth tmpDir

        let longAgo =
            DateTime.UtcNow - StaleArtifactPreflight.Window - TimeSpan.FromDays 1.0

        // Written straight to disk (not via `saveLedger`, which prunes on write) so the
        // records exist but every one of them is outside the window.
        let path = StaleArtifactPreflight.ledgerPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        let arr = JsonArray()

        for i in 1 .. StaleArtifactPreflight.Threshold do
            let o = JsonObject()
            o.Add("at", JsonValue.Create(longAgo.AddHours(-(float i)).ToString("o")))
            o.Add("file", JsonValue.Create(s.CommonCopy))
            arr.Add(o)

        File.WriteAllText(path, arr.ToJsonString())
        test <@ (StaleArtifactPreflight.loadLedger tmpDir).Length = StaleArtifactPreflight.Threshold @>

        invertCopy s
        let outcome = StaleArtifactPreflight.run tmpDir DateTime.UtcNow (targets s)

        test <@ List.isEmpty outcome.Refusals @>
        test <@ outcome.Healed = [ s.CommonCopy ] @>)

/// THE ANTI-WEDGE PROPERTY. The breaker gates the REPAIR, not the run, so a tripped
/// breaker on a clean tree changes nothing at all. Without this the breaker would be
/// exactly the new wedge class the ticket's approval comment forbade.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a tripped breaker does NOT block a run with nothing stale in it`` () =
    withTempDir "a201-breaker-clean" (fun tmpDir ->
        let s = synth tmpDir
        let now = DateTime.UtcNow

        StaleArtifactPreflight.saveLedger
            tmpDir
            now
            [ for i in 1 .. StaleArtifactPreflight.Threshold * 3 ->
                  { StaleArtifactPreflight.At = now.AddMinutes(-(float i))
                    StaleArtifactPreflight.File = s.CommonCopy } ]

        // Nothing inverted: the tree is fresh.
        let outcome = StaleArtifactPreflight.run tmpDir now (targets s)

        test <@ List.isEmpty outcome.Refusals @>
        test <@ List.isEmpty outcome.Healed @>)

/// A bare JSON `null` where the array should be. Distinct from a parse throw: `Parse`
/// succeeds and hands back a null node, so the guard is a real arm rather than a
/// theoretical one.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a ledger holding a bare JSON null reads as empty`` () =
    withTempDir "a201-ledger-null" (fun tmpDir ->
        let path = StaleArtifactPreflight.ledgerPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "null")

        test <@ List.isEmpty (StaleArtifactPreflight.loadLedger tmpDir) @>)

/// An entry the reader cannot turn into a record — a `null` element, a missing field,
/// a non-date `at` — is SKIPPED rather than taking the whole ledger down with it. The
/// direction is deliberate and safe: a dropped record can only UNDERcount heals, so the
/// breaker trips late, never early. It cannot cause a stale run, because the launch is
/// gated by the post-repair byte re-verification, not by this file.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: unreadable ledger entries are skipped, readable neighbours survive`` () =
    withTempDir "a201-ledger-entries" (fun tmpDir ->
        let now = DateTime.UtcNow
        let path = StaleArtifactPreflight.ledgerPath tmpDir
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

        let good = JsonObject()
        good.Add("at", JsonValue.Create(now.AddHours(-1.0).ToString("o")))
        good.Add("file", JsonValue.Create("/good.dll"))

        let missingFile = JsonObject()
        missingFile.Add("at", JsonValue.Create(now.ToString("o")))

        let badDate = JsonObject()
        badDate.Add("at", JsonValue.Create("not-a-date"))
        badDate.Add("file", JsonValue.Create("/bad.dll"))

        let arr = JsonArray()
        arr.Add(null)
        arr.Add(missingFile)
        arr.Add(badDate)
        arr.Add(good)
        File.WriteAllText(path, arr.ToJsonString())

        // The one readable record survives; the three unreadable ones are dropped.
        let loaded = StaleArtifactPreflight.loadLedger tmpDir
        test <@ loaded |> List.map (fun r -> r.File) = [ "/good.dll" ] @>)

// -----------------------------------------------------------------------------
// The marker: how a downstream surface tells this module's refusal apart from the
// OTHER "waiting on build" (the build-ordering race). They need opposite remedies.
// -----------------------------------------------------------------------------

/// Every shape `Reason` can build must be recognisable, or a surface keyed off the
/// marker silently falls back to the build-ordering words — which for this cause are
/// not merely vague, they are false.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: every refusal shape this module builds is recognised as a stale-output deferral`` () =
    let messages = StaleArtifactPreflight.refusalMessages "/repo"
    // The count is asserted so that adding a `Reason` constructor without adding it to
    // `refusalMessages` cannot leave this test passing over a shrunken list.
    test <@ List.length messages = 4 @>

    for m in messages do
        test <@ StaleArtifactPreflight.isStaleOutputDeferral m @>
        // And each still says what to do — the marker replaced prose, it did not
        // displace the remedy.
        test <@ m.Contains "dotnet build" || m.Contains "root-cause" @>

/// NEGATIVE CONTROL. Without this, `isStaleOutputDeferral` could answer `true` to
/// everything and every assertion above would still pass — while the build-ordering
/// defer, which DOES settle on a re-run, got told it never would.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: a build-ordering defer is NOT a stale-output deferral`` () =
    test
        <@
            not (
                StaleArtifactPreflight.isStaleOutputDeferral
                    "P2: waiting on build — apphost not produced; tests did not run"
            )
        @>

    test <@ not (StaleArtifactPreflight.isStaleOutputDeferral "Tests failed in P2") @>
    test <@ not (StaleArtifactPreflight.isStaleOutputDeferral "") @>
    test <@ not (StaleArtifactPreflight.isStaleOutputDeferral null) @>

/// `refusalMessages` is a hand-written list, so on its own it proves only that the list
/// agrees with itself. This drives the REAL preflight into three of its refusal arms and
/// asserts the marker on what it actually emitted.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201: the refusals a real preflight emits carry the marker`` () =
    withTempDir "a201-marker-real" (fun tmpDir ->
        // (1) unrepairable — a stale compile.
        let compileStale =
            let s = synth (Path.Combine(tmpDir, "compile"))
            File.SetLastWriteTimeUtc(s.TestsSrc, s.BuiltAt.AddMinutes 30.0)
            runPreflight s

        // (2) budget exhausted.
        let budgetGone =
            let s = synth (Path.Combine(tmpDir, "budget"))
            invertCopy s
            StaleArtifactPreflight.runWithBudget 0 (Path.Combine(tmpDir, "budget")) DateTime.UtcNow (targets s)

        // (3) breaker tripped — `Threshold` prior repairs of this exact copy.
        let breakerTripped =
            let root = Path.Combine(tmpDir, "breaker")
            let s = synth root
            invertCopy s
            let now = DateTime.UtcNow

            StaleArtifactPreflight.saveLedger
                root
                now
                [ for i in 1 .. StaleArtifactPreflight.Threshold ->
                      { StaleArtifactPreflight.At = now.AddMinutes(float -i)
                        StaleArtifactPreflight.File = s.CommonCopy } ]

            StaleArtifactPreflight.run root now (targets s)

        for outcome in [ compileStale; budgetGone; breakerTripped ] do
            test <@ outcome.Refusals.Length = 1 @>
            test <@ StaleArtifactPreflight.isStaleOutputDeferral outcome.Refusals.Head.Reason @>)

// -----------------------------------------------------------------------------
// THE FLOOR: a preflight that examined nothing is not a certified-fresh tree.
// -----------------------------------------------------------------------------

/// POSITIVE CONTROL, first: full coverage has nothing to report. A `coverageReport` that
/// always warned would satisfy both tests below and teach the reader to ignore it.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201 FLOOR: a preflight that examined every runnable project says nothing`` () =
    test <@ StaleArtifactPreflight.coverageReport [ "P1"; "P2" ] [ "P1"; "P2" ] = None @>
    test <@ StaleArtifactPreflight.coverageReport [] [] = None @>

/// The regression this floor exists for: the call site resolves each config to a
/// build-output target and DROPS the ones it cannot resolve, so a derivation that broke
/// would reduce the whole gate to checking nothing while every run stayed green.
[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201 FLOOR: a preflight that examined NOTHING reports it, naming the projects`` () =
    match StaleArtifactPreflight.coverageReport [ "P1"; "P2" ] [] with
    | None -> failwith "a preflight that examined 0 of 2 projects must not read as a clean tree"
    | Some report ->
        test <@ report.Contains "0 of 2" @>
        test <@ report.Contains "P1" && report.Contains "P2" @>
        test <@ report.Contains "NOT protected" @>

[<Fact(Timeout = 15000)>]
let ``AUTOMATION-201 FLOOR: partial coverage names exactly the projects it could not examine`` () =
    match StaleArtifactPreflight.coverageReport [ "P1"; "P2"; "P3" ] [ "P1"; "P3" ] with
    | None -> failwith "an unexamined project must be named"
    | Some report ->
        test <@ report.Contains "2 of 3" @>
        test <@ report.Contains "P2" @>
        // Only the gap is named — a report that listed the covered ones too would be
        // read as the failure list.
        test <@ not (report.Contains "P1") && not (report.Contains "P3") @>
