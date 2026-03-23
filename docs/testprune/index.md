<!-- sync:intro -->
# TestPrune

Only run the tests affected by your change.

TestPrune analyzes your F# code to figure out which functions depend on
which, then uses that to skip tests that couldn't possibly be affected
by what you changed.
<!-- sync:intro:end -->

## Installation

```bash
dotnet add package TestPrune.Core
```
