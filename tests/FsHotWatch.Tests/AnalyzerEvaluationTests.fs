module FsHotWatch.Tests.AnalyzerEvaluationTests

open System
open System.IO
open System.Xml.Linq
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsHotWatch.Analyzers
open FsHotWatch.ProcessHelper

let private sdkVersion = "expected-sdk"
let private properties =
    [ "MSBuildVersion", "expected-msbuild"; "NETCoreSdkVersion", sdkVersion
      "Configuration", "Debug"; "TargetFramework", "net10.0"; "Platform", "AnyCPU" ]

let private membership paths =
    let items = paths |> List.map (fun path -> XElement(XName.Get "Item", XAttribute(XName.Get "path", path)))
    let fields = properties |> List.map (fun (key, value) ->
        XElement(XName.Get "Property", XAttribute(XName.Get "name", key), XAttribute(XName.Get "value", value)))
    XElement(XName.Get "Membership", XElement(XName.Get "Compile", items), XElement(XName.Get "Effective", fields))

let private output paths =
    JsonSerializer.Serialize(
        {| Items = {| Compile = paths |> List.map (fun path -> {| FullPath = path |}) |}
           Properties = Map.ofList properties |})

let private refuses action = Assert.ThrowsAny<Exception>(Action action) |> ignore

[<Fact>]
let ``complete SDK JSON preserves ordered duplicate compile inputs``() =
    let first = Path.Combine(Path.GetTempPath(), "First.fs")
    let second = Path.Combine(Path.GetTempPath(), "Second.fs")
    let paths = [ first; second; first ]
    AnalyzerEvaluation.validateMembership sdkVersion (membership paths) (output paths)
    refuses (fun () -> AnalyzerEvaluation.validateMembership sdkVersion (membership paths) (output [ first; first; second ]))

[<Fact>]
let ``an explicit empty evaluated Compile array is coherent``() =
    AnalyzerEvaluation.validateMembership sdkVersion (membership []) (output [])

[<Theory>]
[<InlineData("")>]
[<InlineData("not-json")>]
[<InlineData("{}")>]
[<InlineData("{\"Items\":{}}")>]
[<InlineData("{\"Items\":{\"Compile\":null}}")>]
[<InlineData("{\"Items\":{\"Compile\":[]}}")>]
let ``malformed or incomplete SDK evidence cannot validate``(json: string) =
    refuses (fun () -> AnalyzerEvaluation.validateMembership sdkVersion (membership []) json)

[<Theory>]
[<InlineData("empty")>]
[<InlineData("configuration-only")>]
[<InlineData("missing")>]
[<InlineData("duplicate")>]
[<InlineData("extra")>]
[<InlineData("empty-msbuild")>]
[<InlineData("empty-sdk")>]
[<InlineData("wrong-sdk")>]
let ``private effective context requires exact coherent SDK schema``(damage: string) =
    let value = membership []
    let effective = value.Element(XName.Get "Effective")
    let field key = effective.Elements() |> Seq.find (fun p -> p.Attribute(XName.Get "name").Value = key)
    match damage with
    | "empty" -> effective.RemoveNodes()
    | "configuration-only" ->
        let configuration = XElement(field "Configuration")
        effective.RemoveNodes()
        effective.Add configuration
    | "missing" -> (field "NETCoreSdkVersion").Remove()
    | "duplicate" -> effective.Add(XElement(field "MSBuildVersion"))
    | "extra" -> effective.Add(XElement(XName.Get "Property", XAttribute(XName.Get "name", "Unexpected"), XAttribute(XName.Get "value", "")))
    | "empty-msbuild" -> (field "MSBuildVersion").SetAttributeValue(XName.Get "value", "")
    | "empty-sdk" -> (field "NETCoreSdkVersion").SetAttributeValue(XName.Get "value", " ")
    | _ -> (field "NETCoreSdkVersion").SetAttributeValue(XName.Get "value", "another-sdk")
    refuses (fun () -> AnalyzerEvaluation.validateMembership sdkVersion value (output []))

[<Theory>]
[<InlineData("missing")>]
[<InlineData("duplicate")>]
[<InlineData("number")>]
[<InlineData("null")>]
[<InlineData("wrong-sdk")>]
let ``SDK JSON properties require unique strings matching captured identity``(damage: string) =
    let json = JsonNode.Parse(output [])
    let fields = json["Properties"].AsObject()
    match damage with
    | "missing" -> fields.Remove("NETCoreSdkVersion") |> ignore
    | "number" -> fields["NETCoreSdkVersion"] <- JsonValue.Create(42)
    | "null" -> fields["NETCoreSdkVersion"] <- null
    | "wrong-sdk" -> fields["NETCoreSdkVersion"] <- JsonValue.Create("another-sdk")
    | _ -> ()
    let text =
        if damage = "duplicate" then
            json.ToJsonString().Replace("\"Properties\":{", "\"Properties\":{\"MSBuildVersion\":\"expected-msbuild\",")
        else json.ToJsonString()
    refuses (fun () -> AnalyzerEvaluation.validateMembership sdkVersion (membership []) text)

[<Fact>]
let ``complete matching JSON cannot authorize an unfinished output drain``() =
    let json = output []
    AnalyzerEvaluation.validateOutcome sdkVersion (membership []) (Succeeded(ProcessOutput.Drained json))
    refuses (fun () ->
        AnalyzerEvaluation.validateOutcome sdkVersion (membership [])
            (Succeeded(ProcessOutput.DrainTimedOut(json, TimeSpan.FromSeconds 1.0))))
