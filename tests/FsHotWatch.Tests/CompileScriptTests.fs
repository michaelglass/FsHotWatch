/// `mise run compile` runs scripts/compile.sh: the solution build with the published reference
/// assemblies settled around it, as the build plugin settles them (AUTOMATION-1700).
///
/// MSBuild's `CopyRefAssembly` skips copying `refint/X.dll` to `ref/X.dll` when the MVID did not
/// move. When a type moves between assemblies with the API unchanged, the F# signature data in
/// `refint` names the new defining assembly while `ref` still names the old one, and every
/// dependent fails with FS0193 until the copy is made. The daemon's build plugin makes it
/// (`RefAssemblyFreshness`); a raw `dotnet build`, which `land-check` runs first, did not.
///
/// Each test fakes the build command, so it pins the script's contract, not MSBuild's.
module FsHotWatch.Tests.CompileScriptTests

open System.Diagnostics
open System.IO
open Xunit
open Swensen.Unquote
open FsHotWatch.Tests.RepoTasks
open FsHotWatch.Tests.TestHelpers

/// Run the script in `dir` with `build` as the build command; the exit code and its output.
let private compile (dir: string) (build: string) =
    let start = ProcessStartInfo("bash")
    start.WorkingDirectory <- dir
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.ArgumentList.Add(Path.Combine(repoRoot (), "scripts", "compile.sh"))
    start.ArgumentList.Add "bash"
    start.ArgumentList.Add "-c"
    start.ArgumentList.Add build
    use child = Process.Start start
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

let private objOf (dir: string) =
    Path.Combine(dir, "src", "P", "obj", "Debug", "net10.0")

let private intermediate (dir: string) =
    Path.Combine(objOf dir, "refint", "P.dll")

let private published (dir: string) = Path.Combine(objOf dir, "ref", "P.dll")

/// A project whose compiler output holds `refint` and whose dependents compile against `ref`.
let private project (dir: string) (refint: string) (ref: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(intermediate dir)) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName(published dir)) |> ignore
    File.WriteAllText(intermediate dir, refint)
    File.WriteAllText(published dir, ref)

let private builds (dir: string) =
    let log = Path.Combine(dir, "builds")

    if File.Exists log then
        File.ReadAllLines log |> Array.length
    else
        0

/// A fake build that counts itself; on its first run it rewrites `refint` (the compiler wrote a
/// new signature, CopyRefAssembly skipped the copy) and fails as the dependents do, FS0193.
/// Any later run passes when `ref` now holds what `refint` does, as a recompile would.
let private moveTypeBuild =
    "echo run >> builds; "
    + "if [ $(wc -l < builds) -eq 1 ]; then printf new-ccu > src/P/obj/Debug/net10.0/refint/P.dll; "
    + "echo 'error FS0193'; exit 1; fi; "
    + "cmp -s src/P/obj/Debug/net10.0/refint/P.dll src/P/obj/Debug/net10.0/ref/P.dll"

[<Fact>]
let ``a reference assembly the build left stale is copied, and the build runs again green`` () =
    withTempDir "compile-ref-moved" (fun dir ->
        project dir "old-ccu" "old-ccu"

        let code, output = compile dir moveTypeBuild

        test <@ code = 0 @>
        test <@ builds dir = 2 @>
        test <@ File.ReadAllText(published dir) = "new-ccu" @>
        test <@ output.Contains "src/P/obj/Debug/net10.0/ref/P.dll" @>)

[<Fact>]
let ``a stale reference assembly left by an earlier build is copied before the build`` () =
    withTempDir "compile-ref-earlier" (fun dir ->
        project dir "new-ccu" "old-ccu"

        let code, _ =
            compile
                dir
                "echo run >> builds; cmp -s src/P/obj/Debug/net10.0/refint/P.dll src/P/obj/Debug/net10.0/ref/P.dll"

        test <@ code = 0 @>
        test <@ builds dir = 1 @>
        test <@ File.ReadAllText(published dir) = "new-ccu" @>)

[<Fact>]
let ``with nothing stale the build runs once and its exit code is the script's`` () =
    withTempDir "compile-ref-fresh" (fun dir ->
        project dir "same" "same"

        let code, _ =
            compile dir "echo run >> builds; echo 'error FS0001: a real type error'; exit 1"

        test <@ code = 1 @>
        test <@ builds dir = 1 @>)

[<Fact>]
let ``a build that never settles fails after its rounds instead of looping`` () =
    withTempDir "compile-ref-unsettled" (fun dir ->
        project dir "a" "a"

        let code, output =
            compile dir "echo run >> builds; date +%s%N >> src/P/obj/Debug/net10.0/refint/P.dll; exit 0"

        test <@ code <> 0 @>
        test <@ builds dir = 3 @>
        test <@ output.Contains "still stale" @>)

/// `mise run compile` is the script, so `land-check` and `ci` get the repair through it.
[<Fact>]
let ``the compile task runs the script`` () =
    let compileTask = commandLines (miseToml (repoRoot ())) "compile"
    test <@ compileTask.Contains "scripts/compile.sh" @>
