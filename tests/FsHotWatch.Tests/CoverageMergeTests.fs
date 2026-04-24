module FsHotWatch.Tests.CoverageMergeTests

open Xunit
open FsHotWatch.TestPrune.CoverageMerge

[<Fact>]
let ``mergePerLineMax keeps higher hit count from baseline`` () =
    let baseline = parse """{"Foo.dll":{"Foo.fs":{"10":3,"11":1}}}"""
    let partial = parse """{"Foo.dll":{"Foo.fs":{"10":0,"11":0,"12":2}}}"""
    let merged = mergePerLineMax baseline partial
    Assert.Equal(3, hits merged "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits merged "Foo.dll" "Foo.fs" 11)
    Assert.Equal(2, hits merged "Foo.dll" "Foo.fs" 12)

[<Fact>]
let ``mergePerLineMax includes files only in partial`` () =
    let baseline = parse """{"Foo.dll":{"Foo.fs":{"10":1}}}"""
    let partial = parse """{"Foo.dll":{"Bar.fs":{"5":1}}}"""
    let merged = mergePerLineMax baseline partial
    Assert.Equal(1, hits merged "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits merged "Foo.dll" "Bar.fs" 5)

[<Fact>]
let ``toCobertura emits well-formed XML with line-rate`` () =
    let merged = parse """{"Foo.dll":{"Foo.fs":{"10":1,"11":0}}}"""
    let xml = toCobertura merged
    Assert.Contains("<coverage", xml)
    Assert.Contains("line-rate=\"0.5\"", xml)
    Assert.Contains("filename=\"Foo.fs\"", xml)

[<Fact>]
let ``parse tolerates coverlet-style siblings like Branches and Methods`` () =
    let json =
        """{"Foo.dll":{"Foo.fs":{"Lines":{"10":1,"11":0},"Branches":[],"Methods":{}}}}"""
    // Accept either shape: direct lineNumber->hits OR nested under "Lines".
    let parsed = parse json
    // With the "Lines" wrapper, implementation must recognize it and pull out the line map.
    Assert.Equal(1, hits parsed "Foo.dll" "Foo.fs" 10)
    Assert.Equal(0, hits parsed "Foo.dll" "Foo.fs" 11)

[<Fact>]
let ``serialize round-trips through parse`` () =
    let original = parse """{"Foo.dll":{"Foo.fs":{"10":3,"11":1}}}"""
    let serialized = serialize original
    let reparsed = parse serialized
    Assert.Equal(3, hits reparsed "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits reparsed "Foo.dll" "Foo.fs" 11)
