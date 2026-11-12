/// The tree the impact index is sealed at, kept beside the index on disk (AUTOMATION-1587).
///
/// A `BatchChecked` folds a cohort's check results into the index and then reads the receipt
/// tree: from then until the next flush writes rows, the index describes exactly that tree.
/// The seal records it, so a restarted daemon can tell whether its index still holds the rows
/// of the tree its last full suite passed (and so whether the cold scan's diff against those
/// rows is the diff from that tree). Any flush that writes rows outside a seal clears it.
///
/// A seal that is absent or cannot be read is no seal: the reader takes it as a miss.
module FsHotWatch.TestPrune.IndexSeal

open System
open System.IO
open FsHotWatch

/// The seal file of the index at `dbPath`.
let pathOf (dbPath: string) = dbPath + ".sealed-tree"

/// Record that the index at `dbPath` is sealed at `tree`, or clear the seal (`None`). Never
/// throws: a seal that cannot be written is left cleared, which only costs a later miss.
let save (log: string -> unit) (dbPath: string) (tree: string option) : unit =
    let path = pathOf dbPath

    try
        match tree with
        | Some tree -> FsHwPaths.atomicWriteAllText path tree
        | None ->
            if File.Exists path then
                File.Delete path
    with ex ->
        log $"index seal %s{path} could not be written: %s{ex.GetType().Name}: %s{ex.Message}"

        try
            if File.Exists path then
                File.Delete path
        with ex ->
            log $"index seal %s{path} could not be cleared either: %s{ex.Message}"

/// The tree the index at `dbPath` is sealed at; `None` when it is not sealed or the seal
/// cannot be read.
let load (dbPath: string) : string option =
    let path = pathOf dbPath

    try
        if File.Exists path then
            match File.ReadAllText(path).Trim() with
            | "" -> None
            | tree -> Some tree
        else
            None
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None
