/// `fshw-lookup [check|confirm] [repo-dir]`: the lookup verdict of the checkout at `repo-dir`
/// (default: the current directory) for `check` (the default) or `confirm`, read from the
/// outcome stores alone (`Lookup.ofRepo`). One line on stdout; exit 0 green, 1 red, 3 no
/// verdict (missing evidence, no recorded producer, or an unreadable configuration), 2 usage.
/// With `FSHW_LOOKUP_PHASES` set, stderr also gets one `phase <name> <ms>` or `count <name> <n>` line
/// per phase (experiment E5), and a timed walk of the whole root: the scope AUTOMATION-1577 adds.
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

let private ms (ticks: int64) =
    (float ticks * 1000.0 / float Diagnostics.Stopwatch.Frequency)
        .ToString("0.000", Globalization.CultureInfo.InvariantCulture)

/// The variable that turns on the per-phase breakdown (E5): one `phase <name> <ms>` or
/// `count <name> <n>` line per phase on stderr, in order, after the answer.
[<Literal>]
let private PhasesVariable = "FSHW_LOOKUP_PHASES"

/// A timer that records each phase and count, and prints them when asked.
let private recording () =
    let lines = Collections.Generic.List<string>()

    let timer =
        { new Lookup.PhaseTimer with
            member _.Time(phase, work) =
                let start = Diagnostics.Stopwatch.GetTimestamp()
                let result = work ()
                lines.Add("phase " + phase + " " + ms (Diagnostics.Stopwatch.GetTimestamp() - start))
                result

            member _.Count(name, count) =
                lines.Add(
                    "count "
                    + name
                    + " "
                    + count.ToString(Globalization.CultureInfo.InvariantCulture)
                ) }

    timer, lines

/// What AUTOMATION-1577 adds to a lookup: a walk of the whole repository root (the scope of a file
/// command's pattern), timed alone so its cost can be read beside the tree walk's.
let private rootWalk (timer: Lookup.PhaseTimer) (repoRoot: string) =
    let walked =
        timer.Time("root-walk-1577", fun () -> SafeWalk.walk SafeWalk.SourceExcludedDirs "*" repoRoot)

    timer.Count("root-files", walked.Files.Length)

[<EntryPoint>]
let main argv =
    match parse (List.ofArray argv) with
    | Invocation.Usage problem ->
        Console.Error.WriteLine("fshw-lookup: " + problem)
        Console.Error.WriteLine "usage: fshw-lookup [check|confirm] [repo-dir]"
        2
    | Invocation.Run(command, repoRoot) ->
        let timed =
            not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable PhasesVariable))

        let timer, lines = recording ()
        let total = Diagnostics.Stopwatch.GetTimestamp()

        let read =
            if timed then
                Lookup.ofRepoWith timer repoRoot command ignore
            else
                Lookup.ofRepo repoRoot command ignore

        if timed then
            lines.Add("phase total " + ms (Diagnostics.Stopwatch.GetTimestamp() - total))
            rootWalk timer repoRoot

            for line in lines do
                Console.Error.WriteLine line

        match read with
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
