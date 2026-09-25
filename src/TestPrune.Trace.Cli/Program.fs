/// Entry point of the `test-prune-traces` tool.
module TestPrune.Trace.Cli.Program

/// The usage line printed for an unknown or missing verb.
let usage = "usage: test-prune-traces census|audit|overhead|file-census [options]"

/// Dispatches a verb; prints the usage and returns 2 for an unknown one.
[<EntryPoint>]
let main (argv: string array) =
    ignore argv
    eprintfn "%s" usage
    2
