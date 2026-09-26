module TestPrune.Trace.Tests.SiteProbeScenarioTests

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.Loader
open Xunit
open Swensen.Unquote
open Mono.Cecil
open Mono.Cecil.Cil
open TestPrune.Trace
open TestPrune.Trace.Model
open TestPrune.Trace.Recorder
open TestPrune.Trace.Tests

/// What a probe id stands for, in manifest terms.
type Key =
    | Case of union: string * case: string
    | Ty of string
    | Meth of typ: string * mem: string

let private keyOf (row: ManifestRow) =
    match row.Kind with
    | UnionCase -> Case(row.TypeName, row.Member)
    | TypeUse -> Ty row.TypeName
    | _ -> Meth(row.TypeName, row.Member)

/// One weave of FxLib (full) and FxDriver (sites-only) with the site probes, and one run of
/// the woven driver.
type private Woven =
    {
        Result: Weaver.WeaveResult
        /// Scope key -> the manifest keys it recorded.
        Recorded: Map<string, Set<Key>>
        /// Woven assembly name -> its bytes (the scratch dir is gone once this is built).
        Assemblies: Map<string, byte[]>
    }

let private woven =
    lazy
        (let dir, r = WeaverTests.weaveFx [ SiteProbes.pass () ]

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

             if code <> 0 then
                 failwith $"woven driver failed (invalid IL?):\n%s{output}"

             let dumps, rejected = DumpReader.readDirectory out

             if not rejected.IsEmpty then
                 failwith $"rejected dumps: %A{rejected}"

             { Result = r
               Recorded =
                 dumps
                 |> List.collect (fun d -> d.Scopes)
                 |> List.map (fun s -> s.Key, s.Ids |> Array.map (fun id -> keyOf r.Manifest.Rows.[id]) |> Set.ofArray)
                 |> Map.ofList
               Assemblies =
                 [ for name in [ "FxLib"; "FxDriver" ] -> name, File.ReadAllBytes(Path.Combine(dir, name + ".dll")) ]
                 |> Map.ofList }
         finally
             WeaverTests.deleteScratch dir)

let private shape c = Case("FxLib.Shape", c)
let private color c = Case("FxLib.Color5", c)
let private dir c = Case("FxLib.Dir", c)
let private logic m = Meth("FxLib.Logic", m)
let private values m = Meth("FxLib.Values", m)

/// Scenario -> (must record, must not record). The spike's fixture table in manifest keys.
/// `isCaseProp` is left out on purpose: a Debug `x.IsCircle` on a `Square` records `Circle`,
/// a sound over-approximation this table must not freeze either way.
let private expectations =
    Map.ofList
        [ "caseA_area", ([ shape "Circle"; logic "area" ], [ shape "Square" ])
          "caseB_isCircleOnly", ([ logic "isCircleOnly" ], [ shape "Circle"; shape "Square" ])
          "static_case_match", ([ values "get_defaultShape"; shape "Square"; logic "area" ], [ shape "Circle" ])
          "tag5", ([ color "Blue" ], [ color "Red"; color "Green"; color "Cyan"; color "Magenta" ])
          "tag5_static", ([ color "Blue"; values "get_prebuiltBlue" ], [ color "Red"; color "Magenta" ])
          "nullary", ([ dir "East"; dir "South" ], [ dir "North"; dir "West" ])
          "struct", ([ Case("FxLib.SResult", "SErr") ], [ Case("FxLib.SResult", "SOk") ])
          "tricky_tag",
          ([ Case("FxLib.Tricky", "Tag"); logic "tricky" ],
           [ Case("FxLib.Tricky", "Other")
             Case("FxLib.Tricky", "Third")
             Case("FxLib.Tricky", "Fourth") ])
          "modval", ([ values "get_threshold" ], [ values "get_unusedValue"; values "computeThreshold" ])
          "sameFileValue", ([ values "thresholdPlus"; values "get_threshold" ], [])
          "typetest_dog", ([ Ty "FxLib.Dog" ], [ Ty "FxLib.Cat" ])
          "typetest_cat", ([ Ty "FxLib.Cat" ], [ Ty "FxLib.Dog" ])
          "unboxgeneric", ([ Ty "FxLib.Dog" ], [ Ty "FxLib.Cat" ])
          "record", ([ Ty "FxLib.Point"; logic "sumPoint" ], [])
          "testcode_match", ([ shape "Circle" ], [ shape "Square" ])
          "testcode_typetest", ([], [ Ty "FxLib.Dog" ])
          "constValue", ([ logic "addConst"; values "get_constValue" ], [])
          "literal", ([ logic "addLiteral" ], [])
          "activePattern", ([ logic "|Big|Small|" ], [])
          "unionEquality", ([ shape "Circle" ], [ shape "Square" ]) ]

/// Scenario names only: xUnit enumerates serializable rows as separate test cases.
let scenarios: TheoryData<string> = TheoryData<string>(expectations |> Map.keys)

[<Theory>]
[<MemberData(nameof scenarios)>]
let ``each scenario records what it executed and nothing it did not`` (name: string) =
    let must, mustNot = expectations.[name]
    let got = woven.Value.Recorded.["T:" + name]
    must |> List.filter (fun k -> not (got.Contains k)) =! []
    mustNot |> List.filter got.Contains =! []

[<Fact>]
let ``static init lands in its own scope, not in the test that triggered it`` () =
    // Values' initializer computes `threshold` and builds `defaultShape = Square 3.0` and
    // `prebuiltBlue = Blue 3`. Building a case is its constructor's entry probe (the joiner
    // maps `NewX` to case X); no site probe reads a case the initializer only builds.
    let s = woven.Value.Recorded.["S:static-init"]
    test <@ s.Contains(values "computeThreshold") @>
    test <@ s.Contains(Meth("FxLib.Shape", "NewSquare")) @>
    test <@ s.Contains(Meth("FxLib.Color5", "NewBlue")) @>
    test <@ not (woven.Value.Recorded.["T:warm"].Contains(values "computeThreshold")) @>

[<Fact>]
let ``union-case rows are contiguous per union in tag order`` () =
    let rows = woven.Value.Result.Manifest.Rows

    let cases union =
        rows
        |> Array.filter (fun r -> r.Kind = UnionCase && r.TypeName = union)
        |> Array.map (fun r -> r.Id, r.Member)

    let contiguous (xs: (int * string)[]) =
        xs |> Array.mapi (fun i (id, _) -> id - fst xs.[0] = i) |> Array.forall id

    let tricky = cases "FxLib.Tricky"
    test <@ Array.map snd tricky = [| "Tag"; "Other"; "Third"; "Fourth" |] @>
    test <@ contiguous tricky @>
    let color5 = cases "FxLib.Color5"
    test <@ Array.map snd color5 = [| "Red"; "Green"; "Blue"; "Cyan"; "Magenta" |] @>
    test <@ contiguous color5 @>
    test <@ Array.map snd (cases "FxLib.SResult") = [| "SOk"; "SErr" |] @>

    test
        <@
            rows
            |> Array.filter (fun r -> r.Kind = UnionCase || r.Kind = TypeUse)
            |> Array.forall (fun r -> r.Assembly = "FxLib" && r.Document = None && r.FirstLine = 0)
        @>

[<Fact>]
let ``type rows cover product types only`` () =
    let types =
        woven.Value.Result.Manifest.Rows
        |> Array.filter (fun r -> r.Kind = TypeUse)
        |> Array.map (fun r -> r.TypeName, r.Member)
        |> Set.ofArray

    // Records and classes, nested with '+'; never unions, modules, interfaces, closures,
    // case classes, Tags holders or debug proxies.
    test <@ Set.isSubset (set [ "FxLib.Point", ""; "FxLib.Dog", ""; "FxLib.Cat", ""; "FxLib.Greeter", "" ]) types @>

    let names = types |> Set.map fst

    test
        <@
            names
            |> Set.filter (fun n ->
                n = "FxLib.Shape"
                || n = "FxLib.Logic"
                || n = "FxLib.IGreeter"
                || n.Contains '@'
                || n.Contains "Tags"
                || n.StartsWith "FxLib.Shape+"
                || n.StartsWith "<")
            |> Set.isEmpty
        @>

/// The instructions of one woven method, as text.
let private ilOf (assembly: string) (typeName: string) (pick: MethodDefinition -> bool) =
    use m =
        ModuleDefinition.ReadModule(new MemoryStream(woven.Value.Assemblies.[assembly]))

    let t = m.GetType typeName
    let meth = t.Methods |> Seq.find pick
    meth.Body.Instructions |> Seq.map string |> Seq.toList

let private callsHitTag (il: string list) =
    il |> List.exists (fun i -> i.Contains "Probes::HitTag")

[<Fact>]
let ``a case named Tag keeps its static getter unprobed`` () =
    // The case's singleton getter returns a Tricky, not an int: a tag probe there is invalid IL.
    let il = ilOf "FxLib" "FxLib.Tricky" (fun m -> m.Name = "get_Tag" && m.IsStatic)

    test <@ not (callsHitTag il) @>

[<Fact>]
let ``the instance tag getter records its value's case at every return`` () =
    for union in [ "FxLib.Tricky"; "FxLib.Color5"; "FxLib.Shape"; "FxLib.Dir"; "FxLib.SResult" ] do
        let il = ilOf "FxLib" union (fun m -> m.Name = "get_Tag" && not m.IsStatic)
        let rets = il |> List.filter (fun i -> i.EndsWith ": ret") |> List.length
        let tags = il |> List.filter (fun i -> i.Contains "Probes::HitTag") |> List.length
        test <@ (union, rets > 0 && tags = rets) = (union, true) @>

[<Fact>]
let ``a case field named tag is read without a tag probe`` () =
    let il = ilOf "FxLib" "FxLib.Logic" (fun m -> m.Name = "tricky")
    let at = il |> List.findIndex (fun i -> i.Contains "FxLib.Tricky/Other::_tag")

    test
        <@
            not (
                il.[at + 1].Contains "HitTag"
                || il.[at + 2].Contains "HitTag"
                || il.[at + 3].Contains "HitTag"
            )
        @>

[<Fact>]
let ``type initializers run inside a finally that leaves the static-init scope`` () =
    let il =
        ilOf "FxLib" "<StartupCode$FxLib>.$FxLib.Values" (fun m -> m.IsConstructor && m.IsStatic)

    test <@ il |> List.exists (fun i -> i.Contains "Probes::EnterStatic") @>
    test <@ il |> List.exists (fun i -> i.Contains "Probes::ExitStatic") @>
    test <@ il |> List.exists (fun i -> i.EndsWith ": endfinally") @>
    test <@ not (il |> List.exists (fun i -> i.Contains ": ret" && i <> List.last il)) @>

/// Loads the woven assemblies from memory; everything else (FSharp.Core, the recorder)
/// resolves to the copies this test process already has.
type private WovenContext(assemblies: Map<string, byte[]>) =
    inherit AssemblyLoadContext("woven-site-probes", true)

    member val Loaded = Collections.Generic.Dictionary<string, Assembly>()

    override this.Load(name: AssemblyName) =
        match assemblies.TryFind name.Name with
        | Some bytes ->
            match this.Loaded.TryGetValue name.Name with
            | true, a -> a
            | _ ->
                let a = this.LoadFromStream(new MemoryStream(bytes))
                this.Loaded.[name.Name] <- a
                a
        | None -> null

[<Fact>]
let ``every touched non-generic method JIT-prepares`` () =
    let w = woven.Value
    let ctx = WovenContext(w.Assemblies)

    try
        let failures = Collections.Generic.List<string>()
        let mutable prepared = 0

        let flags =
            BindingFlags.DeclaredOnly
            ||| BindingFlags.Public
            ||| BindingFlags.NonPublic
            ||| BindingFlags.Static
            ||| BindingFlags.Instance

        for KeyValue(asmName, keys) in w.Result.Stats.Touched do
            let asm = ctx.LoadFromAssemblyName(AssemblyName asmName)

            for key in keys do
                let sep = key.IndexOf "::"
                let t = asm.GetType(key.Substring(0, sep), true)
                let name = key.Substring(sep + 2)

                let methods: MethodBase list =
                    [ yield! t.GetMethods flags |> Seq.cast<MethodBase>
                      yield! t.GetConstructors flags |> Seq.cast<MethodBase> ]
                    |> List.filter (fun m -> m.Name = name)

                for m in methods do
                    if not (t.ContainsGenericParameters || m.ContainsGenericParameters) then
                        try
                            RuntimeHelpers.PrepareMethod m.MethodHandle
                            prepared <- prepared + 1
                        with ex ->
                            failures.Add $"%s{key}: %s{ex.GetType().Name} %s{ex.Message}"

        List.ofSeq failures =! []
        // The tag getters, the Tag-named case getter's union and the cctors are all in the set.
        test <@ w.Result.Stats.Touched.["FxLib"] |> List.contains "FxLib.Tricky::get_Tag" @>
        test <@ w.Result.Stats.Touched.["FxLib"] |> List.contains "FxLib.Logic::tricky" @>
        // The driver's scenario closures match on FxLib's unions and types.
        test
            <@
                w.Result.Stats.Touched.["FxDriver"]
                |> List.exists (fun k -> k.StartsWith "FxDriver.Program+main@")
            @>

        test <@ prepared > 100 @>
    finally
        ctx.Unload()
