# FsHotWatch CLI pin upgrade

TestPrune's merge gate is authoritative only when it runs the released
`fshotwatch.cli` version declared in `.config/dotnet-tools.json`. A locally packed,
ref-stamped CLI can diagnose a tool failure, but its result is not release evidence
for TestPrune's declared toolchain.

The `0.14.0-alpha.19` pin cannot complete `check --run-once` on this macOS host:
native watcher startup returns `FSEventStreamStart returned false`, and the exception
escapes through `RunOnceCheck -> Daemon.create -> FileWatcher.create`. The replacement
must therefore contain the FsHotWatch fallback that continues with content polling
when FSEvents cannot start.

## Required order

1. In FsHotWatch, verify the watcher fallback and the `--run-once` path, then publish
   a real `fshotwatch.cli` release. Record its exact version; do not predict it here.
2. In a separate TestPrune change, replace the manifest pin and the single
   `InlineData` version in `ToolchainPinTests.fs` with that exact released version.
   Restore through the manifest and confirm the resolved tool reports that version.
3. Run TestPrune's full `mise run ci` gate on the pin-bump tree. Read the resulting
   fshw verdict as well as the process exit code before calling the stack green.

Do not reverse steps 1 and 2, point the manifest at a local package, enable roll
forward, or use a diagnostic local build as the merge verdict. CI restores the
manifest pin, so those shortcuts verify a different toolchain from the one shipped.

