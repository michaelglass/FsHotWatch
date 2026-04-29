# `dotnet <tool>` truncates `NIX_PROFILES` (and other env) on nix-wrapped SDKs

## Symptom

On systems using `nixpkgs`'s `dotnet-sdk` (e.g. `devenv.nix` projects), invoking
fshw — which is a `dotnet` tool — drops the user-profile entry from
`NIX_PROFILES` (and potentially other nix env vars):

```
$ echo $NIX_PROFILES
/nix/var/nix/profiles/default /Users/me/.nix-profile

$ dotnet fshw status &
$ ps eww $(pgrep -nf 'FsHotWatch.Cli') | tr ' ' '\n' | grep '^NIX_PROFILES='
NIX_PROFILES=/nix/var/nix/profiles/default     # ← truncated
```

Downstream consequences seen in `intelligence`:

- `dotnet build` from inside fshw fails with `apphost_version=10.0.X not found`
  because the wrapped `dotnet`'s apphost lookup needs the full `NIX_PROFILES`
  to find the runtime.
- TestPrune emits "binary is stale" warnings every run because builds keep
  failing → DLLs never refresh.

## Where the truncation is *not*

We initially suspected fshw's daemon auto-launch
(`src/FsHotWatch.Cli/Program.fs:201–214`), which does:

```fsharp
let psi = ProcessStartInfo("/bin/sh", $"-c \"nohup '{exe}' tool run fshw start ...\"")
psi.UseShellExecute <- false
Process.Start(psi)
```

That's not the source. Inside the `intelligence` devenv we measured every layer:

| Invocation | Child's `NIX_PROFILES` |
|---|---|
| `/bin/sh -c "echo $NIX_PROFILES"` | full (both entries) |
| `dotnet fsi /tmp/dump.fsx` | full (both entries) |
| `Process.Start("/bin/sh", "-c \"nohup '$dotnet' fsi script &\"")` (mirrors `LaunchDaemon`) | full (both entries) |
| **`dotnet fshw status`** (registered dotnet tool) | **truncated** |

Repro recipe (run inside the affected devenv):

```bash
dotnet fshw stop
dotnet fshw status &        # auto-starts daemon
sleep 3
for pid in $(pgrep dotnet); do
  args=$(ps -p $pid -o args= 2>/dev/null)
  if echo "$args" | grep -q "FsHotWatch.Cli"; then
    ps eww "$pid" | tr ' ' '\n' | grep '^NIX_PROFILES='
  fi
done
```

The truncation reproduces on `dotnet fshw status` *before any fshw code runs*
— a tool-resolution path inside the `dotnet` driver itself is overwriting
`NIX_PROFILES` with the system default. The wrapper at
`/nix/store/...-dotnet-sdk-wrapped-*/bin/dotnet` is a plain symlink to the
real binary, so it does not run a wrapper script that could munge env;
the mangling lives inside `dotnet`'s tool-launching path on nix-wrapped SDKs.

## Why we don't fix this in fshw

- Truncation is upstream of any fshw code. `fshw status` (which never spawns
  a daemon) shows the same truncation as `fshw start`.
- The only fshw-side remediation would be to hardcode `NIX_*` passthrough,
  which papers over a real bug in the wrapping layer instead of fixing it.
- fshw's launch code (`Process.Start` with `UseShellExecute = false`)
  already inherits env correctly — verified by spawning a non-tool dotnet
  child via the exact same code path inside the affected devenv.

## Workarounds

1. **devenv shellHook**: re-export `NIX_PROFILES` after dotnet tool invocations,
   or wrap the `dotnet` shim in a tiny script that preserves env explicitly.
2. **Bypass the tool-launcher**: invoke the tool's DLL directly:
   ```bash
   dotnet ~/.nuget/packages/fshotwatch.cli/<version>/tools/net10.0/any/FsHotWatch.Cli.dll <args>
   ```
   This avoids `dotnet <toolname>`'s apphost-resolution path, which appears
   to be where the env mangling happens.
3. **File upstream**: report against
   [nixpkgs `dotnet-sdk`](https://github.com/NixOS/nixpkgs) — the wrapper
   produces `NIX_PROFILES` truncation when running registered dotnet tools.

## Tested with

- `dotnet-sdk-10.0.202` (nix-wrapped, `intelligence` repo, 2026-04-29)
- macOS 26.0 (Darwin 25.5.0)
- fshw `0.9.0-alpha.0-local101`
