module TestPrune.Tests.ProgramTests

open Xunit
open Swensen.Unquote
open TestPrune.Program

module ``parseArgs`` =

    [<Fact>]
    let ``empty args returns Help`` () = test <@ parseArgs [||] = Ok Help @>

    [<Fact>]
    let ``index command`` () =
        test <@ parseArgs [| "index" |] = Ok Index @>

    [<Fact>]
    let ``run command`` () =
        test <@ parseArgs [| "run" |] = Ok Run @>

    [<Fact>]
    let ``status command`` () =
        test <@ parseArgs [| "status" |] = Ok Status @>

    [<Fact>]
    let ``help command`` () =
        test <@ parseArgs [| "help" |] = Ok Help @>

    [<Fact>]
    let ``--help flag`` () =
        test <@ parseArgs [| "--help" |] = Ok Help @>

    [<Fact>]
    let ``-h flag`` () =
        test <@ parseArgs [| "-h" |] = Ok Help @>

    [<Fact>]
    let ``dead-code command with defaults`` () =
        test <@ parseArgs [| "dead-code" |] = Ok(DeadCodeCmd defaultEntryPatterns) @>

    [<Fact>]
    let ``dead-code command with custom entry patterns`` () =
        let result =
            parseArgs [| "dead-code"; "--entry"; "*.main"; "--entry"; "*.Routes.*" |]

        test <@ result = Ok(DeadCodeCmd [ "*.main"; "*.Routes.*" ]) @>

    [<Fact>]
    let ``dead-code command with unknown flag returns Error`` () =
        let result = parseArgs [| "dead-code"; "--bogus" |]
        test <@ Result.isError result @>

    [<Fact>]
    let ``unknown command returns Error`` () =
        let result = parseArgs [| "bogus" |]
        test <@ Result.isError result @>
