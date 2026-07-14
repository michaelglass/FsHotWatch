module FsHotWatch.Tests.PluginWedgeTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.IdleExit
open FsHotWatch.PluginWedge
open FsHotWatch.Tests.TestHelpers

// AUTOMATION-147: plugin wedge detection. A plugin `started:` with no
// completion past the bound is BY DEFINITION wedged; the daemon must say so
// (periodically, in words) and recover — never serve a stale state forever,
// and never require the user to diagnose the wedge from a missing field.

let private now = DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)

let private bound = TimeSpan.FromMinutes 60.0
let private escalate = TimeSpan.FromMinutes 5.0

let private inputs running busy lastActivity =
    { Now = now
      RunningPlugins = running
      AnyBusy = busy
      LastActivityAt = lastActivity }

// ---------------------------------------------------------------------------
// resolveBound — precedence, and no way to configure "never".
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``resolveBound uses the explicit wedge override when positive`` () =
    test <@ resolveBound (Some "10") None = TimeSpan.FromSeconds 10.0 @>

[<Fact(Timeout = 15000)>]
let ``resolveBound falls back to verdict deadline plus grace`` () =
    let expected = TimeSpan.FromMinutes 60.0 + WedgeGrace
    test <@ resolveBound None None = expected @>
    // Unparseable / non-positive overrides fall back too — there is no
    // "disable the wedge detector" setting on purpose.
    test <@ resolveBound (Some "nope") None = expected @>
    test <@ resolveBound (Some "0") None = expected @>
    test <@ resolveBound (Some "-5") None = expected @>

[<Fact(Timeout = 15000)>]
let ``resolveBound derives from an overridden verdict deadline`` () =
    test <@ resolveBound None (Some "120") = TimeSpan.FromSeconds 120.0 + WedgeGrace @>

// ---------------------------------------------------------------------------
// classifyRunning — never healthy-by-default.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``classifyRunning under the bound is StillRunning — cannot tell, says so`` () =
    let since = now - TimeSpan.FromMinutes 12.0

    test <@ classifyRunning bound now since = RunningHealth.StillRunning(TimeSpan.FromMinutes 12.0) @>

[<Fact(Timeout = 15000)>]
let ``classifyRunning past the bound is Wedged by definition`` () =
    let since = now - TimeSpan.FromMinutes 65.0

    test <@ classifyRunning bound now since = RunningHealth.Wedged(since, TimeSpan.FromMinutes 65.0) @>

[<Fact(Timeout = 15000)>]
let ``classifyRunning clamps a future since to zero elapsed instead of going negative`` () =
    let since = now + TimeSpan.FromMinutes 3.0
    test <@ classifyRunning bound now since = RunningHealth.StillRunning TimeSpan.Zero @>

// ---------------------------------------------------------------------------
// Wording — the exact words both surfaces share.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``wedgedText names the start time and the absent completion`` () =
    let since = DateTime(2026, 7, 14, 11, 38, 39, DateTimeKind.Utc)
    let text = wedgedText since (TimeSpan.FromMinutes 12.0)
    test <@ text = "WEDGED: started 11:38:39, no completion in 12m 0s" @>

[<Fact(Timeout = 15000)>]
let ``stillRunningText states the absence explicitly and names the bound`` () =
    let text = stillRunningText "analyzers" (TimeSpan.FromMinutes 5.0) bound
    test <@ text.Contains "analyzers still running after 5m 0s" @>
    test <@ text.Contains "no completion posted" @>
    test <@ text.Contains "treated as wedged at 1h 0m" @>

[<Fact(Timeout = 15000)>]
let ``wedgeRecoveryMessage says what the daemon found AND what it did`` () =
    let since = DateTime(2026, 7, 14, 11, 38, 39, DateTimeKind.Utc)
    let msg = wedgeRecoveryMessage "analyzers" since (TimeSpan.FromMinutes 65.0)
    test <@ msg.Contains "daemon was wedged on 'analyzers'" @>
    test <@ msg.Contains "WEDGED: started 11:38:39" @>
    test <@ msg.Contains "restarted it" @>

[<Fact(Timeout = 15000)>]
let ``unobservableWedgeMessage admits it cannot name the plugin`` () =
    let msg = unobservableWedgeMessage (TimeSpan.FromMinutes 70.0)
    test <@ msg.Contains "cannot tell which plugin" @>
    test <@ msg.Contains "failing closed" @>

// ---------------------------------------------------------------------------
// decideTick — escalation cadence, wedge declaration, honest limits.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``decideTick does nothing for a quiet idle host`` () =
    let actions, buckets = decideTick bound escalate (inputs [] false now) Map.empty
    test <@ List.isEmpty actions @>
    test <@ Map.isEmpty buckets @>

[<Fact(Timeout = 15000)>]
let ``decideTick logs one escalation per interval crossed, not one per tick`` () =
    let since = now - TimeSpan.FromMinutes 6.0
    let running = [ "test-prune", since ]

    // First tick past the 5m mark: logs.
    let actions1, buckets1 =
        decideTick bound escalate (inputs running false now) Map.empty

    test <@ actions1 = [ TickAction.LogStillRunning("test-prune", TimeSpan.FromMinutes 6.0) ] @>

    // Same interval on the next tick: silent (already logged this bucket).
    let actions2, _ = decideTick bound escalate (inputs running false now) buckets1
    test <@ List.isEmpty actions2 @>

[<Fact(Timeout = 15000)>]
let ``decideTick stays silent before the first escalation interval`` () =
    let running = [ "build", now - TimeSpan.FromMinutes 2.0 ]
    let actions, _ = decideTick bound escalate (inputs running false now) Map.empty
    test <@ List.isEmpty actions @>

[<Fact(Timeout = 15000)>]
let ``decideTick resets escalations for a NEW run of the same plugin`` () =
    let oldSince = now - TimeSpan.FromMinutes 20.0

    let _, buckets =
        decideTick bound escalate (inputs [ "build", oldSince ] false now) Map.empty

    // The plugin finished and started a NEW run 6 minutes ago: it escalates
    // afresh — the old run's bucket must not suppress it.
    let newSince = now - TimeSpan.FromMinutes 6.0

    let actions, newBuckets =
        decideTick bound escalate (inputs [ "build", newSince ] false now) buckets

    test <@ actions = [ TickAction.LogStillRunning("build", TimeSpan.FromMinutes 6.0) ] @>
    // ...and the retired run's bucket is pruned, not accumulated.
    test <@ newBuckets.Count = 1 @>

[<Fact(Timeout = 15000)>]
let ``decideTick declares a wedge past the bound`` () =
    let since = now - TimeSpan.FromMinutes 65.0

    let actions, _ =
        decideTick bound escalate (inputs [ "analyzers", since ] false now) Map.empty

    test <@ actions = [ TickAction.DeclareWedged("analyzers", since, TimeSpan.FromMinutes 65.0) ] @>

[<Fact(Timeout = 15000)>]
let ``decideTick fails closed on busy-with-no-running-plugin past the bound`` () =
    // Work in flight, nothing Running, host silent for the whole bound: the
    // detector cannot NAME the plugin — it says that, and still recovers.
    let lastActivity = now - TimeSpan.FromMinutes 70.0
    let actions, _ = decideTick bound escalate (inputs [] true lastActivity) Map.empty

    test <@ actions = [ TickAction.DeclareUnobservableWedge(TimeSpan.FromMinutes 70.0) ] @>

[<Fact(Timeout = 15000)>]
let ``decideTick does NOT fail closed while busy host activity is recent`` () =
    // Busy with fresh activity is the normal dispatch window, not a wedge.
    let actions, _ =
        decideTick bound escalate (inputs [] true (now - TimeSpan.FromSeconds 30.0)) Map.empty

    test <@ List.isEmpty actions @>

// ---------------------------------------------------------------------------
// runTick — the live wiring: at-most-once recovery, escalations to the log.
// ---------------------------------------------------------------------------

let private deps
    (bound: TimeSpan)
    (running: unit -> (string * DateTime) list)
    (log: ResizeArray<string>)
    (wedges: ResizeArray<string>)
    : MonitorDeps =
    { Bound = bound
      EscalateEvery = escalate
      Now = fun () -> DateTime.UtcNow
      RunningPlugins = running
      AnyBusy = fun () -> false
      LastActivityAt = fun () -> DateTime.UtcNow
      Log = log.Add
      OnWedged = wedges.Add }

[<Fact(Timeout = 15000)>]
let ``runTick fires OnWedged AT MOST ONCE across ticks`` () =
    let log = ResizeArray()
    let wedges = ResizeArray()
    let since = DateTime.UtcNow - TimeSpan.FromMinutes 90.0
    let d = deps bound (fun () -> [ "analyzers", since ]) log wedges
    let latch = FireLatch.create ()
    let buckets = ref Map.empty

    test <@ runTick d latch buckets @>
    // Second tick: the latch already fired — no second recovery.
    test <@ not (runTick d latch buckets) @>
    test <@ wedges.Count = 1 @>
    test <@ wedges.[0].Contains "wedged on 'analyzers'" @>
    test <@ wedges.[0].Contains "WEDGED" @>

[<Fact(Timeout = 15000)>]
let ``runTick routes escalations to the log without recovering`` () =
    let log = ResizeArray()
    let wedges = ResizeArray()
    let since = DateTime.UtcNow - TimeSpan.FromMinutes 6.0
    let d = deps bound (fun () -> [ "test-prune", since ]) log wedges
    let latch = FireLatch.create ()

    test <@ not (runTick d latch (ref Map.empty)) @>
    test <@ wedges.Count = 0 @>
    test <@ log.Count = 1 @>
    test <@ log.[0].Contains "still running after" @>
    test <@ log.[0].Contains "no completion posted" @>

[<Fact(Timeout = 15000)>]
let ``runTick never throws out of a tick — failures are logged`` () =
    let log = ResizeArray()

    let d =
        { deps bound (fun () -> failwith "boom") log (ResizeArray()) with
            Log = log.Add }

    test <@ not (runTick d (FireLatch.create ()) (ref Map.empty)) @>
    test <@ log.Count = 1 @>
    test <@ log.[0].Contains "tick failed" @>

// ---------------------------------------------------------------------------
// Breadcrumb — "say what it did" surviving the restart.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``writeBreadcrumb then consumeBreadcrumb returns the message once`` () =
    withTempDir "wedge-breadcrumb" (fun tmpDir ->
        writeBreadcrumb tmpDir "daemon was wedged on 'analyzers' — restarted it"

        test <@ consumeBreadcrumb tmpDir = Some "daemon was wedged on 'analyzers' — restarted it" @>
        // Consumed: printed once, never nags again.
        test <@ consumeBreadcrumb tmpDir = None @>
        test <@ not (File.Exists(breadcrumbPath tmpDir)) @>)

[<Fact(Timeout = 15000)>]
let ``consumeBreadcrumb is None when absent or empty`` () =
    withTempDir "wedge-breadcrumb-empty" (fun tmpDir ->
        test <@ consumeBreadcrumb tmpDir = None @>
        Directory.CreateDirectory(Path.Combine(tmpDir, ".fshw")) |> ignore
        File.WriteAllText(breadcrumbPath tmpDir, "   ")
        test <@ consumeBreadcrumb tmpDir = None @>)

// ---------------------------------------------------------------------------
// formatElapsed cadence + the remaining arms.
// ---------------------------------------------------------------------------

[<Fact(Timeout = 15000)>]
let ``formatElapsed renders seconds, minutes, and hours`` () =
    test <@ formatElapsed (TimeSpan.FromSeconds 45.0) = "45s" @>
    test <@ formatElapsed (TimeSpan.FromSeconds 63.0) = "1m 3s" @>
    test <@ formatElapsed (TimeSpan.FromMinutes 72.0) = "1h 12m" @>

[<Fact(Timeout = 15000)>]
let ``escalationBucket is zero when the interval is non-positive`` () =
    // Defensive: a zero/negative escalation interval must not divide by zero or
    // log on every single tick.
    test <@ escalationBucket TimeSpan.Zero (TimeSpan.FromMinutes 30.0) = 0 @>
    test <@ escalationBucket (TimeSpan.FromMinutes -1.0) (TimeSpan.FromMinutes 30.0) = 0 @>

[<Fact(Timeout = 15000)>]
let ``escalationBucket counts whole intervals elapsed`` () =
    let five = TimeSpan.FromMinutes 5.0
    test <@ escalationBucket five (TimeSpan.FromMinutes 4.0) = 0 @>
    test <@ escalationBucket five (TimeSpan.FromMinutes 5.0) = 1 @>
    test <@ escalationBucket five (TimeSpan.FromMinutes 12.0) = 2 @>

[<Fact(Timeout = 15000)>]
let ``ambientBound agrees with resolveBound over the live environment`` () =
    let env name =
        Environment.GetEnvironmentVariable(name: string) |> Option.ofObj

    let expected = resolveBound (env WedgeBoundEnvVar) (env "FSHW_VERDICT_DEADLINE_SEC")
    test <@ ambientBound () = expected @>
    // Whatever the environment says, the bound is always a real, positive window —
    // there is no way to configure the detector off.
    test <@ ambientBound () > TimeSpan.Zero @>

[<Fact(Timeout = 15000)>]
let ``createMonitor fires the recovery on a wedged plugin and stops after disposal`` () =
    let wedges = ResizeArray<string>()
    let since = DateTime.UtcNow - TimeSpan.FromHours 2.0

    let deps: MonitorDeps =
        { Bound = TimeSpan.FromMinutes 60.0
          EscalateEvery = TimeSpan.FromMinutes 5.0
          Now = fun () -> DateTime.UtcNow
          RunningPlugins = fun () -> [ "analyzers", since ]
          AnyBusy = fun () -> false
          LastActivityAt = fun () -> DateTime.UtcNow
          Log = ignore
          OnWedged = fun m -> lock wedges (fun () -> wedges.Add m) }

    let monitor = createMonitorWith (TimeSpan.FromMilliseconds 50.0) deps

    let deadline = DateTime.UtcNow.AddSeconds 5.0

    while lock wedges (fun () -> wedges.Count = 0) && DateTime.UtcNow < deadline do
        Threading.Thread.Sleep 25

    monitor.Dispose()

    let count = lock wedges (fun () -> wedges.Count)
    // Fired — and, thanks to the atomic latch, exactly once despite many ticks.
    test <@ count = 1 @>
    test <@ (lock wedges (fun () -> wedges.[0])).Contains "wedged on 'analyzers'" @>

[<Fact(Timeout = 15000)>]
let ``createMonitor leaves a healthy daemon completely alone`` () =
    // The over-correction guard, at the timer level: no Running plugin, nothing
    // busy — the monitor must never fire, or every idle daemon would be restarted
    // and its warm FCS cache thrown away.
    let wedges = ResizeArray<string>()
    let logs = ResizeArray<string>()

    let deps: MonitorDeps =
        { Bound = TimeSpan.FromMinutes 60.0
          EscalateEvery = TimeSpan.FromMinutes 5.0
          Now = fun () -> DateTime.UtcNow
          RunningPlugins = fun () -> []
          AnyBusy = fun () -> false
          LastActivityAt = fun () -> DateTime.UtcNow
          Log = fun m -> lock logs (fun () -> logs.Add m)
          OnWedged = fun m -> lock wedges (fun () -> wedges.Add m) }

    use _monitor = createMonitorWith (TimeSpan.FromMilliseconds 25.0) deps
    Threading.Thread.Sleep 400

    test <@ lock wedges (fun () -> wedges.Count) = 0 @>
    test <@ lock logs (fun () -> logs.Count) = 0 @>

[<Fact(Timeout = 15000)>]
let ``writeBreadcrumb never throws on an unwritable path`` () =
    // The breadcrumb is a diagnostic. A failure to write it must never take down
    // the shutdown it is describing.
    writeBreadcrumb "/nonexistent-root-dir/nope" "wedged on 'x' — restarted it"
    test <@ consumeBreadcrumb "/nonexistent-root-dir/nope" = None @>
