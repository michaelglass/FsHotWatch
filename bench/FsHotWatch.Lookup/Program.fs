/// `fshw-lookup [check|confirm] [repo-dir]`: the lookup verdict of the checkout at `repo-dir`
/// (default: the current directory) for `check` (the default) or `confirm`, read from the
/// outcome stores alone (`Lookup.ofRepo`). One line on stdout; exit 0 green, 1 red, 3 no
/// verdict (missing evidence, no recorded producer, or an unreadable configuration), 2 usage.
/// No printf: text is built with `+` and written with `Console`, so FSharp.Core's printf engine
/// (and the `%A`/reflection code behind it) stays out of the Native AOT image.
module FsHotWatch.Lookup.Program

open System
open FsHotWatch

[<RequireQualifiedAccess>]
type private Invocation =
    | Run of command: Requirements.Command * repoRoot: string
    | Usage of problem: string

let private commandOf (word: string) =
    match word with
    | "check" -> Some Requirements.Command.Check
    | "confirm" -> Some Requirements.Command.Confirm
    | _ -> None

let private parse (argv: string list) : Invocation =
    let here = Environment.CurrentDirectory

    match argv with
    | [] -> Invocation.Run(Requirements.Command.Check, here)
    | [ word ] ->
        match commandOf word with
        | Some command -> Invocation.Run(command, here)
        | None -> Invocation.Usage("not a command: " + word)
    | [ word; dir ] ->
        match commandOf word with
        | Some command -> Invocation.Run(command, IO.Path.GetFullPath dir)
        | None -> Invocation.Usage("not a command: " + word)
    | _ -> Invocation.Usage "too many arguments"

let private names (requirements: string list) = String.Join(", ", requirements)

[<EntryPoint>]
let main argv =
    match parse (List.ofArray argv) with
    | Invocation.Usage problem ->
        Console.Error.WriteLine("fshw-lookup: " + problem)
        Console.Error.WriteLine "usage: fshw-lookup [check|confirm] [repo-dir]"
        2
    | Invocation.Run(command, repoRoot) ->
        match Lookup.ofRepo repoRoot command ignore with
        | Lookup.Read.Answered(tree, producer, answer) ->
            let at = "tree " + tree + ", producer " + producer

            match answer with
            | LookupVerdict.Answer.Green ->
                Console.Out.WriteLine("green: " + at)
                0
            | LookupVerdict.Answer.Red failing ->
                Console.Out.WriteLine("red: " + names failing + "; " + at)
                1
            | LookupVerdict.Answer.Missing missing ->
                Console.Out.WriteLine("missing: " + names missing + "; " + at)
                3
        | Lookup.Read.NoProducer tree ->
            Console.Out.WriteLine("no producer recorded: tree " + tree)
            3
        | Lookup.Read.Unconfigured reason ->
            Console.Out.WriteLine("unconfigured: " + reason)
            3
