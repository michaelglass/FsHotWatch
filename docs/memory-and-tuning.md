# Memory & tuning

The FsHotWatch daemon keeps `FSharpChecker` and its FCS caches warm — that's
what makes re-checks fast, but it also means an idle daemon holds a large warm
working set (mostly FCS-rooted native memory), on the order of ~2.8–3.1 GB. The
defaults below keep that under control; you usually don't need to touch them.

## Advanced config keys

All optional, set in `.fshw.json`:

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `idleExitMin` | `int \| false` | *auto* | Minutes of idleness after which the daemon shuts down to reclaim memory. See [Idle exit](#idle-exit). |
| `pressureIdleFloorMin` | `int \| false` | `2` | Under memory pressure, shorten an already-eligible idle-exit window to this many minutes. See [Pressure-shortened idle exit](#pressure-shortened-idle-exit). |
| `fsEventsLatencyMs` | `int` | `250` | macOS only: the FSEvents coalescing window in milliseconds. Higher = more event coalescing and lower `fseventsd` load per change, at the cost of slightly higher change-to-rebuild latency. `0` disables coalescing; ignored off macOS. |
| `timeoutSec` | `int` | — | Global default per-task timeout in seconds, used when a task has no per-entry override. See [Per-task timeouts](#per-task-timeouts). |

## Garbage collection

Keeping FCS warm produces a large amount of collectable managed churn on top of
the live working set. To stop that churn from accumulating, the CLI ships with
`System.GC.ConserveMemory=9` baked into its `runtimeconfig.json`. In benchmarks
this cut the daemon's steady footprint by ~25–40% with no measurable impact on
scan speed or diagnostics.

To override it for a single daemon process, set `DOTNET_GCConserveMemory`
(`0` = no conservation, `9` = most aggressive); the environment variable takes
precedence over the baked-in default.

## Idle exit

A daemon can shut itself down after a configurable idle period to reclaim its
warm working set. This is transparent to anyone using the CLI: the next `fshw`
command auto-starts a fresh daemon, and the file-backed check cache survives
restarts, so the next `fshw check` pays only one auto-start plus a
mostly-cache-hit scan. The daemon only exits when it has been idle — no file
events, no running plugin work — for the full window; if work is in flight at
the threshold, it defers to a later check.

This matters most when you run **more than one daemon at a time** — one per
checkout when you keep several working copies of the same repo (git worktrees,
[jj](https://jj-vcs.github.io/) workspaces, or just separate clones). Idle
checkouts can otherwise waste several gigabytes between bursts of work.

Configure it with `idleExitMin`:

- **Key absent → AUTO mode.** Enabled with a 30-minute threshold **if and only if** the repo root path contains a `/.workspaces/` segment (the convention for secondary checkouts in this ecosystem). A primary/default checkout's daemon never auto-quits.
- **`0` or `false` → disabled everywhere** (explicit opt-out, overrides AUTO).
- **Positive integer `N` → enabled with an `N`-minute threshold regardless of path** (explicit opt-in, even for a primary checkout).

```jsonc
// AUTO (the default): omit the key. Auto-on at 30min only for /.workspaces/ checkouts.
{}
```

```jsonc
// Explicit opt-in: quit after 15 minutes idle, in ANY checkout.
{ "idleExitMin": 15 }
```

```jsonc
// Explicit opt-out: never auto-quit, even in a /.workspaces/ checkout.
{ "idleExitMin": false }
```

## Pressure-shortened idle exit

[Idle exit](#idle-exit) reclaims memory from daemons that have gone *quiet*, but
on its default schedule (30 min for secondary checkouts). When the machine is
under genuine memory pressure, waiting the full window is too slow — you want
idle daemons gone *now*. Memory pressure therefore feeds into idle exit as an
input: while the machine is tight, an already-eligible daemon's idle window is
shortened to `min(idleExitMin, pressureIdleFloorMin)`, so a 30-min daemon quits
after just 2 min of idleness.

Pressure is the runtime GC's own high-load mark — it's "true" when
`GC.GetGCMemoryInfo().MemoryLoadBytes` reaches `HighMemoryLoadThresholdBytes`
(no percentage knob). It's re-evaluated on every 30s tick, not latched: if
pressure subsides before the daemon goes idle long enough, the full window is
restored.

Crucially, **pressure only ever *shortens* an already-applicable window — it
never *creates* one**. A primary checkout (whose `idleExitMin` resolves to
"off") stays exempt under pressure, exactly as it does normally. Only daemons
that would already quit on idle (a `/.workspaces/` checkout, or an explicit
`idleExitMin N`) quit *faster* under pressure.

Why shorten-and-quit rather than trim caches in place? An in-place trim of the
FCS caches keeps ~400 MB plus the whole process resident, yet the *next* edit
still pays a full cold FCS rebuild — because the file-backed check cache
survives a trim and a restart equally. So quitting strictly dominates: it
reclaims everything and the return cost is the same cold rebuild either way.
(See [`docs/adr-005-pressure-feeds-idle-exit.md`](adr-005-pressure-feeds-idle-exit.md).)

Configure it with `pressureIdleFloorMin`:

- **Key absent → floor at `2` minutes** (default-on).
- **`0` or `false` → pressure-shortening disabled** — a daemon under pressure waits its full idle-exit window, same as no pressure.
- **Positive integer `N` → floor at `N` minutes** under pressure.

```jsonc
// Default: omit the key. Under pressure, eligible daemons quit after 2min idle.
{}
```

```jsonc
// More aggressive: quit after 1min idle under pressure.
{ "pressureIdleFloorMin": 1 }
```

```jsonc
// Disabled: pressure never shortens the window; use idleExitMin as-is.
{ "pressureIdleFloorMin": false }
```

## Per-task timeouts

Any `build[]`, `tests.projects[]`, or `fileCommands[]` entry may set its own
`timeoutSec` to override the global default. When a task exceeds its timeout, the
daemon kills the child process tree, records the run with outcome `timed out`
(a distinct `⏱` glyph in the UI, `timed-out` token in agent mode), and stays
running — the next change retriggers normally.
