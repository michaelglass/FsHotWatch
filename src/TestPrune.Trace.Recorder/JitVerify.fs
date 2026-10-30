/// JIT verification of woven IL, run inside the test app's own runtime (its shared
/// frameworks included) by the `StartupHook`. A weaver bug that emits invalid IL is caught
/// here, before the weave is accepted, instead of as a test that fails for no visible reason.
module TestPrune.Trace.Recorder.JitVerify

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Text.Json

/// What verification found.
type Report =
    {
        /// Methods the JIT compiled.
        Prepared: int
        /// "<Type>::<method>" of every method the JIT rejected, each once.
        Invalid: string list
        /// Methods skipped because they (or their type) are open generics.
        SkippedGeneric: int
        /// Lines naming something that could not be loaded or prepared for another reason.
        Other: int
    }

let private flags =
    BindingFlags.Public
    ||| BindingFlags.NonPublic
    ||| BindingFlags.Static
    ||| BindingFlags.Instance
    ||| BindingFlags.DeclaredOnly

let private candidates (t: Type) (methodName: string) : MethodBase list =
    match methodName with
    | ".ctor"
    | ".cctor" ->
        t.GetConstructors flags
        |> Seq.filter (fun c -> c.IsStatic = (methodName = ".cctor"))
        |> Seq.cast<MethodBase>
        |> List.ofSeq
    | _ ->
        t.GetMethods flags
        |> Seq.filter (fun m -> m.Name = methodName)
        |> Seq.cast<MethodBase>
        |> List.ofSeq

type private Outcome =
    | Prepared
    | Invalid
    | Generic
    | OtherFailure

let private prepareOne (t: Type) (m: MethodBase) =
    if t.ContainsGenericParameters || m.ContainsGenericParameters then
        Generic
    else
        // Anything but the JIT rejecting the IL (an abstract method, a body whose types do
        // not load) is not a weaver bug: count it, never let it escape the hook.
        try
            if m.IsAbstract || isNull (m.GetMethodBody()) then
                OtherFailure
            else
                RuntimeHelpers.PrepareMethod m.MethodHandle
                Prepared
        with
        | :? InvalidProgramException -> Invalid
        | _ -> OtherFailure

/// JIT-prepare every method named by `lines` ("<assembly>\t<Type>::<method>", one per
/// line; every overload of the name), loading each assembly through `load`.
let prepareListed (load: string -> Assembly) (lines: string seq) : Report =
    let outcomes =
        [ for line in lines do
              match line.Split '\t' with
              | [| asmName; key |] when key.Contains "::" ->
                  let sep = key.LastIndexOf "::"
                  let typeName, methodName = key.Substring(0, sep), key.Substring(sep + 2)

                  let found =
                      try
                          let t = (load asmName).GetType(typeName, true)
                          Ok(t, candidates t methodName)
                      with _ ->
                          Error()

                  match found with
                  | Ok(t, (_ :: _ as ms)) ->
                      for m in ms do
                          yield key, prepareOne t m
                  | _ -> yield key, OtherFailure
              | _ -> yield line, OtherFailure ]

    let count o =
        outcomes |> List.filter (fun (_, x) -> x = o) |> List.length

    { Prepared = count Prepared
      Invalid =
        outcomes
        |> List.filter (fun (_, x) -> x = Invalid)
        |> List.map fst
        |> List.distinct
      SkippedGeneric = count Generic
      Other = count OtherFailure }

/// Write `report` to `path` as JSON: {"prepared","invalid","skippedGeneric","other"}.
let write (path: string) (report: Report) =
    using (File.Create path) (fun stream ->
        using (new Utf8JsonWriter(stream)) (fun w ->
            w.WriteStartObject()
            w.WriteNumber("prepared", report.Prepared)
            w.WriteStartArray "invalid"

            for m in report.Invalid do
                w.WriteStringValue m

            w.WriteEndArray()
            w.WriteNumber("skippedGeneric", report.SkippedGeneric)
            w.WriteNumber("other", report.Other)
            w.WriteEndObject()))

/// Verify mode, given the environment: inert (false) unless `TESTPRUNE_TRACE_VERIFY`
/// names a report path; then prepare the methods listed in the file named by
/// `TESTPRUNE_TRACE_VERIFY_ASSEMBLIES`, write the report and return true.
let runFromEnvironment (getEnv: string -> string) (load: string -> Assembly) : bool =
    match getEnv Contract.VerifyEnv with
    | null
    | "" -> false
    | reportPath ->
        let listed = File.ReadAllLines(getEnv Contract.VerifyAssembliesEnv)
        write reportPath (prepareListed load listed)
        true
