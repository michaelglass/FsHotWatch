/// The fixture tree's root, found once by walking up from the binary with `File.Exists`:
/// the initializer probes for a file and reads none.
module FxTests.SuiteRoot

open System
open System.IO

/// The nearest ancestor of the binary holding `src/FxLib/FxLib.fsproj`. Outside the
/// repository there is none and the initializer throws.
let root =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then
            failwith "no src/FxLib/FxLib.fsproj above the test binary"
        elif File.Exists(Path.Combine(dir.FullName, "src", "FxLib", "FxLib.fsproj")) then
            dir.FullName
        else
            up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)
