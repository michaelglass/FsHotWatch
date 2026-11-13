/// A repository host's checkers, one per checker configuration.
///
/// Sessions whose configuration is equal check through one checker. FCS keys its
/// framework imports and `TcGlobals` by the framework references, not by the project,
/// so sessions on one SDK then hold one copy of both.
module FsHotWatch.CheckerPartitions

// TransparentCompiler.CacheSizes is marked experimental; it is the checker's configuration.
#nowarn "57"

open System.Collections.Concurrent
open FSharp.Compiler.CodeAnalysis

/// The checkers built so far, by configuration. `make` builds the checker for a
/// configuration the first time any session asks for it.
type Partitions<'Checker>(make: TransparentCompiler.CacheSizes -> 'Checker) =
    let checkers =
        ConcurrentDictionary<TransparentCompiler.CacheSizes, System.Lazy<'Checker>>()

    /// The checker for `sizes`, built once however many sessions ask at once.
    member _.For(sizes: TransparentCompiler.CacheSizes) : 'Checker =
        checkers.GetOrAdd(sizes, fun key -> lazy (make key)).Value

    /// How many partitions exist.
    member _.Count: int = checkers.Count

/// The options FCS reads into `TcGlobals` that its framework-imports cache key leaves
/// out (AUTOMATION-1656), distinct and sorted: two projects with different keys must
/// not check through one checker.
///
/// FCS builds `TcGlobals` once per framework-imports key (the framework references,
/// primary assembly, framework directories, F# binaries, language version and nullness)
/// from the options of the project it happened to bootstrap first, and hands it to every
/// project with an equal key. A project whose `--reflectionfree` reached `TcGlobals` first
/// made every other project report `sprintf "%A"` as an error, and the reverse hid the
/// error from the project that sets it. The other options here reach `TcGlobals` the same
/// way (`TcImports.BuildFrameworkTcImports`). Per-project options such as `--define`,
/// `--nowarn`, `--warnon` and `--warnaserror` live in the project's `TcConfig` and are not
/// shared.
let globalsOptions (otherOptions: string seq) : string list =
    let prefixes =
        [ "--reflectionfree"
          "--realsig"
          "--quotations-debug"
          "--pathmap:"
          "--nodebugdata"
          "--compiling-fslib" ]

    otherOptions
    |> Seq.filter (fun option ->
        prefixes
        |> List.exists (fun prefix -> option.StartsWith(prefix, System.StringComparison.Ordinal)))
    |> Seq.distinct
    |> Seq.sort
    |> List.ofSeq

/// A checker and the siblings it checks projects with other `globalsOptions` through.
/// The first key asked for checks through `first`; each other key through a sibling
/// `make` builds the first time it is asked for. In a repository without such options
/// (or with one set of them) only `first` is ever used.
type Family<'Checker>(first: 'Checker, make: unit -> 'Checker) =
    let members = ConcurrentDictionary<string list, System.Lazy<'Checker>>()
    let gate = System.Threading.Lock()
    let mutable firstTaken = false
    /// Built so far, newest first.
    let mutable siblings: 'Checker list = []

    /// The checker for a key not seen before: `first` for the first such key, a new
    /// sibling for each later one. Each key's `Lazy` runs this at most once.
    let assign () =
        Locking.locked gate (fun () ->
            if firstTaken then
                let sibling = make ()
                siblings <- sibling :: siblings
                sibling
            else
                firstTaken <- true
                first)

    /// The checker for a project with these options.
    member _.For(otherOptions: string seq) : 'Checker =
        members.GetOrAdd(globalsOptions otherOptions, (fun _ -> lazy (assign ()))).Value

    /// `first` and every sibling built so far.
    member _.Members: 'Checker list =
        first :: Locking.locked gate (fun () -> List.rev siblings)

let private families =
    System.Runtime.CompilerServices.ConditionalWeakTable<FSharpChecker, Family<FSharpChecker>>()

/// Check the projects of `checker` whose `globalsOptions` differ from the first one's
/// through siblings `make` builds. Idempotent: the first `make` stays.
let adopt (checker: FSharpChecker) (make: unit -> FSharpChecker) : unit =
    if not (isNull (box checker)) then
        families.GetValue(checker, (fun c -> Family(c, make))) |> ignore

/// The checker of `checker`'s family that checks a project with `otherOptions`:
/// `checker` itself when it was never adopted.
let forOptions (checker: FSharpChecker) (otherOptions: string seq) : FSharpChecker =
    if isNull (box checker) then
        checker
    else
        match families.TryGetValue checker with
        | true, family -> family.For otherOptions
        | false, _ -> checker

/// `checker` and every sibling it has built so far.
let membersOf (checker: FSharpChecker) : FSharpChecker list =
    if isNull (box checker) then
        [ checker ]
    else
        match families.TryGetValue checker with
        | true, family -> family.Members
        | false, _ -> [ checker ]
