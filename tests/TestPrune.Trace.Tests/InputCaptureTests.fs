/// File-read and child-process capture. The shim tests swap the process-wide recorder, so
/// they run in the non-parallel counters collection.
[<Xunit.Collection("recorder-counters")>]
module TestPrune.Trace.Tests.InputCaptureTests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text
open System.Threading
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

let private shimType name =
    typeof<Probes>.Assembly.GetType(name, true)

let private bclType (name: string) =
    [ "System.Private.CoreLib"; "System.Diagnostics.Process" ]
    |> List.pick (fun a -> Type.GetType(name + ", " + a, false) |> Option.ofObj)

/// The runtime type Cecil spells `name`; a generic instance such as
/// `IEnumerable`1<System.String>` is written `IEnumerable`1[System.String]` by reflection.
let private reflectionType (name: string) =
    bclType (name.Replace('<', '[').Replace('>', ']'))

let private shimFor (r: Redirects.Redirect) =
    let ps = r.Params |> List.map reflectionType |> Array.ofList

    let shimParams =
        if r.IsInstance then
            Array.append [| bclType r.DeclaringType |] ps
        else
            ps

    (shimType r.ShimType).GetMethod(r.Shim, BindingFlags.Public ||| BindingFlags.Static, null, shimParams, null)

let private recorderWithRoot (root: string) =
    let state = RecorderState(8, None, null)
    state.RepoRoot <- root
    state

let private inputsOf (scope: Scope) =
    scope.Inputs.Keys |> Seq.map (fun (struct (k, p)) -> k, p) |> Set.ofSeq

let private withRecorder (state: RecorderState) (f: unit -> unit) =
    let saved = Runtime.state
    Runtime.state <- state

    try
        f ()
    finally
        Runtime.state <- saved

[<Fact>]
let ``every redirect names a shim with the original's stack shape`` () =
    for r in Redirects.table do
        let bcl = bclType r.DeclaringType
        let ps = r.Params |> List.map reflectionType |> Array.ofList
        let shim = shimFor r
        test <@ not (isNull shim) @>

        let expectedReturn =
            if r.Name = ".ctor" then
                bcl
            else
                let flags =
                    (if r.IsInstance then
                         BindingFlags.Instance
                     else
                         BindingFlags.Static)
                    ||| BindingFlags.Public

                bcl.GetMethod(r.Name, flags, null, ps, null).ReturnType

        test <@ shim.ReturnType = expectedReturn @>

[<Fact>]
let ``reads under the repository are noted on the current scope; reads outside are not`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:t"
    let inside = Path.Combine(Fixtures.repoRoot, "global.json")
    Io.NoteWith(state, "read", inside)
    Io.NoteWith(state, "read", "/etc/hosts")
    // A sibling whose name merely starts with the root's name is outside it.
    Io.NoteWith(state, "read", Fixtures.repoRoot + "-sibling/x.json")
    Io.NoteWith(state, "read", null)
    Io.NoteWith(state, "read", "bad\000path")
    Io.NoteWith(state, "list", Fixtures.repoRoot)
    test <@ inputsOf (state.CurrentScope()) = set [ "read", inside; "list", Fixtures.repoRoot ] @>

[<Fact>]
let ``nothing is noted without a recorder or a repository root`` () =
    Io.NoteWith(null, "read", "/x")
    let state = RecorderState(8, None, null)
    Io.NoteWith(state, "read", Path.Combine(Fixtures.repoRoot, "global.json"))
    test <@ state.Scopes |> Seq.forall (fun s -> s.Inputs.IsEmpty) @>

[<Fact>]
let ``a repository root with a trailing separator still matches`` () =
    let state =
        recorderWithRoot (Fixtures.repoRoot + string Path.DirectorySeparatorChar)

    state.EnterScope "T:t"
    let inside = Path.Combine(Fixtures.repoRoot, "global.json")
    Io.NoteWith(state, "read", inside)
    test <@ inputsOf (state.CurrentScope()) = set [ "read", inside ] @>

[<Fact>]
let ``inputs go to static init, then the current scope, then ambient`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    let file = Path.Combine(Fixtures.repoRoot, "global.json")
    Io.NoteWith(state, "read", file)
    state.EnterStatic()
    Io.NoteWith(state, "exists", file)
    state.ExitStatic()

    let byKey key =
        state.Scopes |> Seq.find (fun s -> s.Key = key)

    test <@ inputsOf (byKey "A:ambient") = set [ "read", file ] @>
    test <@ inputsOf (byKey "S:static-init") = set [ "exists", file ] @>

[<Fact>]
let ``every file shim notes its input and does what the original does`` () =
    let root = Fixtures.repoRoot
    let file = Path.Combine(root, "global.json")
    let text = File.ReadAllText file
    let size = FileInfo(file).Length
    let state = recorderWithRoot root
    state.EnterScope "T:io"
    let ct = CancellationToken.None

    let scratch =
        Path.Combine(root, "tests", "TraceFixtures", "bin-scratch", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory scratch |> ignore
    let written = Path.Combine(scratch, "w.txt")

    try
        withRecorder state (fun () ->
            test <@ Io.File_Exists file @>
            test <@ Io.File_ReadAllText file = text @>
            test <@ Io.File_ReadAllText(file, Encoding.UTF8) = text @>
            test <@ Io.File_ReadAllLines file = File.ReadAllLines file @>
            test <@ Io.File_ReadAllBytes file = File.ReadAllBytes file @>
            test <@ List.ofSeq (Io.File_ReadLines file) = List.ofArray (File.ReadAllLines file) @>

            // One at a time, touching nothing else: File.Open(path, mode) shares nothing.
            for opener in
                [ fun () -> Io.File_OpenRead file
                  fun () -> Io.File_Open(file, FileMode.Open)
                  fun () -> Io.File_Open(file, FileMode.Open, FileAccess.Read)
                  fun () -> Io.File_Open(file, FileMode.Open, FileAccess.Read, FileShare.Read)
                  fun () -> Io.FileStream_ctor(file, FileMode.Open)
                  fun () -> Io.FileStream_ctor(file, FileMode.Open, FileAccess.Read)
                  fun () -> Io.FileStream_ctor(file, FileMode.Open, FileAccess.Read, FileShare.Read)
                  fun () -> Io.FileInfo_OpenRead(FileInfo file) ] do
                use s = opener ()
                test <@ s.Length = size @>

            for opener in
                [ fun () -> Io.File_OpenText file
                  fun () -> Io.StreamReader_ctor file
                  fun () -> Io.FileInfo_OpenText(FileInfo file) ] do
                use r = opener ()
                test <@ r.ReadToEnd() = text @>

            test <@ Io.File_ReadAllTextAsync(file, ct).Result = text @>
            test <@ Io.File_ReadAllLinesAsync(file, ct).Result = File.ReadAllLines file @>
            test <@ Io.File_ReadAllBytesAsync(file, ct).Result = File.ReadAllBytes file @>
            test <@ Io.Directory_Exists root @>
            test <@ Io.FileInfo_get_Exists(FileInfo file) @>
            test <@ Io.DirectoryInfo_get_Exists(DirectoryInfo root) @>
            test <@ Io.FileSystemInfo_get_Exists(FileInfo file) @>

            let all = SearchOption.TopDirectoryOnly
            test <@ Io.Directory_GetFiles root = Directory.GetFiles root @>
            test <@ Io.Directory_GetFiles(root, "*.json") = Directory.GetFiles(root, "*.json") @>
            test <@ Io.Directory_GetFiles(root, "*.json", all) = Directory.GetFiles(root, "*.json", all) @>
            test <@ List.ofSeq (Io.Directory_EnumerateFiles root) = List.ofSeq (Directory.EnumerateFiles root) @>

            test
                <@
                    List.ofSeq (Io.Directory_EnumerateFiles(root, "*.json")) = List.ofSeq (
                        Directory.EnumerateFiles(root, "*.json")
                    )
                @>

            test
                <@
                    List.ofSeq (Io.Directory_EnumerateFiles(root, "*.json", all)) = List.ofSeq (
                        Directory.EnumerateFiles(root, "*.json", all)
                    )
                @>

            test <@ Io.Directory_GetDirectories root = Directory.GetDirectories root @>
            test <@ Io.Directory_GetDirectories(root, "s*") = Directory.GetDirectories(root, "s*") @>
            test <@ Io.Directory_GetDirectories(root, "s*", all) = Directory.GetDirectories(root, "s*", all) @>

            test
                <@
                    List.ofSeq (Io.Directory_EnumerateDirectories root) = List.ofSeq (
                        Directory.EnumerateDirectories root
                    )
                @>

            test
                <@
                    List.ofSeq (Io.Directory_EnumerateDirectories(root, "s*")) = List.ofSeq (
                        Directory.EnumerateDirectories(root, "s*")
                    )
                @>

            test
                <@
                    List.ofSeq (Io.Directory_EnumerateDirectories(root, "s*", all)) = List.ofSeq (
                        Directory.EnumerateDirectories(root, "s*", all)
                    )
                @>

            // A stream opened for writing is noted too: over-approximating is sound.
            do
                use w = Io.FileStream_ctor(written, FileMode.Create)
                w.WriteByte 1uy)
    finally
        Directory.Delete(scratch, true)

    test
        <@
            inputsOf (state.CurrentScope()) = set
                [ "read", file; "exists", file; "exists", root; "list", root; "read", written ]
        @>

[<Fact>]
let ``file shims are inert with no recorder`` () =
    let file = Path.Combine(Fixtures.repoRoot, "global.json")
    withRecorder null (fun () -> test <@ Io.File_Exists file @>)

[<Fact>]
let ``a started child inherits the parent scope and is noted with its pid`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:parent"

    let psi =
        ProcessStartInfo("/bin/echo", "hi", UseShellExecute = false, RedirectStandardOutput = true)

    let injected = ProcessShims.PrepareWith(state, psi)
    test <@ injected && psi.Environment.[Contract.ParentScopeEnv] = "T:parent" @>
    use p = Process.Start psi
    ProcessShims.RecordWith(state, p, injected)
    p.WaitForExit()
    let struct (pid, file, env) = state.CurrentScope().Children |> Seq.exactlyOne
    test <@ pid = p.Id && file = "echo" && env @>

[<Fact>]
let ``a shell-executed child cannot inherit and is noted as such`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:parent"
    test <@ not (ProcessShims.PrepareWith(state, ProcessStartInfo("/bin/echo", UseShellExecute = true))) @>

[<Fact>]
let ``with no recorder a child is neither prepared nor noted`` () =
    let psi = ProcessStartInfo("/bin/echo")
    test <@ not (ProcessShims.PrepareWith(null, psi)) @>
    test <@ not (psi.Environment.ContainsKey Contract.ParentScopeEnv) @>
    ProcessShims.RecordWith(null, null, false)
    let state = RecorderState(8, None, null)
    ProcessShims.RecordWith(state, null, false)
    test <@ state.Scopes |> Seq.forall (fun s -> s.Children.IsEmpty) @>

[<Fact>]
let ``a process that did not start is not noted`` () =
    let state = RecorderState(8, None, null)
    use p = new Process(StartInfo = ProcessStartInfo "/bin/echo")
    test <@ not (ProcessShims.StartWith(state, p, (fun _ -> false))) @>
    test <@ state.Scopes |> Seq.forall (fun s -> s.Children.IsEmpty) @>

[<Fact>]
let ``every Process.Start shim starts the child in the current scope`` () =
    let state = recorderWithRoot Fixtures.repoRoot
    state.EnterScope "T:spawn"
    let started = ResizeArray<Process>()

    withRecorder state (fun () ->
        started.Add(ProcessShims.Process_Start(ProcessStartInfo("/bin/echo", "a", RedirectStandardOutput = true)))
        started.Add(ProcessShims.Process_Start "/usr/bin/true")
        started.Add(ProcessShims.Process_Start("/usr/bin/true", "a"))
        started.Add(ProcessShims.Process_Start("/usr/bin/true", [ "a"; "b" ] :> seq<string>))
        let p = new Process(StartInfo = ProcessStartInfo "/usr/bin/true")
        test <@ ProcessShims.Process_Start p @>
        started.Add p)

    for p in started do
        p.WaitForExit()
        p.Dispose()

    let children =
        state.CurrentScope().Children
        |> Seq.map (fun (struct (_, file, env)) -> file, env)
        |> List.ofSeq

    test <@ children = [ "echo", true; "true", true; "true", true; "true", true; "true", true ] @>

/// A throwaway module with one method whose body calls every shape the pass must handle.
let private syntheticModule () =
    let m =
        ModuleDefinition.CreateModule("Synthetic", ModuleParameters(Kind = ModuleKind.Dll))

    let t =
        TypeDefinition("Syn", "C", TypeAttributes.Public ||| TypeAttributes.Class, m.TypeSystem.Object)

    m.Types.Add t

    let meth =
        MethodDefinition("M", MethodAttributes.Public ||| MethodAttributes.Static, m.TypeSystem.Void)

    t.Methods.Add meth
    let il = meth.Body.GetILProcessor()
    let imp (mi: MethodBase) = m.ImportReference mi
    let readAll = typeof<File>.GetMethod("ReadAllText", [| typeof<string> |])
    il.Emit(OpCodes.Ldstr, "a")
    il.Emit(OpCodes.Call, imp readAll)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ldstr, "b")
    il.Emit(OpCodes.Call, imp readAll)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ldstr, "c")
    il.Emit(OpCodes.Newobj, imp (typeof<FileInfo>.GetConstructor [| typeof<string> |]))
    il.Emit(OpCodes.Callvirt, imp (typeof<FileSystemInfo>.GetMethod "get_Exists"))
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ldstr, "d")
    il.Emit(OpCodes.Ldc_I4_3)
    il.Emit(OpCodes.Newobj, imp (typeof<FileStream>.GetConstructor [| typeof<string>; typeof<FileMode> |]))
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ldftn, imp readAll)
    il.Emit(OpCodes.Pop)
    il.Emit(OpCodes.Ret)
    m, meth

let private recorderSet (m: ModuleDefinition) : Weaver.WeaveSet =
    let recorder = ModuleDefinition.ReadModule(typeof<Probes>.Assembly.Location)

    { Modules = [ m, Full ]
      Alloc = fun _ -> 0
      RecorderMethod =
        fun target typeName name ->
            target.ImportReference(recorder.GetType(typeName).Methods |> Seq.find (fun x -> x.Name = name)) }

[<Fact>]
let ``the pass redirects calls, virtual calls and constructors, and nothing else`` () =
    let m, meth = syntheticModule ()
    let set = recorderSet m
    let pass = Redirects.pass ()
    pass.Prepare set
    test <@ pass.Rewrite(set, m, Full, meth) @>

    let calls =
        meth.Body.Instructions
        |> Seq.choose (fun i ->
            match i.Operand with
            | :? MethodReference as r -> Some(i.OpCode.Name, r.DeclaringType.FullName + "::" + r.Name)
            | _ -> None)
        |> List.ofSeq

    test
        <@
            calls = [ "call", "TestPrune.Trace.Recorder.Io::File_ReadAllText"
                      "call", "TestPrune.Trace.Recorder.Io::File_ReadAllText"
                      "newobj", "System.IO.FileInfo::.ctor"
                      "call", "TestPrune.Trace.Recorder.Io::FileSystemInfo_get_Exists"
                      "call", "TestPrune.Trace.Recorder.Io::FileStream_ctor"
                      // A method pointer is left alone: a shim's delegate would bind differently.
                      "ldftn", "System.IO.File::ReadAllText" ]
        @>

    // Nothing left to redirect the second time round.
    test <@ not (pass.Rewrite(set, m, Full, meth)) @>

[<Fact>]
let ``the woven driver's file read lands in its scope`` () =
    let dir, r = WeaverTests.weaveFx [ Redirects.pass () ]

    try
        let out = Path.Combine(dir, "traces")

        let code, output =
            Launch.run
                (Path.Combine(dir, "FxDriver"))
                []
                [ Contract.OutEnv, out
                  Contract.IdsEnv, string r.Manifest.IdCount
                  Contract.RepoRootEnv, Fixtures.repoRoot ]
                dir
                (TimeSpan.FromMinutes 1.0)

        test <@ code = 0 @>
        ignore output
        let dumps, _ = DumpReader.readDirectory out

        let scope =
            dumps
            |> List.collect (fun d -> d.Scopes)
            |> List.find (fun s -> s.Key = "T:fileRead")

        test
            <@
                scope.Inputs
                |> List.exists (fun i -> i.Kind = FileRead && i.Path = Path.Combine(Fixtures.repoRoot, "global.json"))
            @>
    finally
        WeaverTests.deleteScratch dir
