module FsHotWatch.Tests.AnalyzerEvaluationTests

open System
open System.IO
open System.Xml.Linq
open System.Text.Json
open Xunit
open FsHotWatch.Analyzers

let private membership paths =
    let items = paths |> List.map (fun path -> XElement(XName.Get "Item", XAttribute(XName.Get "path", path)))
    XElement(XName.Get "Membership",
        XElement(XName.Get "Compile", items),
        XElement(XName.Get "Effective",
            XElement(XName.Get "Property", XAttribute(XName.Get "name", "MSBuildVersion"), XAttribute(XName.Get "value", "expected-sdk"))))

let private output paths =
    JsonSerializer.Serialize(
        {| Items = {| Compile = paths |> List.map (fun path -> {| FullPath = path |}) |}
           Properties = Map.ofList [ "MSBuildVersion", "expected-sdk" ] |})

[<Fact>]
let ``complete SDK JSON preserves ordered duplicate compile inputs``() =
    let first = Path.Combine(Path.GetTempPath(), "First.fs")
    let second = Path.Combine(Path.GetTempPath(), "Second.fs")
    let paths = [ first; second; first ]
    AnalyzerEvaluation.validateMembership (membership paths) (output paths)
    Assert.ThrowsAny<Exception>(fun () ->
        AnalyzerEvaluation.validateMembership (membership paths) (output [ first; first; second ])) |> ignore

[<Fact>]
let ``an explicit empty evaluated Compile array is coherent``() =
    AnalyzerEvaluation.validateMembership (membership []) (output [])

[<Theory>]
[<InlineData("")>]
[<InlineData("not-json")>]
[<InlineData("{}")>]
[<InlineData("{\"Items\":{}}")>]
[<InlineData("{\"Items\":{\"Compile\":null}}")>]
[<InlineData("{\"Items\":{\"Compile\":[]}}")>]
[<InlineData("{\"Items\":{\"Compile\":[]},\"Properties\":{\"MSBuildVersion\":\"other-sdk\"}}")>]
let ``malformed incomplete or mismatched SDK evidence cannot validate``(json: string) =
    Assert.ThrowsAny<Exception>(fun () -> AnalyzerEvaluation.validateMembership (membership []) json) |> ignore
