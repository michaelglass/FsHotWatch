/// Seeded histories, their execution, and the properties checked after every step.
///
/// THE THEOREM (plan §2): for every tree T and configuration c, the lookup verdict equals the
/// verdict of a clean from-scratch run of T under c, under any interleaving of edits, kills,
/// restarts and concurrent engines. It is checked in three halves so a violation names its half:
///
///   * SOUNDNESS: a lookup is the clean verdict of the tree it answers for, or Missing. Green
///     needs a clean green; every requirement it names red fails in the clean run.
///   * COMPLETENESS: an unfaulted `confirm` that concluded answers green or red, never Missing.
///   * RESTART INVARIANCE: a fresh store over the same directory answers a read exactly as the
///     one that has been open all along.
///
/// A history is generated from one seed with `System.Random`, whose seeded sequence is fixed,
/// so a violation reproduces from (seed, subject) alone.
module FsHotWatch.Simulation.Simulation

open System
open System.IO
open FsHotWatch
open FsHotWatch.Cli
open FsHotWatch.Simulation.Model
open FsHotWatch.Simulation.System

/// What can go wrong while a run is in flight.
[<RequireQualifiedAccess>]
type RunFault =
    | Unfaulted
    /// The process dies after filing this many of its records (taken modulo how many it had).
    | KilledAfter of records: int
    /// A file is edited while the run reads the tree.
    | EditedMidRun of path: string * content: Content
    /// A file is edited while the run reads the tree, and a second, unfaulted run of the same
    /// command over the edited tree publishes before the first one does.
    | Raced of path: string * content: Content

/// One step of a history.
[<RequireQualifiedAccess>]
type Event =
    | Edit of path: string * content: Content
    /// Put the whole tree back as it was after an earlier step (taken modulo how many there were).
    | Revert of snapshot: int
    | Run of command: Verdict.Command * fault: RunFault
    | Read
    /// A new process: a new store instance over the same directory. The selector's files survive.
    | Restart
    /// The selector's files are lost: the next check selects as a cold one does.
    | LedgerLost
    /// A record file is truncated (taken modulo how many there are).
    | TornRecord of pick: int
    /// A record file is copied under another key's directory.
    | ForeignRecord of pick: int * into: int
    | ClockJump of minutes: int
    /// GC under a budget too small for the store.
    | Gc
    /// A new fshw binary: a new producer identity.
    | ProducerUpgrade

/// Which half of the theorem a step broke.
[<RequireQualifiedAccess>]
type Property =
    | Soundness
    | Completeness
    | RestartInvariance

type Violation =
    { Seed: int
      Step: int
      Property: Property
      Detail: string
      History: Event list }

let private answerText (answer: LookupVerdict.Answer) =
    match answer with
    | LookupVerdict.Answer.Green -> "green"
    | LookupVerdict.Answer.Red failing -> "red [" + String.concat ", " failing + "]"
    | LookupVerdict.Answer.Missing missing -> "missing [" + String.concat ", " missing + "]"

let private commandText (c: Verdict.Command) = Verdict.Command.token c

let private contentText (c: Content) = Content.render c

/// An event as one line, for a violation's history.
let eventText (e: Event) : string =
    let faultText (f: RunFault) =
        match f with
        | RunFault.Unfaulted -> ""
        | RunFault.KilledAfter n -> $" killed after %d{n}"
        | RunFault.EditedMidRun(p, c) -> $" edited mid-run %s{p}=%s{contentText c}"
        | RunFault.Raced(p, c) -> $" raced by an edit %s{p}=%s{contentText c}"

    match e with
    | Event.Edit(p, c) -> $"edit %s{p}=%s{contentText c}"
    | Event.Revert n -> $"revert to snapshot %d{n}"
    | Event.Run(c, f) -> $"%s{commandText c}%s{faultText f}"
    | Event.Read -> "verdict read"
    | Event.Restart -> "restart"
    | Event.LedgerLost -> "selector ledger lost"
    | Event.TornRecord n -> $"torn record %d{n}"
    | Event.ForeignRecord(n, into) -> $"record %d{n} copied under key %d{into}"
    | Event.ClockJump m -> $"clock jump %+d{m} min"
    | Event.Gc -> "gc under a tiny budget"
    | Event.ProducerUpgrade -> "fshw upgrade"

/// The events of a history of `length` steps from `seed`.
let generate (seed: int) (length: int) : Event list =
    let rng = Random seed
    let mutable version = 0

    let content () =
        version <- version + 1

        { Version = version
          Fault =
            if rng.NextDouble() < 0.3 then
                Some(Fault.all[rng.Next Fault.all.Length])
            else
                None }

    let path () = paths[rng.Next paths.Length]

    let command () =
        if rng.NextDouble() < 0.65 then
            Verdict.Command.Check
        else
            Verdict.Command.Confirm

    let fault () =
        let roll = rng.NextDouble()

        if roll < 0.65 then
            RunFault.Unfaulted
        elif roll < 0.82 then
            RunFault.KilledAfter(rng.Next 1000)
        elif roll < 0.94 then
            RunFault.EditedMidRun(path (), content ())
        else
            RunFault.Raced(path (), content ())

    [ for _ in 1..length do
          let roll = rng.NextDouble()

          if roll < 0.28 then
              Event.Edit(path (), content ())
          elif roll < 0.36 then
              Event.Revert(rng.Next 1000)
          elif roll < 0.66 then
              Event.Run(command (), fault ())
          elif roll < 0.80 then
              Event.Read
          elif roll < 0.84 then
              Event.Restart
          elif roll < 0.86 then
              Event.LedgerLost
          elif roll < 0.89 then
              Event.TornRecord(rng.Next 1000)
          elif roll < 0.91 then
              Event.ForeignRecord(rng.Next 1000, rng.Next 1000)
          elif roll < 0.95 then
              Event.ClockJump(rng.Next(-48 * 60, 48 * 60))
          elif roll < 0.98 then
              Event.Gc
          else
              Event.ProducerUpgrade ]

/// Everything a history carries from one step to the next.
type private State =
    { Tree: Tree
      Snapshots: Tree list
      Store: OutcomeStore.Store
      Selector: SelectorState
      Producer: int
      Now: DateTimeOffset
      LastCommand: Verdict.Command
      Runs: int }

/// Run `events` against `subject` in a fresh temporary repository; every violation, in step order.
let execute (subject: Subject) (seed: int) (events: Event list) : Violation list =
    let repoRoot =
        Path.Combine(Path.GetTempPath(), "fshw-sim", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory repoRoot |> ignore
    let storeRoot = ShadowVerdict.storeRoot repoRoot
    let violations = ResizeArray<Violation>()

    let violation (step: int) (property: Property) (detail: string) =
        violations.Add
            { Seed = seed
              Step = step
              Property = property
              Detail = detail
              History = List.truncate (step + 1) events }

    let producerText (s: State) = $"sim-fshw-%d{s.Producer}"

    /// SOUNDNESS of `answer`, which claims to describe `tree`.
    let checkSound (step: int) (tree: Tree) (answer: LookupVerdict.Answer) =
        let clean = cleanFailing tree

        match answer with
        | LookupVerdict.Answer.Green when not clean.IsEmpty ->
            let failing = String.concat ", " clean
            violation step Property.Soundness $"lookup green over a clean red [%s{failing}]"
        | LookupVerdict.Answer.Red failing ->
            match failing |> List.filter (clean.Contains >> not) with
            | [] -> ()
            | wrong ->
                let names = String.concat ", " wrong
                violation step Property.Soundness $"lookup red on [%s{names}], which pass in a clean run"
        | LookupVerdict.Answer.Green
        | LookupVerdict.Answer.Missing _ -> ()

    let runOnce (s: State) (command: Verdict.Command) (started: Tree) (read: Tree) : Run =
        let selected, scope = select command s.Selector started

        { Command = command
          Key = treeKey subject started
          Started = started
          Read = read
          Selected = selected
          Scope = scope
          RunId = $"run%d{s.Runs}" }

    let publishRun (step: int) (s: State) (run: Run) (endedAt: Tree) (unfaulted: bool) =
        let o = observation subject run endedAt (producerText s) repoRoot s.Now
        let answer = publish subject repoRoot s.Store o
        checkSound step run.Started answer

        match run.Command, o.Today, unfaulted, answer with
        | Verdict.Command.Confirm, today, true, LookupVerdict.Answer.Missing _ when today <> ShadowVerdict.Class.Missing ->
            violation step Property.Completeness $"a concluded unfaulted confirm answered %s{answerText answer}"
        | _ -> ()

        { s with
            Selector = afterRun subject s.Selector run.Selected run.Read
            Runs = s.Runs + 1
            LastCommand = run.Command }

    let step (s: State) (index: int, event: Event) : State =
        let s =
            { s with
                Now = s.Now.AddMinutes 1.0
                Snapshots = s.Tree :: s.Snapshots }

        match event with
        | Event.Edit(path, content) ->
            { s with
                Tree = Map.add path content s.Tree }
        | Event.Revert n ->
            { s with
                Tree = s.Snapshots[n % s.Snapshots.Length] }
        | Event.Run(command, RunFault.Unfaulted) -> publishRun index s (runOnce s command s.Tree s.Tree) s.Tree true
        | Event.Run(command, RunFault.KilledAfter count) ->
            let run = runOnce s command s.Tree s.Tree
            let o = observation subject run s.Tree (producerText s) repoRoot s.Now
            let total = (ShadowVerdict.recordsOf o configured.TestProjects).Length
            fileKilled s.Store o (count % (total + 1))
            { s with Runs = s.Runs + 1 }
        | Event.Run(command, RunFault.EditedMidRun(path, content)) ->
            let edited = Map.add path content s.Tree
            let s' = publishRun index s (runOnce s command s.Tree edited) edited false
            { s' with Tree = edited }
        | Event.Run(command, RunFault.Raced(path, content)) ->
            let edited = Map.add path content s.Tree
            let first = runOnce s command s.Tree edited
            let s = { s with Runs = s.Runs + 1 }
            let s = publishRun index s (runOnce s command edited edited) edited true
            let s = publishRun index s first edited false
            { s with Tree = edited }
        | Event.Read ->
            let o =
                readObservation s.LastCommand (treeKey subject s.Tree) (producerText s) repoRoot s.Now

            let answer = read subject repoRoot s.Store o
            checkSound index s.Tree answer
            let fresh = decide subject (openStore storeRoot OutcomeStore.defaultMaxBytes) o

            if fresh <> answer then
                violation
                    index
                    Property.RestartInvariance
                    $"open store answered %s{answerText answer}, a fresh one %s{answerText fresh}"

            s
        | Event.Restart ->
            { s with
                Store = openStore storeRoot OutcomeStore.defaultMaxBytes }
        | Event.LedgerLost -> { s with Selector = SelectorState.cold }
        | Event.TornRecord n ->
            match recordFiles storeRoot with
            | [] -> s
            | files ->
                let file = files[n % files.Length]
                let text = File.ReadAllText file
                File.WriteAllText(file, text.Substring(0, text.Length / 2))
                s
        | Event.ForeignRecord(n, into) ->
            match recordFiles storeRoot with
            | [] -> s
            | files ->
                let file = files[n % files.Length]
                let target = Path.GetDirectoryName files[into % files.Length]

                if target <> Path.GetDirectoryName file then
                    File.Copy(file, Path.Combine(target, "foreign-" + Path.GetFileName file), true)

                s
        | Event.ClockJump minutes ->
            { s with
                Now = s.Now.AddMinutes(float minutes) }
        | Event.Gc ->
            (openStore storeRoot 2048L).Gc(s.Now, Set.empty) |> ignore
            s
        | Event.ProducerUpgrade -> { s with Producer = s.Producer + 1 }

    try
        let initial =
            { Tree = initialTree
              Snapshots = []
              Store = openStore storeRoot OutcomeStore.defaultMaxBytes
              Selector = SelectorState.cold
              Producer = 1
              Now = DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)
              LastCommand = Verdict.Command.Check
              Runs = 0 }

        events |> List.indexed |> List.fold step initial |> ignore
        List.ofSeq violations
    finally
        try
            Directory.Delete(repoRoot, true)
        with :? IOException ->
            ()

/// The steps in a history.
[<Literal>]
let HistoryLength = 40

/// Run `count` histories from seeds `firstSeed ..`; every violation, stopping at the first
/// history that has any.
let explore (subject: Subject) (firstSeed: int) (count: int) : Violation list =
    seq { firstSeed .. firstSeed + count - 1 }
    |> Seq.map (fun seed -> execute subject seed (generate seed HistoryLength))
    |> Seq.tryFind (List.isEmpty >> not)
    |> Option.defaultValue []

/// A violation as text: what broke, then the history that broke it.
let describe (v: Violation) : string =
    let property =
        match v.Property with
        | Property.Soundness -> "soundness"
        | Property.Completeness -> "completeness"
        | Property.RestartInvariance -> "restart invariance"

    let steps =
        v.History
        |> List.mapi (fun i e -> $"  %d{i}: %s{eventText e}")
        |> String.concat "\n"

    $"seed %d{v.Seed}, step %d{v.Step}: %s{property}: %s{v.Detail}\n%s{steps}"
