namespace FxLib

type Shape =
    | Circle of radius: float
    | Square of side: float

type Color5 =
    | Red
    | Green of int
    | Blue of int
    | Cyan of string
    | Magenta

type Dir =
    | North
    | East
    | South
    | West

[<Struct>]
type SResult =
    | SOk of okValue: int
    | SErr of errValue: string

/// A case literally named `Tag` and a case field named `tag`: the two shapes that
/// produced invalid IL in the prototype.
type Tricky =
    | Tag
    | Other of tag: string

type Point = { X: int; Y: int }

type Animal() =
    abstract Speak: unit -> string
    default _.Speak() = "..."

type Dog() =
    inherit Animal()
    override _.Speak() = "woof"

type Cat() =
    inherit Animal()
    override _.Speak() = "meow"

type IGreeter =
    abstract Greet: string -> string

type Greeter() =
    interface IGreeter with
        member _.Greet n = "hi " + n
