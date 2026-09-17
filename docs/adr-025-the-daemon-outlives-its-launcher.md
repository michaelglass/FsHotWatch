# ADR-025: The daemon outlives its launcher; it owns its loaded-config identity

Status: Accepted (2026-09-17)

## Context

`fshw` launched its daemon with `/bin/sh -c "nohup … &"`. A non-interactive shell has no
job control, so the daemon stayed in the CALLER's process group: signalling that group (a
closing terminal, a test runner or agent harness tearing down its tree) killed the daemon.
Measured before the fix: the launched child's process group equalled the test host's, and
an isolated caller killed by a group signal took its child with it.

Separately, only the launcher wrote `.fshw/config.hash`. A daemon started directly
(`fshw start` from a fixture or service manager) had none, read as a config change, and was
replaced by the first `check`.

## Decision

- **Detached launch.** A short-lived copy of the CLI (`--internal-detached-launch`) calls
  `setsid`, then `execv`s the existing launch shell. The caller waits a bounded time
  (30 s, the CLI's own daemon start-up deadline), kills and reaps a stuck helper, and
  reports a failed `setsid`, `execv` or shell as a failed start. stdin is `/dev/null`.
  The decision logic (argv encoding, failure classification, host resolution) is pure and
  tested; only the two `libc` imports are native.
- **Not through `ProcessHelper`.** Its child environment policy strips `DOTNET_ROOT_<arch>`
  and forces `MSBUILDDISABLENODEREUSE`, and that environment would pass through `execv` to
  the daemon. An apphost daemon may locate its runtime through exactly that variable. The
  spawn carries a `FSHW-SPAWN-001 ok:` justification instead.
- **Identity.** The daemon writes the hash of the `.fshw.json` text it PARSED before its
  pipe listens. The launcher writes nothing.
- **Pipe release.** `IpcServer.start` returns only once its name refuses connections. On
  Unix every server stream, a connected one included, holds the shared listening socket,
  and disposing the last one does not close it at once: a cancelled accept still holds the
  handle (measured: about 1 ms, in about half of shutdowns). The server stops respawning
  acceptors, awaits them, drains connections (5 s) then closes them, and waits (1 s) for a
  refused connection.
- **No inline teardown.** `RunWithIpc`'s cancellation continuation runs asynchronously.
  Inline, a `Shutdown` RPC ran the whole teardown, including the wait for the IPC server,
  which waits on that RPC's own connection, before it could reply.

## Rejected

- `setsid` in the caller: it moves the CLI itself, and fails for a group leader.
- A parent-side `setpgid` racing the child's `exec`.
- Forking the multithreaded runtime.
- A `setsid` executable: macOS has none.
- Python or Perl in the launcher.
- `ProcessStartInfo.CreateNewProcessGroup`: Windows-only on .NET 10. `StartDetached` is
  .NET 11.
- A 5 s helper bound: a loaded box must not turn into a failed launch.
- A 500 ms connection drain: under a parallel test run it closed the `Shutdown` reply's
  connection before the reply arrived.

## Not established

Linux was not measured. The helper, the group-survival test and the coverage floors were
verified on macOS only.
