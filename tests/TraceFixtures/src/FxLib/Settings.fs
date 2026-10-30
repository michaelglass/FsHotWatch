/// Module values that read a repository file while the module's type initializer runs,
/// the way a suite locates its repository root once.
module FxLib.Settings

open System
open System.IO

let rec private findUp (dir: DirectoryInfo) =
    if isNull dir then
        failwith "mise.toml not found above the binary"
    elif File.Exists(Path.Combine(dir.FullName, "mise.toml")) then
        dir.FullName
    else
        findUp dir.Parent

let private repoRoot = findUp (DirectoryInfo AppContext.BaseDirectory)

let toolConfig = File.ReadAllText(Path.Combine(repoRoot, "mise.toml"))

let toolConfigLines () = toolConfig.Split('\n').Length
