/// Where a repository host's session checks each of its projects: under the repository's
/// virtual root, shared with every session whose project is the same, or at its own
/// paths.
module FsHotWatch.SessionFrames

open System.Collections.Concurrent
open System.IO
open FsHotWatch

/// The frame choice for the session of the worktree at `realRoot`.
///
/// A project is checked under `virtualRoot` when nothing in it reads its own location
/// (`FrameExclusions`) and its content is the repository's canonical content for it
/// (`CanonicalProjects`). Otherwise it is checked where it is, and a project excluded
/// by what it contains is logged once, naming the construct and the file.
let choice
    (registry: CanonicalProjects.Registry)
    (realRoot: string)
    (virtualRoot: string)
    (log: string -> unit)
    : PathFrame.FrameChoice =
    let frame = PathFrame.create realRoot virtualRoot

    let exclusions =
        ConcurrentDictionary<string * string, FrameExclusions.Exclusion option>()

    let reported = ConcurrentDictionary<string, unit>()

    fun options closure ->
        let project =
            Path.GetRelativePath(realRoot, options.ProjectFileName).Replace('\\', '/')

        // Scanned again only when the project's content changes: the closure hashes it.
        let exclusion =
            exclusions.GetOrAdd((project, closure), fun _ -> FrameExclusions.exclusionFor File.ReadAllText options)

        match exclusion with
        | Some excluded ->
            let said = FrameExclusions.Exclusion.describe excluded

            if reported.TryAdd($"%s{project}|%s{said}", ()) then
                log $"%s{project} is checked at its own paths, not shared with other worktrees: it uses %s{said}"

            None
        | None when registry.Claim(project, closure, realRoot) -> Some frame
        | None -> None
