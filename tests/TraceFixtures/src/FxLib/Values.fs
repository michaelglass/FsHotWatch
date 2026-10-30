module FxLib.Values

let computeThreshold () = 40 + 2
let threshold = computeThreshold ()
let constValue = 7

[<Literal>]
let LiteralValue = 99

let defaultShape = Square 3.0
let names = [ "a"; "b" ]
let unusedValue = computeThreshold () * 2
let thresholdPlus x = threshold + x
let prebuiltBlue = Blue 3
let constPlus x = constValue + x
