namespace TestPrune.Trace.Recorder

/// Static entry points the weaver calls. Every body is a no-op until the recorder core exists.
[<AbstractClass; Sealed>]
type Probes =
    /// Records probe `id` against the current scope.
    static member Hit(id: int) : unit = ignore id

    /// Records probe `id` when `value` is not null.
    static member HitIfNotNull(value: obj, id: int) : unit = ignore (value, id)

    /// Records probe `id` when `value` is true.
    static member HitIfTrue(value: bool, id: int) : unit = ignore (value, id)

    /// Records probe `baseId + tag`.
    static member HitTag(tag: int, baseId: int) : unit = ignore (tag, baseId)

    /// Marks entry into a static constructor.
    static member EnterStatic() : unit = ()

    /// Marks exit from a static constructor.
    static member ExitStatic() : unit = ()

/// The consumer-facing scope contract. Inert when the recorder is inactive.
[<AbstractClass; Sealed>]
type Scopes =
    /// Overrides the scope of the current async flow with `key`.
    static member Enter(key: string) : unit = ignore key

    /// Ends the override set by `Enter`.
    static member Exit() : unit = ()

    /// The current scope key, or null when the recorder is inactive.
    static member CurrentKey() : string = null

    /// Makes the current scope inherit everything recorded under `scopeKey`.
    static member LinkCurrentTo(scopeKey: string) : unit = ignore scopeKey
