module SampleLib.Math

let add x y = x + y

let multiply x y = x * y

let factorial n =
    let rec loop acc =
        function
        | 0 -> acc
        | n -> loop (acc * n) (n - 1)

    loop 1 n

/// This function is not used by any consumer — dead code.
let subtract x y = x - y
