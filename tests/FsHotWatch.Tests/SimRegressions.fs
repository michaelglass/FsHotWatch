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
type Entry = Store of seed: int * theorem: StoreSimulation.Theorem * ticket: string

let private storeTheorems =
    [ StoreSimulation.Theorem.PublicationBeforeReceipt
      StoreSimulation.Theorem.Order
      StoreSimulation.Theorem.Conservation
      StoreSimulation.Theorem.Settlement
      StoreSimulation.Theorem.FailureIsolation
      StoreSimulation.Theorem.Version
      StoreSimulation.Theorem.Runtime ]

let private parseLine (number: int) (line: string) : Result<Entry option, string> =
    let text =
        match line.IndexOf '#' with
        | -1 -> line
        | at -> line.Substring(0, at)

    match text.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries) with
    | [||] -> Ok None
    | [| "store"; seed; theorem; ticket |] ->
        match Int32.TryParse seed, storeTheorems |> List.tryFind (fun t -> $"%A{t}" = theorem) with
        | (true, s), Some t -> Ok(Some(Entry.Store(s, t, ticket)))
        | (false, _), _ -> Error $"line %d{number}: '%s{seed}' is not a seed"
        | _, None -> Error $"line %d{number}: the store simulation has no theorem '%s{theorem}'"
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
    let path = FsHotWatch.TestRoots.TestBinary.path "SimRegressions.txt"

    match parse (File.ReadAllText path) with
    | Ok entries -> entries
    | Error e -> failwith $"SimRegressions.txt: %s{e}"

/// The seeds CI runs for the store: the band `first .. first + count - 1`, then every listed
/// store seed outside it.
let storeSeeds (first: int) (count: int) (entries: Entry list) : int list =
    let listed = entries |> List.map (fun (Entry.Store(seed, _, _)) -> seed)
    [ first .. first + count - 1 ] @ listed |> List.distinct
