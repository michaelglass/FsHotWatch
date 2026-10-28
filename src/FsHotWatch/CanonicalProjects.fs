/// Which content each project of a repository is checked under at the virtual root.
///
/// FCS keeps one strong version per cache key, and a project's key is its identity. Two
/// sessions checking different content under one identity would evict each other's work
/// on every check. So one content per project is canonical, and only sessions whose
/// project matches it check at the virtual root. A session whose project differs checks
/// it at its own real root instead.
///
/// A host session claims through a `Lease`, one per incarnation (ADR-046). A recreated
/// session's predecessor can still be finishing a check when it is released; the claim
/// that check makes arrives late, through its lease, and is refused, so an ended
/// incarnation never holds a project and never ends its successor's hold.
module FsHotWatch.CanonicalProjects

open System.Threading

/// One incarnation of a session's right to claim. A recreated session opens a new one;
/// the old one, once released, can claim nothing.
type Lease = { Session: string; Id: int64 }

/// The lease a bare session name claims through: never released, so `Release(session)`
/// ends its holds and its next claim is admitted again.
[<Literal>]
let private UnleasedId = 0L

/// The canonical content of one project, and the sessions checking it, each with the
/// lease it claimed through.
type private Entry =
    { Hash: string
      Holders: Map<string, int64> }

/// The canonical content of every project, by repository-relative project path.
type Registry() =
    let gate = Lock()
    let mutable entries: Map<string, Entry> = Map.empty
    let mutable nextLease = UnleasedId
    let mutable released: Set<int64> = Set.empty
    let mutable refusedRetired = 0L

    let locked work = Locking.locked gate work

    // Must be called under `gate`.
    let claim (project: string) (hash: string) (session: string) (lease: int64) =
        let others entry = Map.remove session entry.Holders

        match Map.tryFind project entries with
        | Some entry when entry.Hash = hash ->
            entries <-
                Map.add
                    project
                    { entry with
                        Holders = Map.add session lease entry.Holders }
                    entries

            true
        | Some entry when not (Map.isEmpty (others entry)) ->
            entries <- Map.add project { entry with Holders = others entry } entries
            false
        | _ ->
            entries <-
                Map.add
                    project
                    { Hash = hash
                      Holders = Map.ofList [ session, lease ] }
                    entries

            true

    // Must be called under `gate`. Ends the holds `holds` selects.
    let releaseWhere (holds: string -> int64 -> bool) =
        entries <-
            entries
            |> Map.map (fun _ entry ->
                { entry with
                    Holders = entry.Holders |> Map.filter (fun session lease -> not (holds session lease)) })

    /// Whether `session` checks `project`, whose content hashes to `hash`, at the
    /// virtual root.
    ///
    /// - The first claim sets the canonical content.
    /// - A claim with the canonical content joins it.
    /// - A claim with other content takes the entry over when no other session holds
    ///   it: a session editing a project nobody else checks keeps its identity, and
    ///   with it every result FCS has for the files before the edit.
    /// - Otherwise it is refused, and `session` stops holding the entry.
    member _.Claim(project: string, hash: string, session: string) : bool =
        locked (fun () -> claim project hash session UnleasedId)

    /// `session` has ended: it holds no project.
    member _.Release(session: string) : unit =
        locked (fun () -> releaseWhere (fun holder _ -> holder = session))

    /// Open a lease for one incarnation of `session`.
    member _.Open(session: string) : Lease =
        locked (fun () ->
            nextLease <- nextLease + 1L
            { Session = session; Id = nextLease })

    /// `Claim`, through `lease`: refused, counted, and nothing held, once it was released.
    member _.Claim(project: string, hash: string, lease: Lease) : bool =
        locked (fun () ->
            if released.Contains lease.Id then
                refusedRetired <- refusedRetired + 1L
                false
            else
                claim project hash lease.Session lease.Id)

    /// The incarnation `lease` has ended: it holds nothing, and claims nothing again. A
    /// hold its session has since taken through a newer lease is not its to end.
    member _.Release(lease: Lease) : unit =
        locked (fun () ->
            released <- released.Add lease.Id
            releaseWhere (fun holder id -> holder = lease.Session && id = lease.Id))

    /// Claims refused because their lease had been released.
    member _.RefusedRetired: int64 = locked (fun () -> refusedRetired)

    /// The canonical content of `project`, if any session has claimed it.
    member _.CanonicalHash(project: string) : string option =
        locked (fun () -> Map.tryFind project entries |> Option.map _.Hash)

    /// The sessions checking `project`'s canonical content under the virtual root, sorted.
    member _.HoldersOf(project: string) : string list =
        locked (fun () ->
            Map.tryFind project entries
            |> Option.map (fun entry -> entry.Holders |> Map.keys |> List.ofSeq)
            |> Option.defaultValue [])
