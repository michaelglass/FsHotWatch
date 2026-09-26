module TestPrune.Trace.Tests.CtrfTests

open Xunit
open Swensen.Unquote
open TestPrune.Trace
open TestPrune.Trace.Model

[<Fact>]
let ``results.tests rows map each status to an outcome`` () =
    let json =
        """{"results":{"tool":{"name":"xunit"},"tests":[
             {"name":"N.C.a","status":"passed"},
             {"name":"N.C.b","status":"FAILED"},
             {"name":"N.C.c","status":"skipped"},
             {"name":"N.C.d","status":"pending"},
             {"name":"N.C.e","status":"other"}]}}"""

    test
        <@
            Ctrf.parse json = [ { Name = "N.C.a"; Outcome = Passed }
                                { Name = "N.C.b"; Outcome = Failed }
                                { Name = "N.C.c"; Outcome = Skipped }
                                { Name = "N.C.d"; Outcome = Skipped }
                                { Name = "N.C.e"
                                  Outcome = OtherOutcome } ]
        @>

[<Fact>]
let ``a top-level tests array is read when results is absent or has no tests`` () =
    let rows = """[{"name":"N.C.a","status":"passed"}]"""
    let expected = [ { Name = "N.C.a"; Outcome = Passed } ]
    test <@ Ctrf.parse ("""{"tests":""" + rows + "}") = expected @>
    test <@ Ctrf.parse ("""{"results":{},"tests":""" + rows + "}") = expected @>

[<Fact>]
let ``rows without a name or a status, and null rows, are skipped`` () =
    let json =
        """{"results":{"tests":[null,{"status":"passed"},{"name":"N.C.a"},{"name":"N.C.b","status":"passed"}]}}"""

    test <@ Ctrf.parse json = [ { Name = "N.C.b"; Outcome = Passed } ] @>

[<Fact>]
let ``an unreadable report has no outcomes`` () =
    test <@ List.isEmpty (Ctrf.parse "not json") @>
    test <@ List.isEmpty (Ctrf.parse """{"results":{"tests":{"name":"x"}}}""") @>
    test <@ List.isEmpty (Ctrf.parse "{}") @>
    test <@ List.isEmpty (Ctrf.parse """{"results":{"tests":[{"name":1,"status":"passed"}]}}""") @>
