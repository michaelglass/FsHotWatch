module FsHotWatch.Tests.TestHelpers

open System
open System.IO
open System.Threading

/// Poll until condition is true or timeout (default 50ms poll interval).
let waitUntil (condition: unit -> bool) (timeoutMs: int) =
    let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)

    while not (condition ()) && DateTime.UtcNow < deadline do
        Thread.Sleep(50)

/// Create a temp directory with the given prefix, run the body, then clean up.
/// Returns the result of the body function.
let withTempDir (prefix: string) (body: string -> 'a) =
    let tmpDir = Path.Combine(Path.GetTempPath(), $"fshw-{prefix}-{Guid.NewGuid():N}")
    Directory.CreateDirectory(tmpDir) |> ignore

    try
        body tmpDir
    finally
        if Directory.Exists(tmpDir) then
            Directory.Delete(tmpDir, true)
