<!-- sync:intro -->
# TestPrune

F# test impact analysis. Uses FSharp.Compiler.Service to build a symbol
dependency graph, then determines which tests are affected by a code change.
Only affected tests run — unchanged code is skipped.
<!-- sync:intro:end -->

## Installation

```bash
dotnet add package TestPrune.Core
```
