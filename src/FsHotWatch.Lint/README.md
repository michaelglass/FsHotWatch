# FsHotWatch.Lint

FsHotWatch plugin for FSharpLint. Uses `lintParsedSource` with the warm
FSharpChecker's parse and check results — no re-parsing needed.

## Usage

```fsharp
let plugin = LintPlugin(configPath = "fsharplint.json") // or LintPlugin() for defaults
daemon.Register(plugin)

// Query warnings
// fs-hot-watch warnings
```
