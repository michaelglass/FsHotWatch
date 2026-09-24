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
open FSharp.Compiler.CodeAnalysis

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

/// Every occurrence of `fromRoot` as a whole path component in `text` replaced by `toRoot`.
let private replaceRoot (fromRoot: string) (toRoot: string) (text: string) =
    Regex.Replace(text, Regex.Escape fromRoot + @"(?=$|[\\/\s'""`),:;\]]|\.(?:$|\s))", toRoot.Replace("$", "$$"))

/// `text` (a compiler option, such as `-o:<path>`) with every worktree path in it made
/// virtual.
let textToVirtual (frame: PathFrame) (text: string) : string =
    replaceRoot frame.RealRoot frame.VirtualRoot text

/// `text` (a diagnostic's message) with every virtual path in it made real. A name that
/// merely starts with the virtual root's name is left alone; a sentence's closing period
/// is not part of the path.
let textToReal (frame: PathFrame) (text: string) : string =
    replaceRoot frame.VirtualRoot frame.RealRoot text

/// `path` as FCS knows it: under `frame`'s virtual root when there is a frame.
let nameIn (frame: PathFrame option) (path: string) : string =
    frame |> Option.fold (fun p f -> toVirtual f p) path

/// `text` from FCS as the worktree reads it: its virtual paths made real when there is a
/// frame.
let textFrom (frame: PathFrame option) (text: string) : string =
    frame |> Option.fold (fun t f -> textToReal f t) text

/// Which frame a project is checked under, given its options and the hash of its
/// closure (its files, its options, and the closures of the projects it references).
/// `None`: at its own paths.
type FrameChoice = FSharpProjectOptions -> string -> PathFrame option

/// Every project at its own paths.
let realPaths: FrameChoice = fun _ _ -> None
