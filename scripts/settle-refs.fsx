// settle-refs: copy every stale published reference assembly over its intermediate, with the
// SAME code the daemon's build plugin uses (`RefAssemblyFreshness`, AUTOMATION-1700). The
// modules are loaded from source, not from a built FsHotWatch.dll, so `mise run compile` can
// run this before anything is built. Nothing here decides what "stale" means.
//
//   dotnet fsi scripts/settle-refs.fsx [repo root]      (default: the current directory)
//
// The projects are those every `*.slnx` in the root lists, as the build plugin's are its graph's.
// Prints each reference assembly it copied, relative to the root. Exit 0: none was stale.
// Exit 10: some were, and every one was copied. Exit 1: one could not be copied (it stays stale,
// and a build over it is not to be believed). Exit 2: no solution to read.
#load "../src/FsHotWatch/Locking.fs"
#load "../src/FsHotWatch/Logging.fs"
#load "../src/FsHotWatch/SafeWalk.fs"
#load "../src/FsHotWatch/ContentHash.fs"
#load "../src/FsHotWatch/RefAssemblyFreshness.fs"

open System
open System.IO
open System.Xml.Linq
open FsHotWatch

let root =
    match fsi.CommandLineArgs |> Array.tail with
    | [| dir |] -> Path.GetFullPath dir
    | _ -> Directory.GetCurrentDirectory()

/// Every project a solution in `root` lists, by absolute path.
let projectsOf (root: string) : string list =
    Directory.GetFiles(root, "*.slnx")
    |> Array.toList
    |> List.collect (fun slnx ->
        XDocument.Load(slnx).Descendants(XName.Get "Project")
        |> Seq.choose (fun p ->
            match p.Attribute(XName.Get "Path") with
            | null -> None
            | path -> Some(Path.GetFullPath(Path.Combine(root, path.Value))))
        |> Seq.toList)
    |> List.distinct

let relative (path: string) = Path.GetRelativePath(root, path)

match projectsOf root with
| [] ->
    eprintfn "settle-refs: no *.slnx with projects in %s" root
    exit 2
| projects ->
    match RefAssemblyFreshness.stalePairs projects with
    | [] -> exit 0
    | stale ->
        let failed = RefAssemblyFreshness.repair stale
        let failedPaths = failed |> List.map (fun (p, _) -> p.Published) |> Set.ofList

        for pair in stale do
            if not (failedPaths.Contains pair.Published) then
                printfn "%s" (relative pair.Published)

        for pair, reason in failed do
            eprintfn "settle-refs: could not copy %s: %s" (relative pair.Published) reason

        exit (if List.isEmpty failed then 10 else 1)
