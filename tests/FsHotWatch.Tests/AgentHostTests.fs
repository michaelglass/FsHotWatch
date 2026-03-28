module FsHotWatch.Tests.AgentHostTests

open Xunit
open Swensen.Unquote
open FsHotWatch.AgentHost

[<Fact>]
let ``agent processes messages sequentially`` () =
    async {
        let agent = createAgent "test" 0 (fun state msg -> async { return state + msg })

        agent.Post(1)
        agent.Post(2)
        agent.Post(3)

        // GetState queues behind posted messages — no sleep needed
        let! state = agent.GetState()
        test <@ state = 6 @>
    }
    |> Async.RunSynchronously

[<Fact>]
let ``agent recovers from handler exceptions`` () =
    async {
        let agent =
            createAgent "test" 0 (fun state msg ->
                async {
                    if msg < 0 then
                        failwith "negative not allowed"

                    return state + msg
                })

        agent.Post(5)
        // This should crash but agent continues with previous state
        agent.Post(-1)
        // This should still process
        agent.Post(3)

        let! state = agent.GetState()
        test <@ state = 8 @>
    }
    |> Async.RunSynchronously

[<Fact>]
let ``agent supports request-reply via GetState`` () =
    async {
        let agent = createAgent "test" "initial" (fun _state msg -> async { return msg })

        agent.Post("updated")

        let! state = agent.GetState()
        test <@ state = "updated" @>
    }
    |> Async.RunSynchronously
