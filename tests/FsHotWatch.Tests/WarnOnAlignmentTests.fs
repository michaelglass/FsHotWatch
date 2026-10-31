/// Every shipped package enforces the SAME compiler warnings.
///
/// Every src/ project sets TreatWarningsAsErrors, but only the core package used to
/// carry a `<WarnOn>` block, so an unused binding (FS1182) was a build error in one
/// package and invisible in the rest. The set is declared once, in
/// src/Directory.Build.props, so a package added later inherits it instead of
/// depending on someone remembering to copy a block.
module FsHotWatch.Tests.WarnOnAlignmentTests

open System.IO
open System.Xml.Linq
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks

let private requiredWarnOn =
    set [ "3390"; "1104"; "0020"; "0044"; "0025"; "3391"; "0067"; "0021"; "1182" ]

let private warnOnCodes (path: string) =
    XDocument.Load(path).Descendants(XName.Get "WarnOn")
    |> Seq.collect (fun element -> element.Value.Split([| ','; ';'; '\n'; '\r'; ' '; '\t' |]))
    |> Seq.map _.Trim()
    |> Seq.filter (fun code -> code <> "" && not (code.StartsWith "$("))
    |> Set.ofSeq

[<Fact>]
let ``src Directory.Build.props declares the whole WarnOn set and imports the root props`` () =
    let props = Path.Combine(repoRoot (), "src", "Directory.Build.props")
    test <@ File.Exists props @>
    test <@ warnOnCodes props = requiredWarnOn @>
    // A nested Directory.Build.props STOPS MSBuild's upward search, so without an
    // explicit import of the root one every src/ project would silently lose it.
    test <@ File.ReadAllText(props).Contains "GetPathOfFileAbove('Directory.Build.props'" @>

[<Fact>]
let ``no src project redeclares WarnOn, so none can drift from the shared set`` () =
    let srcDir = Path.Combine(repoRoot (), "src")

    let projects =
        Directory.GetDirectories srcDir
        |> Array.collect (fun dir -> Directory.GetFiles(dir, "*.fsproj"))

    test <@ projects.Length >= 9 @>

    let redeclaring =
        projects
        |> Array.filter (warnOnCodes >> Set.isEmpty >> not)
        |> Array.map Path.GetFileName

    test <@ Array.isEmpty redeclaring @>
