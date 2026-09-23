/// Routes events from one shared file-event stream to the worktree sessions beneath it.
///
/// A repository host watches a repository root once and serves every attached worktree
/// under it from that one stream. An event must reach EXACTLY ONE session or none:
///
/// - the owner is the attached session whose root is the longest prefix of the path, on
///   a path-segment boundary (`/r/a` never owns `/r/ab/…`);
/// - a path inside a nested worktree that no session has attached belongs to nobody —
///   not to the session whose root happens to enclose it. A sibling worktree's edit is
///   never an edit to the enclosing one.
///
/// Whether a directory is a worktree root is a question about the disk, so it is asked
/// through a probe the caller supplies; everything here is pure.
///
/// Roots and paths are compared ordinally, as given. Callers pass canonical spellings
/// (`RepositoryIdentity.canonicalize` for roots; FSEvents reports real paths), so one
/// directory has one spelling on both sides.
module FsHotWatch.WatchRouter

/// True when `path` is `root` or lies beneath it, on a path-segment boundary.
let isWithin (root: string) (path: string) =
    let prefix = if root.EndsWith '/' then root else root + "/"
    path = root || path.StartsWith(prefix, System.StringComparison.Ordinal)

/// Spell a root without a trailing separator, so `/r/` and `/r` own the same paths.
/// The filesystem root `/` is its own spelling.
let private normalizeRoot (root: string) =
    if root.Length > 1 then root.TrimEnd '/' else root

/// The directories strictly between `root` and `path`: the parents of `path` that are
/// themselves beneath `root`. Empty when `path` is `root` or a direct child of it.
let private directoriesBetween (root: string) (path: string) =
    let prefix = if root.EndsWith '/' then root else root + "/"

    if path.Length <= prefix.Length then
        []
    else
        let segments = path.Substring(prefix.Length).Split '/'

        [ for depth in 1 .. segments.Length - 1 -> prefix + String.concat "/" (Array.truncate depth segments) ]

/// Attached session roots, each owning the events beneath it.
type RoutingTable<'Key when 'Key: equality> = private { Roots: (string * 'Key) list }

module RoutingTable =
    let empty<'Key when 'Key: equality> : RoutingTable<'Key> = { Roots = [] }

    /// Attach `key` at `root`. A key has one root: attaching it again moves it.
    let attach (root: string) (key: 'Key) (table: RoutingTable<'Key>) : RoutingTable<'Key> =
        let others = table.Roots |> List.filter (fun (_, k) -> k <> key)
        { Roots = (normalizeRoot root, key) :: others }

    /// Detach `key`. Its events route as if it had never been attached.
    let detach (key: 'Key) (table: RoutingTable<'Key>) : RoutingTable<'Key> =
        { Roots = table.Roots |> List.filter (fun (_, k) -> k <> key) }

    /// Every attached key.
    let sessions (table: RoutingTable<'Key>) : 'Key list = table.Roots |> List.map snd

    /// True when no worktree root lies strictly between `root` and `path`.
    let private ownedOutright (isWorktreeRoot: string -> bool) (root: string) (path: string) =
        directoriesBetween root path |> List.exists isWorktreeRoot |> not

    /// The one session a file event at `path` belongs to, or `None`.
    let route (isWorktreeRoot: string -> bool) (path: string) (table: RoutingTable<'Key>) : 'Key option =
        table.Roots
        |> List.filter (fun (root, _) -> isWithin root path)
        |> List.sortByDescending (fun (root, _) -> root.Length)
        |> List.tryHead
        |> Option.filter (fun (root, _) -> ownedOutright isWorktreeRoot root path)
        |> Option.map snd

    /// Who must rescan after a must-scan (coalesced) event on `dir`, and what: the
    /// owner of `dir` rescans `dir` itself, and every attached session beneath `dir`
    /// rescans its own root. No session is ever asked to rescan beyond its own root.
    let routeMustScan
        (isWorktreeRoot: string -> bool)
        (dir: string)
        (table: RoutingTable<'Key>)
        : ('Key * string) list =
        let dir = normalizeRoot dir

        let owner =
            route isWorktreeRoot dir table
            |> Option.map (fun key -> key, dir)
            |> Option.toList

        let beneath =
            table.Roots
            |> List.filter (fun (root, _) -> root <> dir && isWithin dir root)
            |> List.map (fun (root, key) -> key, root)

        owner @ beneath

/// When `path` lies in a worktree's VCS metadata (a `.jj` or `.git` segment), the root
/// of that worktree. A shared stream learns of nested worktrees this way: creating one
/// writes its metadata, and the write arrives as an event like any other.
let worktreeRootOfMetadataPath (path: string) : string option =
    let segments = path.Split '/'

    segments
    |> Array.tryFindIndex (fun s -> s = ".jj" || s = ".git")
    |> Option.map (fun i -> System.String.Join("/", segments, 0, i))
