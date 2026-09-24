/// Which content each project of a repository is checked under at the virtual root.
///
/// FCS keeps one strong version per cache key, and a project's key is its identity. Two
/// sessions checking different content under one identity would evict each other's work
/// on every check. So one content per project is canonical, and only sessions whose
/// project matches it check at the virtual root. A session whose project differs checks
/// it at its own real root instead.
module FsHotWatch.CanonicalProjects

open System.Threading

/// The canonical content of one project, and the sessions checking it.
type private Entry = { Hash: string; Holders: Set<string> }

/// The canonical content of every project, by repository-relative project path.
type Registry() =
    let gate = Lock()
    let mutable entries: Map<string, Entry> = Map.empty

    let locked (work: unit -> 'T) : 'T =
        gate.Enter()

        try
            work ()
        finally
            gate.Exit()

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
        locked (fun () ->
            let others entry = Set.remove session entry.Holders

            match Map.tryFind project entries with
            | Some entry when entry.Hash = hash ->
                entries <-
                    Map.add
                        project
                        { entry with
                            Holders = Set.add session entry.Holders }
                        entries

                true
            | Some entry when not (Set.isEmpty (others entry)) ->
                entries <- Map.add project { entry with Holders = others entry } entries
                false
            | _ ->
                entries <-
                    Map.add
                        project
                        { Hash = hash
                          Holders = Set.singleton session }
                        entries

                true)

    /// `session` has ended: it holds no project.
    member _.Release(session: string) : unit =
        locked (fun () ->
            entries <-
                entries
                |> Map.map (fun _ entry ->
                    { entry with
                        Holders = Set.remove session entry.Holders }))

    /// The canonical content of `project`, if any session has claimed it.
    member _.CanonicalHash(project: string) : string option =
        locked (fun () -> Map.tryFind project entries |> Option.map _.Hash)
