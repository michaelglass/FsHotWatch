/// A test-project module value that reads a repository file while its type initializes.
module FxTests.SuiteFiles

open System
open System.IO

let rec private findUp (dir: DirectoryInfo) =
    if isNull dir then
        None
    elif File.Exists(Path.Combine(dir.FullName, "mise.toml")) then
        Some dir.FullName
    else
        findUp dir.Parent

/// The repository's licence text; empty outside the repository.
let licence =
    findUp (DirectoryInfo AppContext.BaseDirectory)
    |> Option.map (fun root -> File.ReadAllText(Path.Combine(root, "LICENSE")))
    |> Option.defaultValue ""
