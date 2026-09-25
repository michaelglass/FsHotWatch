module FxLib.Logic

open FxLib

let area shape =
    match shape with
    | Circle r -> 3.0 * r * r
    | Square s -> s * s

let isCircleOnly shape =
    match shape with
    | Circle _ -> true
    | _ -> false

let colorCode c =
    match c with
    | Red -> 1
    | Green n -> 10 + n
    | Blue n -> 20 + n
    | Cyan s -> s.Length
    | Magenta -> 5

let turn d =
    match d with
    | North -> East
    | East -> South
    | South -> West
    | West -> North

let sval r =
    match r with
    | SOk v -> v
    | SErr e -> e.Length

let tricky t =
    match t with
    | Tag -> 0
    | Other s -> s.Length

let (|Big|Small|) (p: Point) = if p.X + p.Y > 10 then Big else Small

let classify p =
    match p with
    | Big -> "big"
    | Small -> "small"

let sumPoint (p: Point) = p.X + p.Y

let describe (o: obj) =
    match o with
    | :? Dog as d -> "dog " + d.Speak()
    | :? Cat -> "cat"
    | _ -> "other"

let castDog (o: obj) = (o :?> Dog).Speak()
let aboveThreshold x = x > Values.threshold
let addConst x = x + Values.constValue
let addLiteral x = x + Values.LiteralValue
let defaultArea () = area Values.defaultShape
let isCircleProp (s: Shape) = s.IsCircle
let samePoint (a: Point) (b: Point) = a = b
let greet (g: IGreeter) = g.Greet "x"
let readRepoFile (path: string) = System.IO.File.ReadAllText path
