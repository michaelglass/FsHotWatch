/// The READMEs tell a user how to write `build.buildTemplate`. A placeholder they name
/// that the code does not substitute reaches the build command verbatim, so these tests
/// pin every placeholder the docs show for it to the one the code replaces.
module FsHotWatch.Tests.BuildTemplateDocsTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open Swensen.Unquote
open FsHotWatch.Build
open FsHotWatch.Tests.RepoTasks

/// Every `{name}` token on a line of `relativePath` that mentions `buildTemplate`.
let private documentedPlaceholders (relativePath: string) =
    File.ReadAllLines(Path.Combine(repoRoot (), relativePath))
    |> Array.filter (fun line -> line.Contains "buildTemplate")
    |> Array.collect (fun line -> [| for m in Regex.Matches(line, @"\{[A-Za-z]+\}") -> m.Value |])
    |> Array.distinct
    |> List.ofArray

[<Theory>]
[<InlineData("README.md")>]
[<InlineData("src/FsHotWatch.Build/README.md")>]
let ``the buildTemplate placeholder the docs name is the one the build plugin substitutes`` (relativePath: string) =
    test <@ documentedPlaceholders relativePath = [ BuildPlugin.TemplatePlaceholder ] @>
