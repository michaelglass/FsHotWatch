# A106: retain process ownership after the command leader exits

Source review, not executed evidence. Regression prepared at 3f782b6e by the
architecture reviewer; no correction has been compiled or tested yet.

`ProcessHelper.runProcessTo` starts a raw command and untracks it after observing
its exit. `Registry.terminate` also accepts `Process.HasExited` as proof that the
whole tree terminated. A shell can exit after starting a detached-from-stdio
background descendant; both paths then retire successfully while the descendant
continues. Capturing descendants after that exit cannot reconstruct lost parentage.

The proposed correction establishes an operating-system ownership boundary
before launching the command. A small dependency-free process-host executable
ships with the core package and is copied into consuming output directories.
It owns a Unix session/process group or a Windows Job Object, then launches the
actual command. It retains that boundary through command completion and cleanup.
Core remains usable without a dependency on the CLI assembly.

A private, current-user named pipe carries admission, the original command exit
code, and cleanup requests. The parent registers the host handle and its cleanup
capability before acknowledging admission. The command cannot start before that
acknowledgement. Pipe EOF means the parent disappeared and requests cleanup.
Command stdout/stderr remain separate, inherited streams; protocol bytes must
never appear in the command output or count as its first-byte liveness evidence.
Arguments travel as discrete helper arguments; the original command's Arguments
string reaches ProcessStartInfo unchanged, with no extra shell interpretation.

On Unix the helper establishes the session in its own fresh process, never by
forking or changing the daemon's process group. It terminates its own group, not
a PID recovered from historical ancestry. The parent retains the separately
reported original exit code. A missing receipt or unestablished cleanup is a
failure, never the helper's exit code reinterpreted as the command's outcome.
On Windows, assign the helper to a kill-on-close Job before spawning the command;
terminate that Job to cover descendants which outlive their immediate parent.

Registry tracks the cleanup capability alongside the original live helper handle.
ProcessHelper timeout, operation cancellation and daemon shutdown all invoke that
same capability. They cannot fall back to killing an unrelated reused PID. The
capability must handle concurrent cleanup, startup failure, pipe failure and
helper failure, with a bounded wait and retained leak evidence when termination
cannot be established.

Required verification includes the prepared parent-exits-first regression,
command success/nonzero exit, timeout and caller shutdown, child which ignores
TERM, argument/path/environment preservation, protocol failure, and a packaged
consumer where the helper is found beside the installed core assembly. Existing
stream-drain and signal-classification tests remain authoritative. Group escape
and unavailable platform containment must be represented as limitations/failures;
no claim of arbitrary hostile-process sandboxing is implied.

Sources consulted:
- https://man7.org/linux/man-pages/man2/kill.2.html (pid zero addresses caller's group)
- https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
  (job membership and kill-on-close descendant ownership)
- https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0
  (Process.Kill is not an orphan-descendant ownership primitive)
