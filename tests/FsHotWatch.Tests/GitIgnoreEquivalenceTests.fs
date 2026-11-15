/// `PathFilter.IgnoreRules` answers exactly what the `Ignore` package answers for the same
/// lines (E5, AUTOMATION-1696): it is the same rules, evaluated in the same order, with a
/// rule skipped only when a path cannot contain a literal every match of that rule holds.
/// The package is the oracle here, and these properties are what lets the faster evaluation
/// stand in for it: one definition of what a `.gitignore` leaves in, checked file by file.
module FsHotWatch.Tests.GitIgnoreEquivalenceTests

open System.IO
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.FSharp
open Ignore
open FsHotWatch
open FsHotWatch.Tests.TestHelpers

/// Path segments: ASCII names, names differing only by case, the Kelvin sign (which a
/// case-insensitive regex matches to `k`), a non-ASCII letter and regex-significant text.
let private names =
    [ "a"
      "b"
      "ab"
      "A"
      "AB"
      "log"
      "x.log"
      "xlog"
      "k"
      "K"
      "K"
      "é"
      "a+b"
      "build"
      "Build"
      "out" ]

/// Gitignore pattern pieces: the names, every glob form, character classes (including one
/// whose `]` is a member), escapes, and text `Ignore` passes to the regex unescaped.
let private atoms =
    names
    @ [ "*"
        "**"
        "?"
        "*.log"
        "a*"
        "*b"
        "[ab]"
        "[!a]"
        "[]a]"
        "[a-z]"
        "\\#x"
        "a|b"
        "(a)"
        "a$"
        "a\\ "
        "{a,b}"
        "x^y" ]

let private lineGen: Gen<string> =
    let built =
        gen {
            let! count = Gen.choose (1, 3)
            let! parts = Gen.listOfLength count (Gen.elements atoms)
            let! leading = Gen.frequency [ 3, Gen.constant ""; 1, Gen.constant "/" ]
            let! trailing = Gen.frequency [ 3, Gen.constant ""; 1, Gen.constant "/" ]
            let! negated = Gen.frequency [ 3, Gen.constant ""; 1, Gen.constant "!" ]
            return negated + leading + String.concat "/" parts + trailing
        }

    Gen.frequency
        [ 12, built
          1, Gen.elements [ ""; "# comment"; "  "; "**"; "!"; "/"; " a "; "!**" ] ]

let private pathGen: Gen<string> =
    gen {
        let! depth = Gen.choose (1, 4)
        let! parts = Gen.listOfLength depth (Gen.elements names)
        return String.concat "/" parts
    }

/// `Ignore` as its own `Add` builds it, one `IgnoreRule` per line, with each distinct line's
/// rule built once per run instead of once per matcher.
///
/// `Ignore.Add(line)` is `OriginalRules.Add line; rules.Add(IgnoreRule line)` (Ignore 0.2.1),
/// and an `IgnoreRule` is a function of its line: a regex and a negation flag. Its regex is
/// `RegexOptions.Compiled`, compiled on first match, and building every rule anew for each
/// generated case made the oracle three quarters of the line property's time (AUTOMATION-1695's
/// CI budget). The answers still come from `Ignore`'s own `IsIgnored` over `Ignore`'s own
/// rules; `the shared-rule oracle answers what a fresh Ignore answers` pins that. The private
/// field is the package's, read by reflection: a real boundary, checked loudly below.
let private compiledRules =
    System.Collections.Concurrent.ConcurrentDictionary<string, IgnoreRule>(System.StringComparer.Ordinal)

let private rulesField =
    match
        typeof<Ignore>
            .GetField(
                "rules",
                System.Reflection.BindingFlags.NonPublic
                ||| System.Reflection.BindingFlags.Instance
            )
    with
    | null -> failwith "Ignore no longer keeps its rules in a private `rules` field: rebuild `oracle` on Ignore.Add"
    | field -> field

let private oracle (lines: string list) =
    let ig = Ignore()
    // Reflection boundary: the package's own rule list, filled as `Add` fills it.
    let rules = rulesField.GetValue ig :?> System.Collections.Generic.List<IgnoreRule>

    for line in lines do
        // A line whose regex .NET refuses throws here, as `Add` throws, and is never cached.
        let rule = compiledRules.GetOrAdd(line, IgnoreRule)
        ig.OriginalRules.Add line
        rules.Add rule

    ig

/// What `oracle` stands in for: a fresh `Ignore` built by `Add`, every rule compiled anew.
let private freshIgnore (lines: string list) =
    (Ignore(), lines) ||> List.fold (fun (ig: Ignore) l -> ig.Add l)

/// `work`'s answer, or the pattern `Ignore` could not compile. Some valid gitignore lines
/// (`**/**/x`) are regexes .NET refuses; the replacement must refuse exactly those too.
let private attempt (work: unit -> 'T) : Result<'T, string> =
    try
        Ok(work ())
    with :? System.Text.RegularExpressions.RegexParseException as e ->
        Error e.Message

[<Fact(Timeout = PropertyTestCapMs)>]
let ``IgnoreRules answers what Ignore answers for every line set and path`` () =
    let ignored = ref 0
    let kept = ref 0

    let case =
        gen {
            let! lineCount = Gen.choose (0, 8)
            let! lines = Gen.listOfLength lineCount lineGen
            let! paths = Gen.listOfLength 24 pathGen
            return lines, paths
        }

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(3000),
        Prop.forAll (Arb.fromGen case) (fun (lines, paths) ->
            match attempt (fun () -> oracle lines), attempt (fun () -> PathFilter.IgnoreRules.Of lines) with
            | Ok expected, Ok actual ->
                paths
                |> List.forall (fun p ->
                    let want = expected.IsIgnored p

                    if want then
                        ignored.Value <- ignored.Value + 1
                    else
                        kept.Value <- kept.Value + 1

                    actual.IsIgnored p = want)
            | Error expected, Error actual -> expected = actual
            | Ok _, Error _
            | Error _, Ok _ -> false)
    )

    // A property that never ignores (or never keeps) anything proves nothing about it.
    if ignored.Value < 5000 || kept.Value < 5000 then
        failwith $"only {ignored.Value} ignored and {kept.Value} kept: the generator misses a region"

[<Fact(Timeout = PropertyTestCapMs)>]
let ``the shared-rule oracle answers what a fresh Ignore answers`` () =
    // Lines repeat across cases, so most rules here come from the cache, the case it must get right.
    let case =
        gen {
            let! lineCount = Gen.choose (0, 8)
            let! lines = Gen.listOfLength lineCount lineGen
            let! paths = Gen.listOfLength 24 pathGen
            return lines, paths
        }

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(300),
        Prop.forAll (Arb.fromGen case) (fun (lines, paths) ->
            match attempt (fun () -> freshIgnore lines), attempt (fun () -> oracle lines) with
            | Ok fresh, Ok shared ->
                shared.OriginalRules |> List.ofSeq = lines
                && paths |> List.forall (fun p -> shared.IsIgnored p = fresh.IsIgnored p)
            | Error fresh, Error shared -> fresh = shared
            | Ok _, Error _
            | Error _, Ok _ -> false)
    )

// --- the tree: nested .gitignore files, read the way `GitIgnores` reads them ----------

/// The `GitIgnores` this replaces, as it read a tree before `IgnoreRules`: each directory's
/// rules are its parent's plus its own `.gitignore` (rebased by `rebaseIgnoreLine`), matched
/// by one `Ignore`, and a directory asked in directory form against the same rules with any
/// trailing `/` dropped.
///
/// One per written tree. The matcher for a path depends only on the directory holding it and
/// on the form it is asked in, so it is built once per (directory, form) and asked for every
/// path there. Building it per query compiled every rule of the chain anew for each file,
/// directory and ancestor asked about (`Ignore` compiles a regex per rule on first use), which
/// made this oracle most of the property's time (AUTOMATION-1695's CI budget). The answers are
/// the same: the same lines, read from the same files, in the same `Ignore`.
type private TreeOracle(root: string) =
    let matchers =
        System.Collections.Generic.Dictionary<string * bool, Result<Ignore, string>>()

    let matcherFor (dir: string) (isDirectory: bool) : Result<Ignore, string> =
        match matchers.TryGetValue((dir, isDirectory)) with
        | true, m -> m
        | false, _ ->
            let parts = if dir = "" then [||] else dir.Split '/'
            let dirs = [ for i in 0 .. parts.Length -> String.concat "/" parts[.. i - 1] ]

            let lines =
                dirs
                |> List.collect (fun d ->
                    let file = Path.Combine(root, d, ".gitignore")

                    if File.Exists file then
                        File.ReadAllLines file
                        |> Array.choose (PathFilter.rebaseIgnoreLine d)
                        |> List.ofArray
                    else
                        [])

            let lines =
                if isDirectory then
                    lines
                    |> List.map (fun l -> l.TrimEnd('/'))
                    |> List.filter (fun l -> l <> "" && l <> "!")
                else
                    lines

            let m = attempt (fun () -> oracle lines)
            matchers[(dir, isDirectory)] <- m
            m

    member _.IsIgnored(rel: string, isDirectory: bool) : Result<bool, string> =
        let dir =
            match rel.LastIndexOf '/' with
            | -1 -> ""
            | i -> rel.Substring(0, i)

        matcherFor dir isDirectory
        |> Result.bind (fun m -> attempt (fun () -> m.IsIgnored rel))

    member this.IsUnderIgnored(rel: string) : Result<bool, string> =
        let parts = rel.Split '/'

        // The prefixes in order, stopping at the first that is ignored or fails, as `List.exists`
        // over the per-query answers did.
        let rec firstOf (n: int) =
            if n > parts.Length then
                Ok false
            else
                match this.IsIgnored(String.concat "/" parts[.. n - 1], true) with
                | Ok true -> Ok true
                | Ok false -> firstOf (n + 1)
                | Error e -> Error e

        firstOf 1

let private treeGen =
    gen {
        let! fileCount = Gen.choose (1, 30)
        let! files = Gen.listOfLength fileCount pathGen
        let! ignoreCount = Gen.choose (1, 4)

        let! ignores =
            Gen.listOfLength
                ignoreCount
                (gen {
                    let! depth = Gen.choose (0, 2)
                    let! dir = Gen.listOfLength depth (Gen.elements names)
                    let! lineCount = Gen.choose (2, 8)
                    let! lines = Gen.listOfLength lineCount lineGen
                    return String.concat "/" dir, lines
                })

        return files, ignores
    }

/// Write what fits: a generated file whose path collides with a directory (or, on a
/// case-insensitive file system, with another name) is left out, and the rest is the tree.
let private writeTree (root: string) (files: string list) (ignores: (string * string list) list) =
    let tryWrite (rel: string) (text: string) =
        try
            let path = Path.Combine(root, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, text)
            true
        with
        | :? IOException
        | :? System.UnauthorizedAccessException -> false

    for dir, lines in ignores do
        tryWrite (if dir = "" then ".gitignore" else dir + "/.gitignore") (String.concat "\n" lines + "\n")
        |> ignore

    files |> List.distinct |> List.filter (fun f -> tryWrite f "x")

/// Write `files` and `ignores` under `root` and ask every written file, and every directory above
/// one, what `GitIgnores` and the per-file oracle answer: the queries where they disagree, named,
/// and how many files the oracle ignored and kept.
let private disagreements (root: string) (files: string list) (ignores: (string * string list) list) =
    let written = writeTree root files ignores
    let ignoresNow = PathFilter.GitIgnores root
    let oracleNow = TreeOracle root
    let abs (rel: string) = Path.Combine(root, rel)

    let dirs =
        written
        |> List.collect (fun f ->
            let parts = f.Split '/'
            [ for n in 1 .. parts.Length - 1 -> String.concat "/" parts[.. n - 1] ])
        |> List.distinct

    let fileAnswers =
        written
        |> List.map (fun f -> f, oracleNow.IsIgnored(f, false), attempt (fun () -> ignoresNow.IsIgnored(abs f, false)))

    let wrong =
        [ for f, want, got in fileAnswers do
              if got <> want then
                  sprintf "file %s: oracle %A, GitIgnores %A" f want got
          for d in dirs do
              let want = oracleNow.IsIgnored(d, true)
              let got = attempt (fun () -> ignoresNow.IsIgnored(abs d, true))

              if got <> want then
                  sprintf "directory %s: oracle %A, GitIgnores %A" d want got

              let wantUnder = oracleNow.IsUnderIgnored d
              let gotUnder = attempt (fun () -> ignoresNow.IsUnderIgnored(abs d))

              if gotUnder <> wantUnder then
                  sprintf "under %s: oracle %A, GitIgnores %A" d wantUnder gotUnder ]

    let count (answer: bool) =
        fileAnswers |> List.filter (fun (_, want, _) -> want = Ok answer) |> List.length

    wrong, count true, count false

[<Fact(Timeout = PropertyTestCapMs)>]
let ``GitIgnores answers what the per-file Ignore matcher answers for every file and directory of a tree`` () =
    let ignored = ref 0
    let kept = ref 0

    Check.One(
        Config.QuickThrowOnFailure.WithMaxTest(150),
        Prop.forAll (Arb.fromGen treeGen) (fun (files, ignores) ->
            withTempDir "gitignore-equivalence" (fun root ->
                let wrong, i, k = disagreements root files ignores
                ignored.Value <- ignored.Value + i
                kept.Value <- kept.Value + k
                List.isEmpty wrong))
    )

    if ignored.Value < 200 || kept.Value < 200 then
        failwith $"only {ignored.Value} ignored and {kept.Value} kept files: the generator misses a region"

/// The case GitHub CI's Linux run 43659129229 found (seed 15333788256446128806,614693935018741563):
/// on a case-sensitive file system `A` and `a`, `K` and `k`, `AB` and `ab` are different directories.
[<Fact>]
let ``the case Linux CI found: GitIgnores agrees with the per-file matcher`` () =
    let files =
        [ "x.log/k"
          "ab"
          "A/a/xlog"
          "ab/AB/a/A"
          "k/AB/K/é"
          "a+b/build/out/log"
          "AB/K"
          "b"
          "AB/a"
          "log"
          "build/é"
          "Build"
          "AB/xlog/AB"
          "K/log/ab/b"
          "out/build"
          "xlog/b/x.log/K"
          "b/A/x.log"
          "k/a/a/x.log" ]

    let ignores =
        [ "", [ "/xlog/[]a]"; "K/" ]
          "a", [ "!b/**"; "out/a|b"; "*.log/[!a]/é"; "!/\\#x"; "K/log/(a)"; "!/K/**/" ]
          "b", [ "?"; "*/*b/"; "[]a]/AB"; "ab/{a,b}"; "[ab]/a$/**"; "[!a]/x^y/[a-z]/" ]
          "", [ "**/**/"; "!**"; "[!a]/ab/a$"; "(a)/"; "[]a]/[]a]/"; "**"; "/*/é/" ] ]

    withTempDir "gitignore-equivalence-ci" (fun root ->
        let wrong, _, _ = disagreements root files ignores
        test <@ List.isEmpty wrong @>)

/// The cause of that case, on any file system: `**/**/` compiles in its directory form (`**/**`)
/// and not in its file form (AUTOMATION-1720). A directory query reads only the directory form,
/// so it is answered, as the per-file matcher answers it; a file query fails as that matcher fails.
[<Fact>]
let ``a line only the file form of which cannot compile fails file queries, not directory ones`` () =
    withTempDir "gitignore-forms" (fun root ->
        let wrong, _, _ = disagreements root [ "d/x"; "d/e/y" ] [ "", [ "**/**/" ] ]
        test <@ List.isEmpty wrong @>

        let ignores = PathFilter.GitIgnores root
        test <@ ignores.IsIgnored(Path.Combine(root, "d"), true) @>

        let fileQueryFails =
            (attempt (fun () -> ignores.IsIgnored(Path.Combine(root, "d", "x"), false))).IsError

        test <@ fileQueryFails @>)
