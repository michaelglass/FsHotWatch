namespace TestPrune.Trace.Recorder

/// The consumer-facing scope contract. Inert when the recorder is inactive.
[<AbstractClass; Sealed>]
type Scopes =
    /// Overrides the scope of the current async flow with `key`.
    static member Enter(key: string) : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.EnterScope key

    /// Ends the override set by `Enter`.
    static member Exit() : unit =
        let s = Runtime.state

        if not (isNull s) then
            s.ExitScope()

    /// The current scope key, or null when the recorder is inactive.
    static member CurrentKey() : string =
        let s = Runtime.state

        if isNull s then
            null
        else
            match s.CurrentScope() with
            | null -> null
            | scope -> scope.Key

    /// Makes the current scope inherit everything recorded under `scopeKey`.
    static member LinkCurrentTo(scopeKey: string) : unit =
        let s = Runtime.state

        if not (isNull s) then
            match s.CurrentScope() with
            | null -> ()
            | scope -> scope.Links.TryAdd(scopeKey, 0uy) |> ignore

    /// Notes `path` as a file input of the code running now, on the scope a woven read
    /// would use: static init, then the current scope, then ambient. The escape hatch for
    /// a read the weaver cannot see because it happens inside an assembly that is not
    /// woven: wrap that boundary and note what it reads. `kind` is `read` (the file's
    /// content), `exists`, `list` (the directory's own entries) or `list-deep` (every entry
    /// of its tree); any other kind, a null path, or a path outside the repository root
    /// notes nothing. Never throws.
    static member NoteInput(kind: string, path: string) : unit =
        match kind with
        | "read"
        | "exists"
        | "list"
        | "list-deep" -> Io.NoteWith(Runtime.state, kind, path)
        | _ -> ()
