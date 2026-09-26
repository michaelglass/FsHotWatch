namespace FxLib

/// An F# exception declaration: its CLR class is a type-use site like a record's.
exception Overflowed of limit: int

/// A class in a namespace: F# emits the closures of its `let` bindings and members into
/// this file's `<StartupCode$FxLib>` class, and a closure that only passes a function
/// along (`List.map double`) has no sequence points of its own.
type Pipeline(limit: int) =
    let double x = x * 2

    member _.DoubleAll(xs: int list) = xs |> List.map double

    member _.Capped x =
        try
            if x > limit then raise (Overflowed limit) else double x
        with Overflowed l ->
            l
