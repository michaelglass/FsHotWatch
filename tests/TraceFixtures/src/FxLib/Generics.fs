namespace FxLib

/// A generic class with a generic method. The CLR names the class `Crate`1`; the method
/// keeps its plain name, `Map`, with its own type parameter.
type Crate<'item>(item: 'item) =
    member _.Item = item
    member _.Map<'other>(f: 'item -> 'other) = Crate<'other>(f item)

/// Generic unions nested in a module. The CLR nests them (`FxLib.Generics+Lookup`1`) and
/// nests each case class once more (`FxLib.Generics+Lookup`1+Found`).
module Generics =
    type Lookup<'value> =
        | Found of 'value
        | Absent
        | CouldNotRead of reason: string

    type Either<'left, 'right> =
        | Left of 'left
        | Right of 'right

    let fromOption (o: 'value option) =
        match o with
        | Some v -> Found v
        | None -> Absent

    let describe (lookup: Lookup<'value>) =
        match lookup with
        | Found _ -> "found"
        | Absent -> "absent"
        | CouldNotRead reason -> reason

    let swap (e: Either<'left, 'right>) : Either<'right, 'left> =
        match e with
        | Left l -> Right l
        | Right r -> Left r
