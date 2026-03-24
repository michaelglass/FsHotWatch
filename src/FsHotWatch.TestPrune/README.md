# FsHotWatch.TestPrune

FsHotWatch plugin for test impact analysis via [TestPrune](https://github.com/michaelglass/TestPrune).

When source files change, re-indexes the dependency graph using the warm
FSharpChecker and reports which tests are affected.

## Usage

```fsharp
let plugin = TestPrunePlugin(dbPath = ".test-prune.db", repoRoot = "/path/to/repo")
daemon.Register(plugin)

// Query affected tests
// fs-hot-watch affected-tests
```
