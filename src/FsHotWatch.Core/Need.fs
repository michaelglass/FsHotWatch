/// The key is the trace (AUTOMATION-1636, S1 and S2 of ADR-059 §35).
///
/// S1: an action is an `action { … }` computation that reads its inputs ONLY through
/// `Need.*`. Each `Need` call returns the value AND records the digest of what it read; the
/// action's key is what it needed (`NeedTrace.key`). The world an action reads from is
/// handed to `Action.run` and never to the action: `Action`'s constructor is private, so the
/// only actions there are `Need.*`, `return` and the builder's binds of them. An undeclared
/// input stops being a bug someone must notice, because there is no API to read one.
///
/// S2: an oracle is a typed question about the environment (`Oracle<'Answer>`): the SDK, an
/// environment variable, the tool manifest, the platform. It is asked again on every run,
/// and its answer's digest, never the answer, enters the key.
namespace FsHotWatch.Core

open System.Text

/// Who answers oracle questions: one function per question, each returning that question's
/// own type.
[<NoEquality; NoComparison>]
type Oracles =
    {
        /// `OracleQuestion.DotnetSdk`: the SDK a `dotnet` started here resolves.
        DotnetSdk: unit -> string
        /// `OracleQuestion.Variable`: the variable's value, `None` when it is unset.
        Variable: EnvVarName -> string option
        /// `OracleQuestion.ToolManifest`: the manifest's bytes, `None` when there is none.
        ToolManifest: unit -> byte array option
        /// `OracleQuestion.Platform`: the .NET runtime identifier, e.g. `osx-arm64`.
        Platform: unit -> string
    }

/// Everything an action can read, handed to `Action.run`, never to the action.
[<NoEquality; NoComparison>]
type World =
    {
        /// A file's bytes, `None` when it is not there.
        ReadFile: RepoRelativePath -> byte array option
        /// The digest of an output another action produced, `None` when none was.
        Upstream: OutputLabel -> OutputDigest option
        /// The environment's answers.
        Oracles: Oracles
    }

/// A question about the environment whose answer is a `'Answer`. Made only by `Oracle`.
[<NoEquality; NoComparison>]
type Oracle<'Answer> =
    private
        {
            Question: OracleQuestion
            Ask: Oracles -> 'Answer
            /// The answer as the bytes its digest is taken over: two answers have equal
            /// bytes only if they are equal.
            Canonical: 'Answer -> byte array
        }

/// The oracles: one per `OracleQuestion`.
[<RequireQualifiedAccess>]
module Oracle =
    let private utf8 (text: string) = Encoding.UTF8.GetBytes text

    /// Unset and set-to-empty are different answers, so presence is a leading byte.
    let private optional (bytes: byte array option) =
        match bytes with
        | None -> [| 0uy |]
        | Some bytes -> Array.append [| 1uy |] bytes

    /// What the oracle asks.
    let question (oracle: Oracle<'Answer>) : OracleQuestion = oracle.Question

    /// The SDK a `dotnet` started here resolves.
    let dotnetSdk: Oracle<string> =
        { Question = OracleQuestion.DotnetSdk
          Ask = fun oracles -> oracles.DotnetSdk()
          Canonical = utf8 }

    /// One environment variable's value, `None` when it is unset.
    let variable (name: EnvVarName) : Oracle<string option> =
        { Question = OracleQuestion.Variable name
          Ask = fun oracles -> oracles.Variable name
          Canonical = Option.map utf8 >> optional }

    /// The repository's tool manifest, `None` when it has none.
    let toolManifest: Oracle<byte array option> =
        { Question = OracleQuestion.ToolManifest
          Ask = fun oracles -> oracles.ToolManifest()
          Canonical = optional }

    /// The platform, as a .NET runtime identifier.
    let platform: Oracle<string> =
        { Question = OracleQuestion.Platform
          Ask = fun oracles -> oracles.Platform()
          Canonical = utf8 }

/// One input an action needed, with the digest of what it read.
[<RequireQualifiedAccess>]
type Needed =
    /// A file, with its content's digest, or `None`: it was not there.
    | File of path: RepoRelativePath * content: ContentDigest option
    /// An output another action produced.
    | Upstream of label: OutputLabel * digest: OutputDigest
    /// An oracle's answer.
    | Oracle of question: OracleQuestion * answer: AnswerDigest

/// Reads needs.
[<RequireQualifiedAccess>]
module Needed =
    /// What the need is of, without its answer: a trace holds one answer per subject.
    let subject (needed: Needed) : string =
        match needed with
        | Needed.File(path, _) -> "file:" + path.Text
        | Needed.Upstream(label, _) -> "upstream:" + label.Text
        | Needed.Oracle(question, _) -> "oracle:" + question.Subject

    /// The need as a key input.
    let internal toKeyInput (needed: Needed) : KeyInput =
        match needed with
        | Needed.File(path, Some content) -> KeyInput.Content(path, content)
        | Needed.File(path, None) -> KeyInput.Absent path
        | Needed.Upstream(label, digest) -> KeyInput.Upstream(label, digest)
        | Needed.Oracle(question, answer) -> KeyInput.Oracle(question, answer)

/// How one need differs between two traces.
[<RequireQualifiedAccess>]
type NeedChange =
    /// Needed only by the later trace.
    | Added of Needed
    /// Needed only by the earlier trace.
    | Removed of Needed
    /// Needed by both, answered differently.
    | Changed of before: Needed * after: Needed

    /// The subject that differs.
    member this.Subject =
        match this with
        | Added needed
        | Removed needed
        | Changed(needed, _) -> Needed.subject needed

/// Why an action produced no trace.
[<RequireQualifiedAccess>]
type NeedError =
    /// It needed an output no action produced.
    | NotProduced of label: OutputLabel
    /// It needed this subject twice and the world answered differently: no one key
    /// describes what it read.
    | Inconsistent of subject: string

    override this.ToString() =
        match this with
        | NotProduced label -> $"no output '%s{label.Text}' was produced"
        | Inconsistent subject -> $"'%s{subject}' answered differently within one run"

/// What an action needed: one answer per subject.
type NeedTrace = private { Needs: Map<string, Needed> }

/// Reads traces.
[<RequireQualifiedAccess>]
module NeedTrace =
    let internal empty: NeedTrace = { Needs = Map.empty }

    /// `trace` with `needed` recorded; a second, different answer for its subject is refused.
    let internal add (needed: Needed) (trace: NeedTrace) : Result<NeedTrace, NeedError> =
        let subject = Needed.subject needed

        match Map.tryFind subject trace.Needs with
        | Some earlier when earlier <> needed -> Error(NeedError.Inconsistent subject)
        | Some _ -> Ok trace
        | None -> Ok { Needs = Map.add subject needed trace.Needs }

    /// Every need, ordered by subject.
    let needs (trace: NeedTrace) : Needed list =
        trace.Needs |> Map.toList |> List.map snd

    /// The action's key: what it needed, as a key of kind `'Kind`.
    let key<'Kind when 'Kind :> IRecordKind> (trace: NeedTrace) : Result<ActionKey<'Kind>, KeyError> =
        trace |> needs |> List.map Needed.toKeyInput |> ActionKey.create<'Kind>

    /// What differs from `before` to `after`, ordered by subject; empty when they are equal.
    let diff (before: NeedTrace) (after: NeedTrace) : NeedChange list =
        let changedOrRemoved =
            before.Needs
            |> Map.toList
            |> List.choose (fun (subject, earlier) ->
                match Map.tryFind subject after.Needs with
                | Some later when later <> earlier -> Some(NeedChange.Changed(earlier, later))
                | Some _ -> None
                | None -> Some(NeedChange.Removed earlier))

        let added =
            after.Needs
            |> Map.toList
            |> List.filter (fun (subject, _) -> not (before.Needs.ContainsKey subject))
            |> List.map (snd >> NeedChange.Added)

        changedOrRemoved @ added |> List.sortBy _.Subject

/// An action yielding a `'T`. Its constructor is private: it is built only from `Need.*`
/// and the `action` builder, and only `Action.run` hands it a world.
[<NoEquality; NoComparison>]
type Action<'T> = private Action of (World -> NeedTrace -> Result<'T * NeedTrace, NeedError>)

/// Runs actions.
[<RequireQualifiedAccess>]
module Action =
    /// `action` run against `world`: its value and what it needed, or why it stopped.
    let run (world: World) (action: Action<'T>) : Result<'T * NeedTrace, NeedError> =
        let (Action body) = action
        body world NeedTrace.empty

    let internal ret (value: 'T) : Action<'T> = Action(fun _ trace -> Ok(value, trace))

    let internal bind (next: 'T -> Action<'U>) (action: Action<'T>) : Action<'U> =
        Action(fun world trace ->
            let (Action body) = action

            match body world trace with
            | Error error -> Error error
            | Ok(value, trace) ->
                let (Action rest) = next value
                rest world trace)

    /// One read: `read` answers from the world, and `toNeeded` names what was read.
    let internal reading (read: World -> 'T) (toNeeded: 'T -> Needed) : Action<'T> =
        Action(fun world trace ->
            let value = read world
            NeedTrace.add (toNeeded value) trace |> Result.map (fun trace -> value, trace))

/// The only reads an action has.
[<RequireQualifiedAccess>]
module Need =
    /// A file's bytes, `None` when it is not there; records the content's digest or absence.
    let file (path: RepoRelativePath) : Action<byte array option> =
        Action.reading (fun world -> world.ReadFile path) (fun bytes ->
            Needed.File(path, Option.map ContentDigest.ofBytes bytes))

    /// The digest of the output `label` names; an action that needs one never produced
    /// stops with `NeedError.NotProduced`.
    let upstream (label: OutputLabel) : Action<OutputDigest> =
        Action(fun world trace ->
            match world.Upstream label with
            | None -> Error(NeedError.NotProduced label)
            | Some digest ->
                NeedTrace.add (Needed.Upstream(label, digest)) trace
                |> Result.map (fun trace -> digest, trace))

    /// The oracle's answer, asked now; records the answer's digest.
    let oracle (oracle: Oracle<'Answer>) : Action<'Answer> =
        Action.reading (fun world -> oracle.Ask world.Oracles) (fun answer ->
            Needed.Oracle(oracle.Question, AnswerDigest.ofBytes (oracle.Canonical answer)))

    /// An environment variable's value: `Need.oracle (Oracle.variable name)`.
    let env (name: EnvVarName) : Action<string option> = oracle (Oracle.variable name)

/// The `action { … }` builder.
type ActionBuilder() =
    member _.Return(value: 'T) : Action<'T> = Action.ret value
    member _.ReturnFrom(action: Action<'T>) : Action<'T> = action
    member _.Bind(action: Action<'T>, next: 'T -> Action<'U>) : Action<'U> = Action.bind next action

/// Brings `action { … }` into scope.
[<AutoOpen>]
module ActionBuilderInstance =
    /// Builds an `Action` that reads only through `Need.*`.
    let action = ActionBuilder()
