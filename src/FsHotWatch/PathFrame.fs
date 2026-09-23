/// How a session's paths map between its worktree and the root the worktree is checked
/// under.
///
/// Every worktree of a repository is checked under one virtual root, so identical
/// content gives FCS identical inputs and its content-versioned caches share them. A
/// path under the worktree has exactly one twin under the virtual root. A path outside
/// the worktree (the SDK, the NuGet cache) is the same in every worktree and passes
/// through unchanged.
module FsHotWatch.PathFrame

open System
open System.IO
open System.Text.RegularExpressions

/// A worktree root and the virtual root it is checked under, both full paths without a
/// trailing separator.
[<NoComparison>]
type PathFrame =
    private
        { RealRoot: string
          VirtualRoot: string }

    member this.Real = this.RealRoot
    member this.Virtual = this.VirtualRoot

let private trimmed (path: string) =
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

/// The frame checking `realRoot` under `virtualRoot`.
let create (realRoot: string) (virtualRoot: string) : PathFrame =
    { RealRoot = trimmed realRoot
      VirtualRoot = trimmed virtualRoot }

/// `path` moved from under `fromRoot` to under `toRoot`, or unchanged when it is not
/// under `fromRoot`. A sibling that merely shares the prefix (`/r2` beside `/r`) is not
/// under it.
let private rebase (fromRoot: string) (toRoot: string) (path: string) =
    if String.Equals(path, fromRoot, StringComparison.Ordinal) then
        toRoot
    elif path.StartsWith(fromRoot + string Path.DirectorySeparatorChar, StringComparison.Ordinal) then
        toRoot + path.Substring fromRoot.Length
    else
        path

/// The path FCS checks `path` under.
let toVirtual (frame: PathFrame) (path: string) : string =
    rebase frame.RealRoot frame.VirtualRoot path

/// The worktree path a path FCS reported stands for.
let toReal (frame: PathFrame) (path: string) : string =
    rebase frame.VirtualRoot frame.RealRoot path

/// `text` (a diagnostic's message) with every virtual path in it made real. A name that
/// merely starts with the virtual root's name is left alone; a sentence's closing period
/// is not part of the path.
let textToReal (frame: PathFrame) (text: string) : string =
    Regex.Replace(
        text,
        Regex.Escape frame.VirtualRoot + @"(?=$|[\\/\s'""`),:;\]]|\.(?:$|\s))",
        frame.RealRoot.Replace("$", "$$")
    )
