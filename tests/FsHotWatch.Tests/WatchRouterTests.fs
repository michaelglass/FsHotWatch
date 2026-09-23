/// Routing one shared file-event stream to the worktree sessions under it: a file event
/// reaches exactly one session — the one whose root is the longest segment-boundary
/// prefix of the path — or none, when the path lies in a nested worktree nobody has
/// attached. A must-scan of a directory reaches every session that directory covers,
/// each bounded to its own subtree.
module FsHotWatch.Tests.WatchRouterTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.WatchRouter

let private noNested (_: string) = false

let private table (roots: (string * string) list) =
    roots
    |> List.fold (fun t (root, key) -> RoutingTable.attach root key t) RoutingTable.empty

[<Fact(Timeout = 5000)>]
let ``a path routes to the session whose root contains it`` () =
    let t = table [ "/r", "primary" ]
    test <@ RoutingTable.route noNested "/r/src/A.fs" t = Some "primary" @>

[<Fact(Timeout = 5000)>]
let ``a path under no attached root routes to no session`` () =
    let t = table [ "/r", "primary" ]
    test <@ RoutingTable.route noNested "/elsewhere/src/A.fs" t = None @>

[<Fact(Timeout = 5000)>]
let ``the longest attached root wins, so a nested worktree owns its own files`` () =
    let t = table [ "/r", "primary"; "/r/.workspaces/b", "b" ]
    test <@ RoutingTable.route noNested "/r/.workspaces/b/src/A.fs" t = Some "b" @>
    test <@ RoutingTable.route noNested "/r/src/A.fs" t = Some "primary" @>

[<Fact(Timeout = 5000)>]
let ``a root is matched on a path-segment boundary, never as a string prefix`` () =
    let t = table [ "/r/.workspaces/a", "a"; "/r/.workspaces/ab", "ab" ]
    test <@ RoutingTable.route noNested "/r/.workspaces/ab/src/A.fs" t = Some "ab" @>
    test <@ RoutingTable.route noNested "/r/.workspaces/a/src/A.fs" t = Some "a" @>
    test <@ RoutingTable.route noNested "/r/.workspaces/abc/src/A.fs" t = None @>

[<Fact(Timeout = 5000)>]
let ``the root directory itself belongs to its session`` () =
    let t = table [ "/r", "primary" ]
    test <@ RoutingTable.route noNested "/r" t = Some "primary" @>

[<Fact(Timeout = 5000)>]
let ``a trailing separator on an attached root does not change what it owns`` () =
    let t = table [ "/r/", "primary" ]
    test <@ RoutingTable.route noNested "/r/src/A.fs" t = Some "primary" @>
    test <@ RoutingTable.route noNested "/rr/src/A.fs" t = None @>

[<Fact(Timeout = 5000)>]
let ``a path inside an unattached nested worktree routes to no session, not to the enclosing one`` () =
    let t = table [ "/r", "primary" ]
    let isWorktreeRoot dir = dir = "/r/.workspaces/x"
    test <@ RoutingTable.route isWorktreeRoot "/r/.workspaces/x/src/A.fs" t = None @>
    test <@ RoutingTable.route isWorktreeRoot "/r/src/A.fs" t = Some "primary" @>

[<Fact(Timeout = 5000)>]
let ``the nested-worktree probe is asked only about directories strictly between the root and the path`` () =
    let t = table [ "/r", "primary" ]
    let asked = Collections.Generic.List<string>()

    let probe dir =
        asked.Add dir
        false

    RoutingTable.route probe "/r/src/Lib/A.fs" t |> ignore
    test <@ Set.ofSeq asked = set [ "/r/src"; "/r/src/Lib" ] @>

[<Fact(Timeout = 5000)>]
let ``a detached session no longer receives events`` () =
    let t =
        table [ "/r", "primary"; "/r/.workspaces/b", "b" ] |> RoutingTable.detach "b"

    test <@ RoutingTable.route noNested "/r/.workspaces/b/src/A.fs" t = Some "primary" @>
    test <@ RoutingTable.sessions t = [ "primary" ] @>

[<Fact(Timeout = 5000)>]
let ``re-attaching a key moves it to its new root`` () =
    let t = table [ "/r/one", "k" ] |> RoutingTable.attach "/r/two" "k"
    test <@ RoutingTable.route noNested "/r/one/A.fs" t = None @>
    test <@ RoutingTable.route noNested "/r/two/A.fs" t = Some "k" @>

[<Fact(Timeout = 5000)>]
let ``every path routes to at most one session, and it is the longest containing root`` () =
    // A deterministic sweep standing in for a property test: every path over a small
    // alphabet of segments, against roots that nest, share string prefixes, and sit
    // side by side.
    let roots = [ "/r", "r"; "/r/a", "ra"; "/r/ab", "rab"; "/r/a/b", "rab2"; "/s", "s" ]

    let t = table roots
    let segments = [ "a"; "ab"; "b"; "x" ]

    let paths =
        [ for s1 in segments do
              for s2 in segments do
                  for top in [ "/r"; "/s"; "/t" ] do
                      yield $"%s{top}/%s{s1}/%s{s2}/F.fs" ]

    let contains (root: string) (path: string) =
        path = root || path.StartsWith(root + "/")

    for path in paths do
        let expected =
            roots
            |> List.filter (fun (root, _) -> contains root path)
            |> List.sortByDescending (fun (root, _) -> root.Length)
            |> List.tryHead
            |> Option.map snd

        test <@ RoutingTable.route noNested path t = expected @>

[<Fact(Timeout = 5000)>]
let ``a must-scan inside one session's tree reaches only that session, for that directory`` () =
    let t = table [ "/r", "primary"; "/r/.workspaces/b", "b" ]
    test <@ RoutingTable.routeMustScan noNested "/r/src/Lib" t = [ "primary", "/r/src/Lib" ] @>

[<Fact(Timeout = 5000)>]
let ``a must-scan of an ancestor reaches every session beneath it, each bounded to its own root`` () =
    let t = table [ "/r", "primary"; "/r/.workspaces/b", "b"; "/elsewhere", "far" ]

    test <@ RoutingTable.routeMustScan noNested "/r" t |> List.sort = [ "b", "/r/.workspaces/b"; "primary", "/r" ] @>

    test <@ RoutingTable.routeMustScan noNested "/" t |> List.map fst |> List.sort = [ "b"; "far"; "primary" ] @>

[<Fact(Timeout = 5000)>]
let ``a must-scan inside an unattached nested worktree reaches no session`` () =
    let t = table [ "/r", "primary" ]
    let isWorktreeRoot dir = dir = "/r/.workspaces/x"
    test <@ List.isEmpty (RoutingTable.routeMustScan isWorktreeRoot "/r/.workspaces/x/src" t) @>

[<Fact(Timeout = 5000)>]
let ``the empty table routes nothing`` () =
    test <@ RoutingTable.route noNested "/r/A.fs" RoutingTable.empty = None @>
    test <@ List.isEmpty (RoutingTable.routeMustScan noNested "/" RoutingTable.empty) @>
    test <@ List.isEmpty (RoutingTable.sessions RoutingTable.empty) @>

[<Fact(Timeout = 5000)>]
let ``a path is a worktree-metadata path when a segment is .jj or .git, and names that worktree's root`` () =
    test <@ worktreeRootOfMetadataPath "/r/.workspaces/x/.jj/working_copy/checkout" = Some "/r/.workspaces/x" @>
    test <@ worktreeRootOfMetadataPath "/r/.workspaces/y/.git" = Some "/r/.workspaces/y" @>
    test <@ worktreeRootOfMetadataPath "/r/src/A.fs" = None @>
    test <@ worktreeRootOfMetadataPath "/r/src/.jjx/A.fs" = None @>
