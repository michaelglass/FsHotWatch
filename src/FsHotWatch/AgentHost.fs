/// Reusable MailboxProcessor wrapper with error recovery and state queries.
module FsHotWatch.AgentHost

open FsHotWatch.Logging

/// An agent that processes messages of type 'Msg and maintains state of type 'State.
[<NoComparison; NoEquality>]
type Agent<'State, 'Msg> =
    { Mailbox: MailboxProcessor<Choice<'Msg, AsyncReplyChannel<'State>>>
      Name: string }

    /// Post a message to the agent for processing.
    member this.Post(msg: 'Msg) = this.Mailbox.Post(Choice1Of2 msg)

    /// Query the agent's current state.
    member this.GetState() =
        this.Mailbox.PostAndAsyncReply(fun ch -> Choice2Of2 ch)

/// Create an agent with the given name, initial state, and message handler.
/// If the handler throws, the exception is logged and the agent continues
/// with its previous state.
let createAgent<'State, 'Msg> (name: string) (initialState: 'State) (handler: 'State -> 'Msg -> Async<'State>) =
    let mailbox =
        MailboxProcessor<Choice<'Msg, AsyncReplyChannel<'State>>>.Start(fun inbox ->
            let rec loop state =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Choice2Of2 ch ->
                        ch.Reply(state)
                        return! loop state
                    | Choice1Of2 msg ->
                        let! nextState =
                            async {
                                try
                                    return! handler state msg
                                with ex ->
                                    error name $"Agent message handler failed: %s{ex.ToString()}"
                                    return state
                            }

                        return! loop nextState
                }

            loop initialState)

    { Mailbox = mailbox; Name = name }
