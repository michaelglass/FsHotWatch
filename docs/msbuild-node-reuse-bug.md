# MSBuild Node Reuse Causes Orphan Worker Accumulation

## Summary

When a long-running .NET process (such as the FsHotWatch daemon) repeatedly
invokes `dotnet build` as a subprocess, MSBuild's default node-reuse behavior
leaves orphan worker processes (`MSBuild.dll /nodemode:1 /nodeReuse:true`) on
the system. Successive builds spawn fresh batches of workers without reaping
prior generations. Setting `MSBUILDDISABLENODEREUSE=1` in the build's
environment prevents the accumulation.

## Reproduction (verified 2026-04-25, .NET 10.0.201, macOS arm64)

Comment out the `MSBUILDDISABLENODEREUSE=1` env injection in
`src/FsHotWatch.Build/BuildPlugin.fs`, rebuild the CLI, then:

```bash
pkill -f "MSBuild.dll.*nodemode:1"           # baseline: 0 workers
dotnet src/.../FsHotWatch.Cli.dll start &    # start daemon
for i in 1 2 3 4 5; do
    echo "// trigger $i" >> src/FsHotWatch/Daemon.fs
    sleep 8
    echo "After build $i: $(pgrep -f MSBuild.dll.*nodemode:1 | wc -l) workers"
done
```

### Observed without `MSBUILDDISABLENODEREUSE=1`

```
After build 1: 11 workers   (one per project in the solution)
After build 2: 0 workers
After build 3: 0 workers
After build 4: 11 workers
After build 5: 22 workers   (two distinct generations co-existing)
```

Final tally inspected via `ps -o lstart=`: workers from two distinct build
invocations (21:37:21 and 21:37:34) were alive simultaneously.

### Observed with `MSBUILDDISABLENODEREUSE=1`

```
After build N: ~0–11 workers
Final: 11 workers (single generation, replaced not accumulated)
```

Worker count tracks the currently-running build only; previous generations
exit before the next batch spawns.

## Why It Matters

Beyond the resource cost of orphan processes, the regression test in
`tests/FsHotWatch.Tests/BuildPluginTests.fs` records that **stale workers
served subsequent builds with bad cwd/state**, producing a silent
"Build FAILED. 0 errors" output where MSBuild reported zero projects built.
That specific failure mode is what `formatSilentFailureDiagnostic` in
`BuildPlugin.fs` is built to surface.

The "stale workers cause silent failure" claim is **not independently
reproduced** in this document — the orphan accumulation is verified, the
downstream silent-failure causation is inherited from prior debugging
sessions. Treat it as a defensive hypothesis rather than a proven chain.

## Upstream References

- [dotnet/msbuild#1709 — Failed build leaves MSBuild processes lying around](https://github.com/dotnet/msbuild/issues/1709)
- [dotnet/msbuild#5221 — VS reuses MSBuild process although node reuse is disabled](https://github.com/dotnet/msbuild/issues/5221)
- [dotnet/sdk#40930 — `--nodereuse:false` not closing all dotnet.exe processes after build/publish](https://github.com/dotnet/sdk/issues/40930)
- [dotnet/msbuild#7693 — Support disabling node reuse with an msbuild property](https://github.com/dotnet/msbuild/issues/7693)
- [Disabling MSBuild Node Reuse to Avoid File Locking Issues — awakecoding](https://awakecoding.com/posts/disabling-msbuild-node-reuse-to-avoid-file-locking-issues/)

None of these explicitly describe the silent-failure causation; they
corroborate the orphan-accumulation phenomenon.

## Fix Location

`src/FsHotWatch/ProcessHelper.fs` — `runProcessWithTimeout` injects
`MSBUILDDISABLENODEREUSE=1` whenever the command basename is `dotnet`
(or `dotnet.exe`) and the caller hasn't already set the key. This means
all callers — BuildPlugin, TestPrunePlugin's per-project `dotnet run`,
FileCommandPlugin when invoking `dotnet`, etc. — get the fix
automatically without per-plugin duplication.

Tests: `tests/FsHotWatch.Tests/ProcessHelperTests.fs` covers
`isDotnetCommand` matching and `mergeDotnetEnv` injection /
caller-precedence behavior. The pure helpers are tested directly;
spawning a real subprocess to assert the env reached it would require
an end-to-end harness (see follow-ups).

## Possible Follow-ups

- Build a regression test harness that spawns a child daemon, triggers
  N builds, and asserts the worker count stays bounded.
- Independently reproduce the silent-failure causation, or remove that
  claim from this document if it can't be verified.
