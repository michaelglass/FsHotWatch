/// The input-capture weave pass: rewrites call sites of the BCL's file readers, existence
/// probes, directory listings and `Process.Start` into the recorder's `Io` and
/// `ProcessShims` shims, which have the same stack shape. A call site is used rather than
/// an interposer on `open`, because managed file reads reach the OS through
/// `open$NOCANCEL`, which an interposer on plain `open` never sees.
module TestPrune.Trace.Redirects

open System.Collections.Generic
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace.Recorder
open TestPrune.Trace.Weaver

/// One BCL method whose call sites are redirected to a recorder shim.
type Redirect =
    {
        /// CLR full name of the BCL type.
        DeclaringType: string
        /// Method name; ".ctor" for a constructor.
        Name: string
        /// Parameter type full names, as Cecil spells them.
        Params: string list
        /// An instance method: the shim takes the instance first.
        IsInstance: bool
        /// `Contract.IoType` or `Contract.ProcessShimsType`.
        ShimType: string
        /// The shim's method name on `ShimType`.
        Shim: string
    }

let private io t name ps shim =
    { DeclaringType = t
      Name = name
      Params = ps
      IsInstance = false
      ShimType = Contract.IoType
      Shim = shim }

let private ioInstance t name shim =
    { io t name [] shim with
        IsInstance = true }

let private proc ps =
    { io "System.Diagnostics.Process" "Start" ps "Process_Start" with
        ShimType = Contract.ProcessShimsType }

[<Literal>]
let private S = "System.String"

[<Literal>]
let private Mode = "System.IO.FileMode"

[<Literal>]
let private Access = "System.IO.FileAccess"

[<Literal>]
let private Share = "System.IO.FileShare"

[<Literal>]
let private Opt = "System.IO.SearchOption"

[<Literal>]
let private Ct = "System.Threading.CancellationToken"

[<Literal>]
let private F = "System.IO.File"

[<Literal>]
let private D = "System.IO.Directory"

/// Every redirected BCL method.
let table: Redirect list =
    [ io F "Exists" [ S ] "File_Exists"
      io F "ReadAllText" [ S ] "File_ReadAllText"
      io F "ReadAllText" [ S; "System.Text.Encoding" ] "File_ReadAllText"
      io F "ReadAllLines" [ S ] "File_ReadAllLines"
      io F "ReadAllBytes" [ S ] "File_ReadAllBytes"
      io F "ReadLines" [ S ] "File_ReadLines"
      io F "OpenRead" [ S ] "File_OpenRead"
      io F "OpenText" [ S ] "File_OpenText"
      io F "Open" [ S; Mode ] "File_Open"
      io F "Open" [ S; Mode; Access ] "File_Open"
      io F "Open" [ S; Mode; Access; Share ] "File_Open"
      io F "ReadAllTextAsync" [ S; Ct ] "File_ReadAllTextAsync"
      io F "ReadAllLinesAsync" [ S; Ct ] "File_ReadAllLinesAsync"
      io F "ReadAllBytesAsync" [ S; Ct ] "File_ReadAllBytesAsync"
      io D "Exists" [ S ] "Directory_Exists"
      io D "GetFiles" [ S ] "Directory_GetFiles"
      io D "GetFiles" [ S; S ] "Directory_GetFiles"
      io D "GetFiles" [ S; S; Opt ] "Directory_GetFiles"
      io D "EnumerateFiles" [ S ] "Directory_EnumerateFiles"
      io D "EnumerateFiles" [ S; S ] "Directory_EnumerateFiles"
      io D "EnumerateFiles" [ S; S; Opt ] "Directory_EnumerateFiles"
      io D "GetDirectories" [ S ] "Directory_GetDirectories"
      io D "GetDirectories" [ S; S ] "Directory_GetDirectories"
      io D "GetDirectories" [ S; S; Opt ] "Directory_GetDirectories"
      io D "EnumerateDirectories" [ S ] "Directory_EnumerateDirectories"
      io D "EnumerateDirectories" [ S; S ] "Directory_EnumerateDirectories"
      io D "EnumerateDirectories" [ S; S; Opt ] "Directory_EnumerateDirectories"
      io "System.IO.FileStream" ".ctor" [ S; Mode ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share ] "FileStream_ctor"
      io "System.IO.StreamReader" ".ctor" [ S ] "StreamReader_ctor"
      ioInstance "System.IO.FileSystemInfo" "get_Exists" "FileSystemInfo_get_Exists"
      ioInstance "System.IO.FileInfo" "get_Exists" "FileInfo_get_Exists"
      ioInstance "System.IO.FileInfo" "OpenRead" "FileInfo_OpenRead"
      ioInstance "System.IO.FileInfo" "OpenText" "FileInfo_OpenText"
      ioInstance "System.IO.DirectoryInfo" "get_Exists" "DirectoryInfo_get_Exists"
      proc [ "System.Diagnostics.ProcessStartInfo" ]
      proc [ S ]
      proc [ S; S ]
      proc [ S; "System.Collections.Generic.IEnumerable`1<System.String>" ]
      { proc [] with IsInstance = true } ]

let private signature (t: string) (name: string) (ps: string seq) =
    t + "::" + name + "(" + String.concat "," ps + ")"

let private byKey =
    table
    |> List.map (fun r -> signature r.DeclaringType r.Name r.Params, r)
    |> dict

let private redirectable (op: OpCode) =
    op = OpCodes.Call || op = OpCodes.Callvirt || op = OpCodes.Newobj

type private Pass() =
    /// Shim definitions by redirect signature, resolved once per weave.
    let shims = Dictionary<string, MethodDefinition>()

    let shimFor (set: WeaveSet) (m: ModuleDefinition) (sigKey: string) (r: Redirect) =
        match shims.TryGetValue sigKey with
        | true, d -> d
        | _ ->
            let shimParams = (if r.IsInstance then [ r.DeclaringType ] else []) @ r.Params
            // RecorderMethod finds a method by name only; pick the overload by its parameters.
            let recorder = (set.RecorderMethod m r.ShimType r.Shim).Resolve().DeclaringType

            let d =
                recorder.Methods
                |> Seq.find (fun x ->
                    x.Name = r.Shim
                    && (x.Parameters |> Seq.map (fun p -> p.ParameterType.FullName) |> List.ofSeq) = shimParams)

            shims.[sigKey] <- d
            d

    interface IWeavePass with
        member _.Prepare(_) = shims.Clear()

        member _.Rewrite(set, m, _, meth) =
            let mutable changed = false

            for ins in meth.Body.Instructions do
                match ins.Operand with
                | :? MethodReference as mr when redirectable ins.OpCode ->
                    let sigKey =
                        signature
                            mr.DeclaringType.FullName
                            mr.Name
                            (mr.Parameters |> Seq.map (fun p -> p.ParameterType.FullName))

                    match byKey.TryGetValue sigKey with
                    | true, r ->
                        ins.OpCode <- OpCodes.Call
                        ins.Operand <- m.ImportReference(shimFor set m sigKey r)
                        changed <- true
                    | _ -> ()
                | _ -> ()

            changed

/// A fresh redirect pass for one weave.
let pass () : IWeavePass = Pass() :> IWeavePass
