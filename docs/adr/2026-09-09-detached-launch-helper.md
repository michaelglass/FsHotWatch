# Detached daemon launch helper

Status: compiled and focused controls passed. Full group-termination survival
qualification, package/apphost qualification and full CI remain required.

Actual regression run `8bc1b2ca347e45d380cd5492c7c2e5ef` proved that the old production
nohup boundary created a live child in caller process group 88978. It did not prove
which signal or actor caused historical daemon disappearances.

The CLI now starts a short-lived copy of its own assembly using the current runtime
installation. A private entry route calls setsid inside that new child, then execv
replaces the helper with the shell that launches the daemon. A failed session change
or exec returns failure; the parent bounds helper waiting and disposes its own handle.
Executable and log paths are shell-quoted, while the existing already-rendered tool
prefix and extra argument contract is preserved. stdin is explicitly /dev/null.
The regression now also includes a filename with spaces, quotes and shell syntax.
Both variants passed in run 45d25b8e79234d03a57dcc29d8277e24. The daemon also
remained alive across completed CLI clients; a binary-identity mismatch caused one
automatic replacement, which is distinct from an unexplained disappearance.

Rejected: changing session in the caller, racing a parent-side setpgid against exec,
managed fork of the multithreaded runtime, assuming a setsid executable on macOS,
adding Python to the launcher, guessing opaque posix_spawnattr layouts, or upgrading
the runtime just for a process flag. .NET 10 CreateNewProcessGroup is Windows-only;
StartDetached is a newer .NET 11 API. The helper adds no external runtime dependency.

Sources:
- https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/setsid.2.html
- https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/exec.3.html
- https://github.com/dotnet/runtime/issues/44944
- https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/

The existing positive alive/PID/PGID fixture and owned-child cleanup remain mandatory.
The next stronger fixture must first establish its OWN outer session/group, invoke
this actual production boundary from it, terminate only that verified fixture group,
and prove the detached child remains alive. Never signal a shared daemon, the test
runner group, or a pidfile-discovered group to perform this experiment.
