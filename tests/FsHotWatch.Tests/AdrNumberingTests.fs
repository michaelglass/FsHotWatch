/// Every ADR under `docs/` has its own number (AUTOMATION-1016): two ADRs sharing a number
/// make a citation such as "ADR-036" ambiguous. A new ADR takes the next number after the
/// highest one in `docs/`.
module FsHotWatch.Tests.AdrNumberingTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote

let private docsDir =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "docs"))

/// Each `adr-NNN-*.md` file's number, from its name and from its `# ADR-NNN:` title.
let private adrs () =
    Directory.GetFiles(docsDir, "adr-*.md")
    |> Array.map (fun path ->
        let name = Path.GetFileName path
        let fromName = Regex.Match(name, "^adr-(\\d{3})-").Groups[1].Value

        let fromTitle =
            File.ReadLines path
            |> Seq.tryHead
            |> Option.map (fun line -> Regex.Match(line, "^# ADR-(\\d{3}):").Groups[1].Value)
            |> Option.defaultValue ""

        name, fromName, fromTitle)
    |> Array.toList

[<Fact(Timeout = 15000)>]
let ``every ADR has its own number, and its title carries the number in its file name`` () =
    let all = adrs ()
    test <@ not all.IsEmpty @>

    let mismatched =
        all
        |> List.filter (fun (_, fromName, fromTitle) -> fromName = "" || fromName <> fromTitle)

    test <@ List.isEmpty mismatched @>

    let shared =
        all
        |> List.groupBy (fun (_, n, _) -> n)
        |> List.filter (fun (_, files) -> files.Length > 1)
        |> List.map (fun (n, files) -> n, files |> List.map (fun (name, _, _) -> name))

    test <@ List.isEmpty shared @>
