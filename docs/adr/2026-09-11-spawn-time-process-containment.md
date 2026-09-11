# Spawn-time process containment

ProcessHelper must retire descendants even after the original command exits.
Sampling a process tree after that exit loses ancestry; resolving historical PIDs
again also risks signalling an unrelated process. The regression starts a child,
holds the parent until both live handles are observed, and requires that child to
be dead before the supervised operation retires.

The small ProcessHost executable establishes a Unix session or Windows job before
admitting the target. A private pipe carries admission and the target's exit
receipt separately from the helper's deliberate cleanup exit. On Unix only the
live helper signals its current group (`kill(0, SIGKILL)`); the parent queries the
captured group identity and never signals a numeric historical PID. On Windows
the parent retains a job handle and can terminate through that stable handle.
Unknown cleanup is an error, and lock acquisition and cleanup waits are bounded.

Parent transport and native queries live in the core F# assembly. A separate C#
ProcessOwnership library was rejected after successful compilation still produced
a runtime FileNotFoundException in the actual descendant test. The core package
bundles only the helper DLL, runtime configuration, and dependency manifest;
its buildTransitive target copies those files for package consumers. No runtime
assembly resolver or additional library package is needed.

Disposal releases already verified ownership; it never retries termination while
unwinding another exception. ProcessHelper protects setup as well as execution,
and preserves both the primary failure and any cleanup failure. A pending native
termination can retain the ownership lock, so a later cleanup attempt uses a
bounded monitor acquisition rather than blocking indefinitely.

This is process lifecycle containment, not a security sandbox. A target that
intentionally creates a different Unix session escapes the original process
group. Windows behavior requires Windows execution evidence; passing macOS tests
does not establish it.
