module FsHotWatch.Tests.PoolProbeTests

open System
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.PoolProbe

let private at (seconds: float) (threads: int) (pending: int64) (completed: int64) : Sample =
    { At = DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero).AddSeconds seconds
      Threads = threads
      Pending = pending
      Completed = completed }

[<Fact>]
let ``an empty queue in either sample is not reported`` () =
    test <@ report (at 0.0 8 0L 100L) (at 5.0 8 0L 200L) = None @>
    test <@ report (at 0.0 8 3L 100L) (at 5.0 8 0L 200L) = None @>
    test <@ report (at 0.0 8 0L 100L) (at 5.0 8 3L 200L) = None @>

[<Fact>]
let ``a queue that stays waiting across two samples is reported with what the pool did in between`` () =
    let line = report (at 0.0 8 12L 1000L) (at 5.0 9 40L 1003L)

    test <@ line.IsSome @>
    let text = line.Value
    test <@ text.Contains "pool-probe" @>
    test <@ text.Contains "threads=9" @>
    test <@ text.Contains "pending=40" @>
    // Three work items completed in five seconds while 40 waited: what starvation looks like.
    test <@ text.Contains "completed+3/5s" @>
    test <@ text.Contains "2026-11-17T00:00:05" @>
