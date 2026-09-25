/// Absolute paths to the fixture projects the normal solution build produces.
module TestPrune.Trace.Tests.Fixtures

open System
open System.IO

/// The repository root: the first ancestor of the test binary holding TestPrune.slnx.
let repoRoot =
    let rec up (d: DirectoryInfo) =
        if isNull d then
            failwith "TestPrune.slnx not found above the test binary"
        elif File.Exists(Path.Combine(d.FullName, "TestPrune.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

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
