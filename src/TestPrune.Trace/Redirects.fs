/// The input-capture weave pass: rewrites call sites of the BCL's file readers, existence
/// probes, directory listings, metadata readers and `Process.Start` into the recorder's `Io` and
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

let private ioInstance t name ps shim =
    { io t name ps shim with
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
let private Enc = "System.Text.Encoding"

[<Literal>]
let private LoadOpts = "System.Xml.Linq.LoadOptions"

[<Literal>]
let private EnumOpts = "System.IO.EnumerationOptions"

[<Literal>]
let private StreamOpts = "System.IO.FileStreamOptions"

[<Literal>]
let private FileOpts = "System.IO.FileOptions"

[<Literal>]
let private I32 = "System.Int32"

[<Literal>]
let private I64 = "System.Int64"

[<Literal>]
let private Bool = "System.Boolean"

[<Literal>]
let private F = "System.IO.File"

[<Literal>]
let private D = "System.IO.Directory"

/// `DirectoryInfo`'s directory and file-system-info listings in each overload, and every
/// `DirectoryInfo` listing that takes `EnumerationOptions`.
let private directoryInfoListings: Redirect list =
    let listing name ps =
        ioInstance "System.IO.DirectoryInfo" name ps ("DirectoryInfo_" + name)

    (List.allPairs
        [ "GetDirectories"
          "EnumerateDirectories"
          "GetFileSystemInfos"
          "EnumerateFileSystemInfos" ]
        [ []; [ S ]; [ S; Opt ] ]
     |> List.map (fun (name, ps) -> listing name ps))
    @ ([ "GetFiles"
         "EnumerateFiles"
         "GetDirectories"
         "EnumerateDirectories"
         "GetFileSystemInfos"
         "EnumerateFileSystemInfos" ]
       |> List.map (fun name -> listing name [ S; EnumOpts ]))

/// Every redirected BCL method.
let table: Redirect list =
    [ io F "Exists" [ S ] "File_Exists"
      io F "ReadAllText" [ S ] "File_ReadAllText"
      io F "ReadAllText" [ S; "System.Text.Encoding" ] "File_ReadAllText"
      io F "ReadAllLines" [ S ] "File_ReadAllLines"
      io F "ReadAllLines" [ S; Enc ] "File_ReadAllLines"
      io F "ReadAllBytes" [ S ] "File_ReadAllBytes"
      io F "ReadLines" [ S ] "File_ReadLines"
      io F "ReadLines" [ S; Enc ] "File_ReadLines"
      io F "OpenRead" [ S ] "File_OpenRead"
      io F "OpenText" [ S ] "File_OpenText"
      io F "Open" [ S; Mode ] "File_Open"
      io F "Open" [ S; Mode; Access ] "File_Open"
      io F "Open" [ S; Mode; Access; Share ] "File_Open"
      io F "ReadAllTextAsync" [ S; Ct ] "File_ReadAllTextAsync"
      io F "ReadAllLinesAsync" [ S; Ct ] "File_ReadAllLinesAsync"
      io F "ReadAllTextAsync" [ S; Enc; Ct ] "File_ReadAllTextAsync"
      io F "ReadAllLinesAsync" [ S; Enc; Ct ] "File_ReadAllLinesAsync"
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
      io D "GetFileSystemEntries" [ S ] "Directory_GetFileSystemEntries"
      io D "GetFileSystemEntries" [ S; S ] "Directory_GetFileSystemEntries"
      io D "GetFileSystemEntries" [ S; S; Opt ] "Directory_GetFileSystemEntries"
      io D "EnumerateFileSystemEntries" [ S ] "Directory_EnumerateFileSystemEntries"
      io D "EnumerateFileSystemEntries" [ S; S ] "Directory_EnumerateFileSystemEntries"
      io D "EnumerateFileSystemEntries" [ S; S; Opt ] "Directory_EnumerateFileSystemEntries"
      io "System.IO.FileStream" ".ctor" [ S; Mode ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share ] "FileStream_ctor"
      io "System.IO.StreamReader" ".ctor" [ S ] "StreamReader_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; Enc ] "StreamReader_ctor"
      io "System.Xml.Linq.XDocument" "Load" [ S ] "XDocument_Load"
      io "System.Xml.Linq.XDocument" "Load" [ S; LoadOpts ] "XDocument_Load"
      io "System.Xml.Linq.XElement" "Load" [ S ] "XElement_Load"
      io "System.Xml.Linq.XElement" "Load" [ S; LoadOpts ] "XElement_Load"
      io "System.Xml.XmlReader" "Create" [ S ] "XmlReader_Create"
      io "System.Xml.XmlReader" "Create" [ S; "System.Xml.XmlReaderSettings" ] "XmlReader_Create"
      ioInstance "System.Xml.XmlDocument" "Load" [ S ] "XmlDocument_Load"
      ioInstance "System.IO.FileSystemInfo" "get_Exists" [] "FileSystemInfo_get_Exists"
      ioInstance "System.IO.FileInfo" "get_Exists" [] "FileInfo_get_Exists"
      ioInstance "System.IO.FileInfo" "OpenRead" [] "FileInfo_OpenRead"
      ioInstance "System.IO.FileInfo" "OpenText" [] "FileInfo_OpenText"
      ioInstance "System.IO.DirectoryInfo" "get_Exists" [] "DirectoryInfo_get_Exists"
      ioInstance "System.IO.DirectoryInfo" "GetFiles" [] "DirectoryInfo_GetFiles"
      ioInstance "System.IO.DirectoryInfo" "GetFiles" [ S ] "DirectoryInfo_GetFiles"
      ioInstance "System.IO.DirectoryInfo" "GetFiles" [ S; Opt ] "DirectoryInfo_GetFiles"
      ioInstance "System.IO.DirectoryInfo" "EnumerateFiles" [] "DirectoryInfo_EnumerateFiles"
      ioInstance "System.IO.DirectoryInfo" "EnumerateFiles" [ S ] "DirectoryInfo_EnumerateFiles"
      ioInstance "System.IO.DirectoryInfo" "EnumerateFiles" [ S; Opt ] "DirectoryInfo_EnumerateFiles"
      // Listings taking `EnumerationOptions` (the `DirectoryInfo` ones are in
      // `directoryInfoListings`).
      io D "GetFiles" [ S; S; EnumOpts ] "Directory_GetFiles"
      io D "EnumerateFiles" [ S; S; EnumOpts ] "Directory_EnumerateFiles"
      io D "GetDirectories" [ S; S; EnumOpts ] "Directory_GetDirectories"
      io D "EnumerateDirectories" [ S; S; EnumOpts ] "Directory_EnumerateDirectories"
      io D "GetFileSystemEntries" [ S; S; EnumOpts ] "Directory_GetFileSystemEntries"
      io D "EnumerateFileSystemEntries" [ S; S; EnumOpts ] "Directory_EnumerateFileSystemEntries"
      // Opens by path through the remaining overloads. `RandomAccess` reads only through a
      // handle, so recording `File.OpenHandle` covers it.
      io F "OpenHandle" [ S; Mode; Access; Share; FileOpts; I64 ] "File_OpenHandle"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share; I32 ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share; I32; Bool ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; Mode; Access; Share; I32; FileOpts ] "FileStream_ctor"
      io "System.IO.FileStream" ".ctor" [ S; StreamOpts ] "FileStream_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; Bool ] "StreamReader_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; Enc; Bool ] "StreamReader_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; Enc; Bool; I32 ] "StreamReader_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; StreamOpts ] "StreamReader_ctor"
      io "System.IO.StreamReader" ".ctor" [ S; Enc; Bool; StreamOpts ] "StreamReader_ctor"
      ioInstance "System.IO.FileInfo" "Open" [ Mode ] "FileInfo_Open"
      ioInstance "System.IO.FileInfo" "Open" [ Mode; Access ] "FileInfo_Open"
      ioInstance "System.IO.FileInfo" "Open" [ Mode; Access; Share ] "FileInfo_Open"
      ioInstance "System.IO.FileInfo" "Open" [ StreamOpts ] "FileInfo_Open"
      io F "ReadLinesAsync" [ S; Ct ] "File_ReadLinesAsync"
      io F "ReadLinesAsync" [ S; Enc; Ct ] "File_ReadLinesAsync"
      // Metadata reads. The `FileSystemInfo` getters are not virtual, so a call through a
      // `FileInfo` or `DirectoryInfo` is spelled against `FileSystemInfo`.
      io F "GetLastWriteTime" [ S ] "File_GetLastWriteTime"
      io F "GetLastWriteTimeUtc" [ S ] "File_GetLastWriteTimeUtc"
      io F "GetCreationTime" [ S ] "File_GetCreationTime"
      io F "GetCreationTimeUtc" [ S ] "File_GetCreationTimeUtc"
      io F "GetLastAccessTime" [ S ] "File_GetLastAccessTime"
      io F "GetLastAccessTimeUtc" [ S ] "File_GetLastAccessTimeUtc"
      io F "GetAttributes" [ S ] "File_GetAttributes"
      io F "GetUnixFileMode" [ S ] "File_GetUnixFileMode"
      io D "GetLastWriteTime" [ S ] "Directory_GetLastWriteTime"
      io D "GetLastWriteTimeUtc" [ S ] "Directory_GetLastWriteTimeUtc"
      io D "GetCreationTime" [ S ] "Directory_GetCreationTime"
      io D "GetCreationTimeUtc" [ S ] "Directory_GetCreationTimeUtc"
      ioInstance "System.IO.FileSystemInfo" "get_LastWriteTime" [] "FileSystemInfo_get_LastWriteTime"
      ioInstance "System.IO.FileSystemInfo" "get_LastWriteTimeUtc" [] "FileSystemInfo_get_LastWriteTimeUtc"
      ioInstance "System.IO.FileSystemInfo" "get_CreationTime" [] "FileSystemInfo_get_CreationTime"
      ioInstance "System.IO.FileSystemInfo" "get_CreationTimeUtc" [] "FileSystemInfo_get_CreationTimeUtc"
      ioInstance "System.IO.FileSystemInfo" "get_Attributes" [] "FileSystemInfo_get_Attributes"
      ioInstance "System.IO.FileSystemInfo" "get_UnixFileMode" [] "FileSystemInfo_get_UnixFileMode"
      ioInstance "System.IO.FileInfo" "get_Length" [] "FileInfo_get_Length"
      ioInstance "System.IO.FileInfo" "get_IsReadOnly" [] "FileInfo_get_IsReadOnly"
      proc [ "System.Diagnostics.ProcessStartInfo" ]
      proc [ S ]
      proc [ S; S ]
      proc [ S; "System.Collections.Generic.IEnumerable`1<System.String>" ]
      { proc [] with IsInstance = true } ]
    @ directoryInfoListings

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
