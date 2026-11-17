/// Where a test may touch disk (AUTOMATION-1775): under a root it alone owns.
///
/// Three flakes in one week had one shape: a test touched a place on disk that another
/// test, or the build, was changing at the same time. A repo-wide audit walk raced
/// TestPrune.Trace.Tests' scratch directories inside the repository (AUTOMATION-1671), a
/// leftover daemon rebuilt bin/ under the next run's tests (AUTOMATION-1746), and leftover
/// build output of a deleted project was counted as a test project (AUTOMATION-1717).
///
/// This file is the ONE place a test resolves a shared location, and FSHW-ROOT-001
/// (analyzers/FsHotWatch.TestRoots) keeps it that way:
///
/// - `TestRoot`: scratch space. A fresh, uniquely named directory under this process's
///   root, removed when it is disposed. Paths under it are taken only as RELATIVE parts:
///   a rooted part or a `..` segment is refused, so a test cannot name a shared path by
///   way of its own root.
/// - `RepositoryTree`: the real repository, for READING named files and this repo's
///   build output. Nothing here writes to it.
/// - `TestBinary`: files beside the running test assembly, for reading.
///
/// Dependency-free on purpose: it is compiled into every test assembly, TestPrune's
/// included, which do not reference FsHotWatch.
module FsHotWatch.TestRoots

open System
open System.IO

/// Parse the parts of a path that must stay under `root`: each is relative and none
/// climbs. Refused at the boundary rather than checked after joining, so a refusal names
/// the offending part.
let private under (root: string) (parts: string array) : string =
    for part in parts do
        if String.IsNullOrEmpty part then
            invalidArg "parts" $"an empty path part under %s{root}"

        if Path.IsPathRooted part then
            invalidArg "parts" $"%s{part} is rooted: a path under %s{root} must be relative"

        if part.Split([| '/'; '\\' |]) |> Array.contains ".." then
            invalidArg "parts" $"%s{part} climbs out of %s{root} with '..'"

    Path.Combine(Array.append [| root |] parts)

/// `path` with every symlinked component resolved (`/var/folders/…` reads
/// `/private/var/folders/…` on macOS), the form the OS reports as a working directory.
/// Components that do not exist yet are kept as written.
let private physical (path: string) : string =
    let full = Path.GetFullPath path
    let start = Path.GetPathRoot full

    full.Substring(start.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
    |> Array.fold
        (fun (resolved: string) part ->
            let next = Path.Combine(resolved, part)

            if Directory.Exists next then
                match Directory.ResolveLinkTarget(next, true) with
                | null -> next
                | target -> Path.GetFullPath target.FullName
            else
                next)
        start

/// The repository this test assembly was built in: the first ancestor of the test
/// binary holding `FsHotWatch.slnx`. Read once.
let private repositoryRoot =
    lazy
        (let rec up (directory: DirectoryInfo) =
            if isNull directory then
                failwith "FsHotWatch.slnx not found above the test binary"
            elif File.Exists(Path.Combine(directory.FullName, "FsHotWatch.slnx")) then
                directory.FullName
            else
                up directory.Parent

         // FSHW-ROOT-001 ok: the one place a test finds the repository it was built in.
         up (DirectoryInfo AppContext.BaseDirectory))

/// This process's temp directory, as it was when first read. TMPDIR is never changed
/// mid-process: .NET caches the directory a named pipe lives in on first use.
let systemTemp =
    // FSHW-ROOT-001 ok: the one place a test reads the shared temp directory; every
    // scratch path is made under this process's own root inside it.
    lazy (Path.GetFullPath(Path.GetTempPath()))

/// A directory this process owns under the system temp directory: made on first use,
/// prepared once, and removed when the process exits.
let private processOwned (name: string) (prepare: string -> unit) =
    lazy
        (let root = Path.Combine(systemTemp.Value, name) |> Path.GetFullPath

         Directory.CreateDirectory root |> ignore
         prepare root

         AppDomain.CurrentDomain.ProcessExit.Add(fun _ ->
             try
                 Directory.Delete(root, true)
             with
             | :? IOException
             | :? UnauthorizedAccessException -> ())

         root)

/// This process's root: the parent of every `TestRoot` this process makes, owned by the
/// process and removed when it exits. It holds this repository's `global.json`, so a
/// `dotnet` run in any scratch directory resolves the SDK this repository builds with
/// rather than the machine's newest (AUTOMATION-1621). Short on purpose: a Unix socket
/// path under it is capped at 104 bytes.
let processRoot =
    processOwned $"fshw-sdk-%d{Environment.ProcessId}" (fun root ->
        File.Copy(Path.Combine(repositoryRoot.Value, "global.json"), Path.Combine(root, "global.json"), true))

/// The parent of the few roots that must have NO global.json above them, for a test about
/// how `dotnet` resolves the machine's newest SDK. Also owned by the process.
let private machineSdkRoot =
    processOwned $"fshw-nosdk-%d{Environment.ProcessId}" ignore

/// Which SDK a `dotnet` run inside a root resolves.
type Sdk =
    /// This repository's, from the `global.json` in `processRoot`. Every test but one kind.
    | RepositorySdk
    /// The machine's newest: no global.json above the root. Only for a test about that case.
    | MachineSdk

/// Remove a scratch tree, best effort: whatever is left is removed with the process root.
let private removeBestEffort (path: string) =
    try
        if Directory.Exists path then
            Directory.Delete(path, true)
    with
    | :? IOException
    | :? UnauthorizedAccessException -> ()

/// A directory this test alone owns: fresh, uniquely named, under a process-owned parent,
/// and removed when disposed.
[<Sealed>]
type TestRoot private (path: string, remove: string -> unit) =

    /// A fresh root named `<prefix>-<guid>` whose `dotnet` resolves `sdk`, removed on
    /// dispose by `remove`.
    static member CreateIn (sdk: Sdk) (remove: string -> unit) (prefix: string) : TestRoot =
        let parent =
            match sdk with
            | RepositorySdk -> processRoot.Value
            | MachineSdk -> machineSdkRoot.Value

        let path =
            Path.Combine(parent, prefix + "-" + Guid.NewGuid().ToString "N")
            |> Path.GetFullPath

        Directory.CreateDirectory path |> ignore
        new TestRoot(path, remove)

    /// `CreateIn RepositorySdk`.
    static member CreateWith (remove: string -> unit) (prefix: string) : TestRoot =
        TestRoot.CreateIn RepositorySdk remove prefix

    /// The root's absolute path.
    member _.Path: string = path

    /// The root's path with every symlink resolved, for a consumer that keys files by the
    /// exact string the OS reports (FCS's TransparentCompiler, `getcwd`).
    member _.Physical: string = physical path

    /// A path under this root. Each part is relative and none climbs out with `..`.
    member _.Under([<ParamArray>] parts: string array) : string = under path parts

    interface IDisposable with
        member _.Dispose() = remove path

[<RequireQualifiedAccess>]
module TestRoot =

    /// A fresh root this test alone owns, removed best effort on dispose.
    let create (prefix: string) : TestRoot =
        TestRoot.CreateWith removeBestEffort prefix

    /// Run `body` with a fresh root, and remove the root afterwards.
    let withRoot (prefix: string) (body: TestRoot -> 'a) : 'a =
        use root = create prefix
        body root

/// The real repository, read only.
[<RequireQualifiedAccess>]
module RepositoryTree =

    /// A named path in the repository: a tracked file, a directory to list, or this
    /// repository's build output. `relative` may hold separators (`src/FsHotWatch`), and does not climb out.
    let path (relative: string) : string =
        under repositoryRoot.Value [| relative |]

    /// The repository root itself, for a test that hands it to the product as a READ-ONLY
    /// input (a project to evaluate, a config to parse). Never a place to write.
    let root () : string = repositoryRoot.Value

/// Files beside the running test assembly (its build output), read only.
[<RequireQualifiedAccess>]
module TestBinary =

    /// The running test assembly's own directory.
    let directory () : string =
        // FSHW-ROOT-001 ok: the one place a test reads its own output directory.
        AppContext.BaseDirectory

    /// A file beside the running test assembly. `relative` does not climb out.
    let path (relative: string) : string = under (directory ()) [| relative |]
