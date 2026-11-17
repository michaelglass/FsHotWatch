/// Absolute paths to the fixture projects the normal solution build produces.
module TestPrune.Trace.Tests.Fixtures

open System
open System.IO

/// The repository root (`RepositoryTree.root`), read only: the fixtures are built in it.
let repoRoot = FsHotWatch.TestRoots.RepositoryTree.root ()

/// The fixtures' miniature repository root (it has its own src/ and tests/).
let fixtureRoot = Path.Combine(repoRoot, "tests", "TraceFixtures")

let private built (sub: string) (project: string) =
    Path.Combine(fixtureRoot, sub, project, "bin", "Debug", "net10.0")

/// Build output of the fixture library.
let fxLibDir = built "src" "FxLib"

/// Build output of the fixture driver console app.
let fxDriverDir = built "src" "FxDriver"

/// Build output of the fixture xUnit v3 suite.
let fxTestsDir = built "tests" "FxTests"

/// A scratch directory, not yet created, for a COPY of a fixture's build output, INSIDE the
/// repository at tests/TraceFixtures/bin-scratch/<guid>. The one place a test writes into the
/// repository tree (AUTOMATION-1775 allow-list).
///
// FSHW-ROOT-001 ok: the copies must sit under the traced repository root (ADR-077). The recorder notes a
// file input only under that root (TESTPRUNE_TRACE_REPO_ROOT), the fixture suites find their
// root by walking up from their binary to mise.toml / src/FxLib/FxLib.fsproj, and the PDBs
// join to the real sources relative to it; a copy elsewhere records none of the repository
// reads these tests measure (tried 2026-11-17: 12 EndToEnd/Measurement tests went red). A
// walk of tests/ must therefore tolerate these directories vanishing (SafeWalk, AUTOMATION-1721);
// the source audits walk src/ only.
let inRepositoryScratch () : string =
    Path.Combine(fixtureRoot, "bin-scratch", Guid.NewGuid().ToString "N")
