# ADR-007: Collapse the CLI to `check` + `status`; fix the never-run false-green

Status: Accepted (2026-06-08)

## Context

The CLI had grown a per-plugin verb for every check: `build`, `test`, `lint`,
`analyze`, `format-check`, plus `errors` (show accumulated diagnostics) and
`check` (run everything). The daemon already runs every plugin on a warm cache,
so these verbs were thin wrappers that mostly differed in which plugin they
filtered to — surface area that invited "holding it wrong."

Worse, two of those wrappers produced **false greens**. `dotnet fshw build`
followed by `errors --wait`, and `check` in daemon mode, both reported
"No errors" on a freshly-started daemon whose plugins had not yet run. The cause:
the daemon-mode poll (`IpcOutput.pollAndRender`) waited on `isAllTerminal`, which
used `PluginStatus.isQuiescent` — and `isQuiescent` treats **`Idle` as terminal**.
`Idle` means "this plugin has never run", so on a daemon that had only just
started (everything `Idle`), the poll exited immediately and reported clean
*before build/lint/analyze/test had run at all*. This repeatedly masked real
breakages during the Intelligence modularization work; `./build.fsx check`
(which has its own gate) caught them, the fshw aggregates did not.

`isQuiescent`-treats-`Idle`-as-done was itself a deliberate earlier fix: without
it, a plugin that legitimately never runs (e.g. test-prune with zero test
projects, a `file-cmd-*` plugin whose pattern matches nothing) would make the
poll hang forever. So we could not simply flip the poll to wait for `isTerminal`
(`Idle` excluded) — that trades a false-green for a hang.

## Decision

1. **Two verbs that matter: `check` (the gate) and `status` (the observer).**
   Retire `build`, `test`, `lint`, `analyze`, `format-check`, and `errors`.
   `check` triggers a full run and blocks until done; `status` reports the
   daemon's current state without triggering anything. Inspect one plugin with
   `status <plugin>`; force one to re-run with `rerun <plugin>`. `test-rerun`
   (xUnit `--filter-*` slicing) and `format` stay as distinct, non-overlapping
   verbs.

2. **Purely a surface collapse — `check`'s completeness/exit-code is unchanged.**
   `check` keeps the existing converge-then-verdict path
   (`IpcOutput.pollAndRender` → `CheckVerdict.converge`): scan, poll until every
   plugin is terminal, read diagnostics + coverage, and converge (re-scan up to
   N attempts) before mapping to an exit code — 0 clean / 1 failures / 2
   completeness-unconfirmed. That mechanism, and the false-green protection it
   provides, is owned by the `CheckVerdict` work, not by this change. This ADR
   retires the redundant *verbs*; it does not touch the check internals.

   (An earlier draft of this change rerouted `check` to `TriggerBuild` +
   `WaitForComplete` at the CLI layer and added a Ctrl-C→130 wrapper. On
   integration with the shipped `CheckVerdict.converge` — a more complete,
   coverage-aware verdict — that reroute was dropped in favour of deferring to
   the existing mechanism. A Ctrl-C/130 affordance for the blocking `check` is a
   possible future follow-up, tracked separately.)

## Why not keep the per-plugin verbs

They were filters over the one thing the daemon already does (run all plugins).
The forward-progress contract — "everything downstream of a change runs" — is a
property of `check`, not of any single-plugin verb; the per-plugin verbs only
ever showed a slice of a run that happened anyway. `status <plugin>` covers the
"just show me lint" need without pretending to be a gate.

## Consequences

- Breaking CLI change. `mise.toml`, the agent-mode banner/`next:` hints, the CLI
  README (and the docs synced from it), and `AGENTS.md` were updated. Downstream
  repos that shell out to `fshw build`/`test`/`lint`/`errors` must move to
  `fshw check` (gate) / `fshw status` (observe) when they bump the pin.
- The check-completeness machinery (`IpcOutput.pollAndRender`,
  `CheckVerdict.converge`, `IpcParsing.isAllTerminal`, the `Coverage` verdict) is
  unchanged and still drives `check` — this change removes verbs, not internals.
