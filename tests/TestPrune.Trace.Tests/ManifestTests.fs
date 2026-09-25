module TestPrune.Trace.Tests.ManifestTests

open System.IO
open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

let private row id kind typeName mem doc first last =
    { Id = id
      Kind = kind
      Assembly = "A"
      TypeName = typeName
      Member = mem
      Document = doc
      FirstLine = first
      LastLine = last }

[<Fact>]
let ``a manifest round-trips`` () =
    let dir = Directory.CreateTempSubdirectory().FullName

    try
        let m =
            { Rows =
                [| row 0 UserMethod "N.M" "f" (Some "/r/M.fs") 3 5
                   row 1 UnionCase "N.M+U" "X" None 0 0
                   row 2 GeneratedMethod "N.M+f@3" "Invoke" (Some "/r/M.fs") 3 3
                   row 3 StaticCtor "<StartupCode$A>.$N" ".cctor" None 0 0
                   row 4 TypeUse "N.T" "" None 0 0 |]
              Documents = Map.ofList [ "/r/M.fs", "ab12" ]
              IdCount = 5 }

        Manifest.write dir m
        test <@ Manifest.read dir = Ok m @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a field containing a tab is refused at write time`` () =
    let dir = Directory.CreateTempSubdirectory().FullName

    try
        let bad =
            { Rows = [| row 0 UserMethod "N\tM" "f" None 0 0 |]
              Documents = Map.empty
              IdCount = 1 }

        raises<System.ArgumentException> <@ Manifest.write dir bad @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``an unknown probe kind is an error, not an exception`` () =
    let dir = Directory.CreateTempSubdirectory().FullName

    try
        File.WriteAllText(Path.Combine(dir, "manifest.tsv"), "0\tbogus\tA\tN.M\tf\t\t0\t0\n")
        File.WriteAllText(Path.Combine(dir, "documents.tsv"), "")
        test <@ Manifest.read dir |> Result.isError @>
    finally
        Directory.Delete(dir, true)

[<Fact>]
let ``a missing manifest is an error`` () =
    let dir = Directory.CreateTempSubdirectory().FullName

    try
        test <@ Manifest.read dir |> Result.isError @>
    finally
        Directory.Delete(dir, true)
