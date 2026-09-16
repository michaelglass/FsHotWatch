# Dependency-closure analyzer key: invalidation and cost measurement

Split. This WIP change keys analyzer results on the `dependency-closure`
slot (`CacheInputs.dependencyClosureHash`): the content of every source a file's check can
read. It is correct but too expensive as written.

- `DependencyClosureMeasure.fs.txt` — the scratch xUnit test that produced the numbers. It
  is stored as `.txt` so no build, format or lint glob picks it up. To re-run it, copy it into
  `tests/FsHotWatch.Tests/`, add it to the fsproj, and run
  `mise run test-direct -- --filter-class "*MeasureScratch*"`. It copies each tree's
  `.fsproj`/`.fs` files to a temp dir, then counts how many keys change when the first,
  middle or last file of a project is edited.
- `fshotwatch-invalidation.txt`, `intelligence-invalidation.txt` — the outputs (2026-09-16).
  On intelligence (1870 files), an edit to src/Intelligence invalidated 82% / 69% / 55% of
  all files (first / middle / last position), and one key pass over all files took ~60 s.

Next step: a signature-based key (earlier files' and referenced assemblies' public shape
from FCS) so a body-only edit invalidates nothing downstream.
