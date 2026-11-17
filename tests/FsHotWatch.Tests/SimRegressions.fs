/// The checked-in regression list of the async simulations (AUTOMATION-1695).
///
/// CI runs a FIXED band of seeds, 1..N, so the interleavings it reaches (and the coverage they
/// contribute) are the same on every run. The nightly explores 100,000 fresh seeds. Every seed
/// the nightly finds failing is appended to `SimRegressions.txt` with the theorem it violated
/// and the ticket filed for it, and CI runs every listed seed on every run, in addition to the
/// band: a nightly failure becomes a ticket and a permanent CI case.
///
/// One entry per line: `<component> <seed> <theorem> <ticket>`. `#` starts a comment.
module FsHotWatch.Tests.SimRegressions

open System
open System.IO

/// One listed seed, of the component whose simulation found it.
[<RequireQualifiedAccess>]
type Entry =
    | Store of seed: int * theorem: StoreSimulation.Theorem * ticket: string
    | SupervisedWork of seed: int * theorem: SupervisedWorkSimulation.Theorem * ticket: string

let private storeTheorems =
    [ StoreSimulation.Theorem.PublicationBeforeReceipt
      StoreSimulation.Theorem.Order
      StoreSimulation.Theorem.Conservation
      StoreSimulation.Theorem.Settlement
      StoreSimulation.Theorem.FailureIsolation
      StoreSimulation.Theorem.Version
      StoreSimulation.Theorem.Runtime ]

let private supervisedTheorems =
    [ SupervisedWorkSimulation.Theorem.Serial
      SupervisedWorkSimulation.Theorem.HonestRest
      SupervisedWorkSimulation.Theorem.Outcome
      SupervisedWorkSimulation.Theorem.Settlement
      SupervisedWorkSimulation.Theorem.State
      SupervisedWorkSimulation.Theorem.Runtime ]

/// The theorem named `name` among `theorems`, by its case name.
let private named (theorems: 'T list) (name: string) =
    theorems |> List.tryFind (fun t -> $"%A{t}" = name)

let private parseLine (number: int) (line: string) : Result<Entry option, string> =
    let text =
        match line.IndexOf '#' with
        | -1 -> line
        | at -> line.Substring(0, at)

    match text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
    | [||] -> Ok None
    | [| "store"; seed; theorem; ticket |] ->
        match Int32.TryParse seed, named storeTheorems theorem with
        | (true, s), Some t -> Ok(Some(Entry.Store(s, t, ticket)))
        | (false, _), _ -> Error $"line %d{number}: '%s{seed}' is not a seed"
        | _, None -> Error $"line %d{number}: the store simulation has no theorem '%s{theorem}'"
    | [| "supervised-work"; seed; theorem; ticket |] ->
        match Int32.TryParse seed, named supervisedTheorems theorem with
        | (true, s), Some t -> Ok(Some(Entry.SupervisedWork(s, t, ticket)))
        | (false, _), _ -> Error $"line %d{number}: '%s{seed}' is not a seed"
        | _, None -> Error $"line %d{number}: the supervised-work simulation has no theorem '%s{theorem}'"
    | [| name; _; _; _ |] -> Error $"line %d{number}: no simulated component '%s{name}'"
    | _ -> Error $"line %d{number}: expected '<component> <seed> <theorem> <ticket>', got '%s{line.Trim()}'"

/// The entries of a regression list's text, or the first line that is not one.
let parse (text: string) : Result<Entry list, string> =
    text.Split '\n'
    |> Array.toList
    |> List.mapi (fun i line -> parseLine (i + 1) (line.TrimEnd '\r'))
    |> List.fold
        (fun acc parsed ->
            match acc, parsed with
            | Error e, _ -> Error e
            | Ok entries, Ok None -> Ok entries
            | Ok entries, Ok(Some entry) -> Ok(entry :: entries)
            | Ok _, Error e -> Error e)
        (Ok [])
    |> Result.map List.rev

/// The checked-in list, copied beside the test assembly.
let load () : Entry list =
    let path = Path.Combine(AppContext.BaseDirectory, "SimRegressions.txt")

    match parse (File.ReadAllText path) with
    | Ok entries -> entries
    | Error e -> failwith $"SimRegressions.txt: %s{e}"

let private band (first: int) (count: int) (listed: int list) =
    [ first .. first + count - 1 ] @ listed |> List.distinct

/// The seeds CI runs for the store: the band `first .. first + count - 1`, then every listed
/// store seed outside it.
let storeSeeds (first: int) (count: int) (entries: Entry list) : int list =
    entries
    |> List.choose (fun entry ->
        match entry with
        | Entry.Store(seed, _, _) -> Some seed
        | Entry.SupervisedWork _ -> None)
    |> band first count

/// The seeds CI runs for supervised work: the band, then every listed supervised-work seed.
let supervisedWorkSeeds (first: int) (count: int) (entries: Entry list) : int list =
    entries
    |> List.choose (fun entry ->
        match entry with
        | Entry.SupervisedWork(seed, _, _) -> Some seed
        | Entry.Store _ -> None)
    |> band first count

/// N histories per component per run, a FIXED band from the first seed, so the interleavings
/// these histories reach, and the coverage they contribute, are the same on every run.
/// `FSHW_SIM_HISTORIES` raises N (the nightly's 100,000); `FSHW_SIM_SEED` moves the band (the
/// nightly's fresh seeds), and with N = 1 replays one reported seed.
let histories =
    match Int32.TryParse(Environment.GetEnvironmentVariable "FSHW_SIM_HISTORIES") with
    | true, n when n > 0 -> n
    | _ -> 1000

let firstSeed =
    match Int32.TryParse(Environment.GetEnvironmentVariable "FSHW_SIM_SEED") with
    | true, n -> n
    | _ -> 1
