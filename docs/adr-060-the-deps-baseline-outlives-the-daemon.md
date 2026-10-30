# ADR-060: the deps gate's fresh baseline outlives the daemon, bound to the assets it saw

Status: Accepted (2026-10-30)

Completes what ADR-017 deferred ("A durable, cross-process fresh-signature baseline").

## Context

`DepsFreshness` reads a project as stale when any restore input is newer than its
`obj/project.assets.json`. A content-identical rewrite (a merge or checkout rewriting
`Directory.Build.props` byte for byte) moves the mtime and nothing else. Within one
daemon the in-memory fresh baseline suppresses that reading: the dep-content signature
matches the last fresh one, so no restore runs. A NEW daemon process had no baseline,
so it restored every such project once (`dotnet restore`, a `paket restore` per lock
group, `tool restore`). AUTOMATION-1096 counted 21 to 112 "deps were stale —
auto-restored OK" lines per daemon session in the consuming repository.

ADR-017 declined to persist the baseline because that spurious restore was
load-bearing: the signature did not cover the `ProjectReference` closure, so when a
referenced project's package graph changed, only an accidental restore repaired the
referencing project's assets.

## Decision

1. **The signature covers the closure.** `depRelevantSignature` hashes the dep-relevant
   digest of the project AND of every project in its transitive `ProjectReference`
   closure, plus the ancestor dep files of all of them. A reference whose `Include`
   names an MSBuild property is not expanded; its raw text is already in the
   referencing project's digest. Project-file facts are memoized by content hash, so
   reading the closure on every evaluation costs a read and a hash per file, not a parse.
2. **The baseline is durable.** `.fshw/deps-baseline.json` records, per project, the
   signature it was fresh at. `DepsFreshness.trackerFor` is the tracker the daemon
   uses; a first sighting in a new process consults the file.
3. **An entry vouches only for the assets it was recorded with.** It also records a
   content hash of `project.assets.json`, and matches only while the assets still hash
   to it. Deps that went to another state and were restored with no daemon watching,
   then came back, leave assets the baseline never saw; those restore. The assets'
   length and write time decide only whether recording needs to hash again.
   The same binding lets a new process see drift the mtime probe cannot: an entry
   at another signature whose assets are still the recorded ones means the inputs
   moved (a referenced project's package graph, say, which the probe does not read)
   and nothing has restored since, so it restores. Changed assets mean something did.
4. **Every stale reading names its cause.** The probe logs which inputs were written
   after the assets (or that there are none), and the gate logs whether the baseline
   suppressed the restore.

An unreadable or other-schema file is no baseline, and a failed write keeps the old
one. Both fail toward a restore, never toward skipping one.

## Not done

`Import`ed `.props`/`.targets` other than the ancestor dep files are still hashed only
by the `Import` element's text, as before. Within one daemon that was already the
contract (ADR-008); the durable baseline does not widen it.
