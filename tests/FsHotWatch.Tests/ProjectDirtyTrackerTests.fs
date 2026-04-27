module FsHotWatch.Tests.ProjectDirtyTrackerTests

open Xunit
open Swensen.Unquote
open FsHotWatch.ProjectDirtyTracker

[<Fact(Timeout = 5000)>]
let ``newly created tracker has nothing dirty`` () =
    let t = ProjectDirtyTracker()
    test <@ t.AllDirty = [] @>

[<Fact(Timeout = 5000)>]
let ``MarkDirty makes IsDirty return true`` () =
    let t = ProjectDirtyTracker()
    t.MarkDirty [ "FsHotWatch" ]
    test <@ t.IsDirty "FsHotWatch" @>

[<Fact(Timeout = 5000)>]
let ``ClearDirty makes IsDirty return false`` () =
    let t = ProjectDirtyTracker()
    t.MarkDirty [ "FsHotWatch"; "FsHotWatch.Tests" ]
    t.ClearDirty "FsHotWatch"
    test <@ not (t.IsDirty "FsHotWatch") @>
    test <@ t.IsDirty "FsHotWatch.Tests" @>

[<Fact(Timeout = 5000)>]
let ``AllDirty returns only dirty projects`` () =
    let t = ProjectDirtyTracker()
    t.MarkDirty [ "A"; "B"; "C" ]
    t.ClearDirty "B"
    let all = t.AllDirty |> List.sort
    test <@ all = [ "A"; "C" ] @>

[<Fact(Timeout = 5000)>]
let ``MarkDirty is idempotent`` () =
    let t = ProjectDirtyTracker()
    t.MarkDirty [ "FsHotWatch" ]
    t.MarkDirty [ "FsHotWatch" ]
    test <@ t.AllDirty.Length = 1 @>

[<Fact(Timeout = 5000)>]
let ``ClearDirty on non-dirty project is safe`` () =
    let t = ProjectDirtyTracker()
    t.ClearDirty "NotDirty"
    test <@ t.AllDirty = [] @>
