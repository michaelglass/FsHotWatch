module FsHotWatch.Tests.CoverageMergeTests

open Xunit
open FsHotWatch.TestPrune.CoverageMerge

let private cobertura (pkg: string) (file: string) (lines: (int * int) list) =
    let linesXml =
        lines
        |> List.map (fun (n, h) -> sprintf "<line number=\"%d\" hits=\"%d\" />" n h)
        |> String.concat ""

    sprintf
        "<?xml version=\"1.0\"?><coverage><packages><package name=\"%s\"><classes><class filename=\"%s\" name=\"%s\"><lines>%s</lines></class></classes></package></packages></coverage>"
        pkg
        file
        file
        linesXml

[<Fact>]
let ``mergePerLineMax keeps higher hit count from baseline`` () =
    let baseline = parse (cobertura "Foo.dll" "Foo.fs" [ (10, 3); (11, 1) ])
    let partial = parse (cobertura "Foo.dll" "Foo.fs" [ (10, 0); (11, 0); (12, 2) ])
    let merged = mergePerLineMax baseline partial
    Assert.Equal(3, hits merged "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits merged "Foo.dll" "Foo.fs" 11)
    Assert.Equal(2, hits merged "Foo.dll" "Foo.fs" 12)

[<Fact>]
let ``mergePerLineMax includes files only in partial`` () =
    let baseline = parse (cobertura "Foo.dll" "Foo.fs" [ (10, 1) ])
    let partial = parse (cobertura "Foo.dll" "Bar.fs" [ (5, 1) ])
    let merged = mergePerLineMax baseline partial
    Assert.Equal(1, hits merged "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits merged "Foo.dll" "Bar.fs" 5)

[<Fact>]
let ``toCobertura emits well-formed XML with line-rate`` () =
    let merged = parse (cobertura "Foo.dll" "Foo.fs" [ (10, 1); (11, 0) ])
    let xml = toCobertura merged
    Assert.Contains("<coverage", xml)
    Assert.Contains("line-rate=\"0.5\"", xml)
    Assert.Contains("filename=\"Foo.fs\"", xml)

[<Fact>]
let ``parse tolerates multiple class elements sharing a filename`` () =
    // MTP cobertura can split a file across multiple <class> elements
    // (inner types, method groupings). Take max per line.
    let xml =
        """<?xml version="1.0"?><coverage><packages><package name="Foo.dll"><classes>
<class filename="Foo.fs" name="OuterType"><lines><line number="10" hits="1" /></lines></class>
<class filename="Foo.fs" name="InnerType"><lines><line number="10" hits="5" /><line number="11" hits="0" /></lines></class>
</classes></package></packages></coverage>"""

    let parsed = parse xml
    Assert.Equal(5, hits parsed "Foo.dll" "Foo.fs" 10)
    Assert.Equal(0, hits parsed "Foo.dll" "Foo.fs" 11)

[<Fact>]
let ``parse returns empty on empty input`` () =
    Assert.True(parse "" |> Map.isEmpty)
    Assert.True(parse "   " |> Map.isEmpty)

[<Fact>]
let ``serialize round-trips through parse`` () =
    let original = parse (cobertura "Foo.dll" "Foo.fs" [ (10, 3); (11, 1) ])
    let serialized = serialize original
    let reparsed = parse serialized
    Assert.Equal(3, hits reparsed "Foo.dll" "Foo.fs" 10)
    Assert.Equal(1, hits reparsed "Foo.dll" "Foo.fs" 11)

[<Fact>]
let ``parse handles cobertura with no packages`` () =
    let empty =
        """<?xml version="1.0"?><coverage line-rate="1"><packages /></coverage>"""

    Assert.True(parse empty |> Map.isEmpty)
