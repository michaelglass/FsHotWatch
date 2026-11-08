# ADR-065: A stale reference assembly is repaired and built again

Status: Accepted (2026-11-05)

## Context

A dependent compiles against its dependency's published reference assembly,
`obj/<cfg>/<tfm>/ref/X.dll`. The compiler writes `refint/X.dll`, and MSBuild's
`CopyRefAssembly` copies it to `ref/` only when the two MVIDs differ.

The F# compiler derives a reference assembly's MVID from a 32-bit signature hash. That hash
folds each file in with `(acc <<< 1) + fileHash + 631`, so after 32 more files a file's
contribution has been shifted out entirely. If you change a public signature in such a file,
`refint` gets the new API but the MVID stays the same. The copy is skipped, and every
dependent compiles against the old API. This was measured under SDK 10.0.401 with
`FSharp.Compiler.Service` decompiled (`calculateSignatureHashOfFiles`,
`referenceAssemblySignatureHash`). A 34-file library whose first file gains a parameter fails
its dependent with FS0003, and the incident had the same shape: `FsHotWatch.TestPrune`'s
`TraceShadow.fs` is file 17 of 63. The integration test `RefAssemblyBuildTests` reproduces it.

If the dependent is recompiled, the result is a false red. If its inputs did not change, it
is not recompiled at all. Then the result is a false green, and the test host loads an
assembly the dependent was never checked against.

## Decision

The build plugin compares each graph project's `ref/` copy with its `refint/` copy
(`RefAssemblyFreshness`). It checks size and timestamp first, and reads the bytes only when
those differ, because an implementation-only change rewrites `refint` with the same bytes.

- Before a build, a stale copy is repaired: `refint` is copied over it, which is the copy
  that `CopyRefAssembly` skipped.
- After a build, if the build left a copy stale, it is repaired and the build runs again.
  The repaired copy is newer than every dependent's output, so the dependents recompile, and a
  recompile can rewrite the dependent's own reference assembly under the same MVID, so the
  staleness climbs the graph one tier per build. Measured on this repository, a version bump over an
  incremental tree left 10, then 7, then 1, then 0 copies stale. Rebuilds are bounded by the
  graph's project count, the longest chain a repair can climb. A copy still stale when the
  bound is spent demotes the build to `BuildArtifactsStale`, and the diagnostic says so.
- A stale copy blocks a cache replay, and a stale-artifact red is never stored: it describes the
  outputs, which the merkle over inputs cannot see, so a stored one would replay after its
  cause is gone.

## Rejected

- **`ProduceReferenceAssembly=false`.** It is not fshw's to set in a consumer's build, and it
  gives up incremental builds that skip dependents.
- **A target that always copies `refint` to `ref`.** It has the same cost: every
  implementation-only change would touch `ref`, and every dependent would rebuild.
- **Reporting without repairing.** The cached failure would replay over a tree that was never
  wrong, and a person would have to delete `ref/*.dll` by hand.

The defect itself belongs to the F# compiler's signature hash. When an SDK fixes it, the
positive control in `RefAssemblyBuildTests` fails, and that is the signal to revisit this
ADR.
