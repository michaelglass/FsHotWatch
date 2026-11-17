/// Loads each configured analyzer path into its own Client, in COLLECTIBLE load
/// contexts, so one path can be reloaded and its old assemblies really unloaded
/// without touching the others.
///
/// Why not the SDK's `Client.LoadAnalyzers` (measured against FSharp.Analyzers.SDK
/// 0.39.2): it loads every `*Analyzer*.dll` through McMaster.NETCore.Plugins'
/// `PluginLoader` with `LoadInMemory` and `PreferSharedTypes`, a NEW load context per
/// DLL per call, and never sets `IsUnloadable`. So a fresh Client does get fresh
/// statics for the analyzer assembly and its private dependencies, but the context it
/// replaced can never be unloaded: every reload leaked a full copy of the reloaded
/// assemblies for the life of the daemon. Dependencies the host carries itself
/// (FSharp.Core, FCS, the SDK) resolve from the default context and are shared under
/// both loaders; that sharing is what keeps `CliContext` one type for host and
/// analyzer, and it means a static in one of THOSE is never reset by any reload.
///
/// The discovery rules are the SDK's own: the same file glob and filters, the same
/// SDK-version gate, and the SDK's internal `analyzersFromType`, reached by
/// reflection, to find the analyzer members. Only the load context differs.
module FsHotWatch.Analyzers.AnalyzerLoad

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Reflection.PortableExecutable
open System.Runtime.Loader
open System.Text.RegularExpressions
open FSharp.Analyzers.SDK
open McMaster.NETCore.Plugins

type internal Registered = Client.RegisteredAnalyzer<CliContext>

/// One configured analyzer path, loaded.
type internal LoadedPath =
    {
        Path: string
        /// Holds exactly this path's analyzers.
        Client: Client<CliAnalyzerAttribute, CliContext>
        /// Analyzers registered from this path.
        Count: int
        /// File names (without extension) of the DLLs that registered an analyzer:
        /// what decides which configuration files this path reads.
        Assemblies: string list
        /// One collectible loader per registered DLL; disposing one unloads it.
        Loaders: PluginLoader list
    }

/// The DLLs the SDK would open under `dir`: `*Analyzer*.dll` in it and every
/// real (not symlinked) subdirectory, minus the SDK itself and any file name matching `.*test.*\.dll$`.
let internal analyzerDlls (dir: string) : string list =
    let testName = Regex(".*test.*\\.dll$")

    // Unlike the SDK, never through a symlinked directory: SafeWalk is the repository's
    // one recursive walk (FSHW-WALK-001).
    FsHotWatch.SafeWalk.bestEffortFilePaths Set.empty "*Analyzer*.dll" dir
    |> Seq.toArray
    |> Array.filter (fun dll ->
        let name = Path.GetFileName dll

        not (name.EndsWith("fsharp.analyzers.sdk.dll", StringComparison.OrdinalIgnoreCase))
        && not (testName.IsMatch name))
    |> Array.sort
    |> Array.toList

/// The SDK's own member discovery, `Client.analyzersFromType`, and the Client's
/// private registry. Both are internal to the SDK; a version that renames either must
/// fail here, loudly, rather than load nothing.
let private sdkInternals =
    lazy
        let sdk = typeof<CliContext>.Assembly

        let fromType =
            sdk.GetType("FSharp.Analyzers.SDK.Client")
            |> Option.ofObj
            |> Option.bind (fun t ->
                t.GetMethod("analyzersFromType", BindingFlags.NonPublic ||| BindingFlags.Static)
                |> Option.ofObj)
            |> Option.map (fun m -> m.MakeGenericMethod(typeof<CliAnalyzerAttribute>, typeof<CliContext>))

        let registry =
            typeof<Client<CliAnalyzerAttribute, CliContext>>
                .GetField("registeredAnalyzers", BindingFlags.NonPublic ||| BindingFlags.Instance)
            |> Option.ofObj

        match fromType, registry with
        | Some fromType, Some registry -> fromType, registry
        | _ ->
            failwith
                "FSharp.Analyzers.SDK no longer has Client.analyzersFromType / Client.registeredAnalyzers — the analyzer loader must follow the SDK"

let private analyzersFromType (dll: string) (t: Type) : Registered list =
    let fromType, _ = sdkInternals.Value
    fromType.Invoke(null, [| box dll; box t |]) :?> Registered list

let private registryOf (client: Client<CliAnalyzerAttribute, CliContext>) =
    let _, registry = sdkInternals.Value
    registry.GetValue client :?> ConcurrentDictionary<string, Registered list>

/// The SDK skips an assembly built against another minor version of itself.
let private builtForThisSdk (assembly: Assembly) : bool =
    let sdk = Utils.currentFSharpAnalyzersSDKVersion

    assembly.GetReferencedAssemblies()
    |> Array.exists (fun reference ->
        reference.Name = "FSharp.Analyzers.SDK"
        && reference.Version.Major = sdk.Major
        && reference.Version.Minor = sdk.Minor)

/// Load `dll` into a fresh collectible context, or None when it is not a loadable
/// assembly (the SDK skips those the same way).
let private loadCollectible (dll: string) : (PluginLoader * Assembly) option =
    let loader =
        PluginLoader.CreateFromAssemblyFile(
            dll,
            Action<PluginConfig>(fun config ->
                config.DefaultContext <- AssemblyLoadContext.Default
                config.PreferSharedTypes <- true
                config.LoadInMemory <- true
                config.IsUnloadable <- true)
        )

    try
        Some(loader, loader.LoadDefaultAssembly())
    with :? BadImageFormatException ->
        loader.Dispose()
        None

/// Load every analyzer under `path` into a new Client, skipping analyzers whose name
/// `exclude` matches. A path that does not exist loads nothing.
let internal loadPath (exclude: string -> bool) (path: string) : LoadedPath =
    let client = Client<CliAnalyzerAttribute, CliContext>()

    let loaded =
        if Directory.Exists path then
            analyzerDlls path
            |> List.choose (fun dll ->
                loadCollectible dll
                |> Option.bind (fun (loader, assembly) ->
                    let analyzers =
                        if builtForThisSdk assembly then
                            assembly.GetExportedTypes()
                            |> Seq.collect (analyzersFromType dll)
                            |> Seq.filter (fun analyzer -> not (exclude analyzer.Name))
                            |> Seq.toList
                        else
                            []

                    if List.isEmpty analyzers then
                        // Nothing in it will run, so nothing may keep it loaded.
                        loader.Dispose()
                        None
                    else
                        (registryOf client).[dll] <- analyzers
                        Some(dll, loader, List.length analyzers)))
        else
            []

    { Path = path
      Client = client
      Count = loaded |> List.sumBy (fun (_, _, count) -> count)
      Assemblies = loaded |> List.map (fun (dll, _, _) -> Path.GetFileNameWithoutExtension dll)
      Loaders = loaded |> List.map (fun (_, loader, _) -> loader) }

/// Start unloading `loaded`'s contexts. The runtime reclaims them once nothing
/// references their code: a Client still running (a timed-out analyzer ignoring its
/// token) keeps its context alive until it returns, never the reverse.
let internal unload (loaded: LoadedPath) : unit =
    loaded.Loaders |> List.iter (fun loader -> loader.Dispose())

// ---- The FSharp.Compiler.Service band (AUTOMATION-1706) ----------------------------
//
// An analyzer reads the typed tree through FCS's own types, and the host shares ITS FCS
// with every analyzer (see the module comment). An analyzer compiled against another FCS
// API therefore binds members that may not exist in the host's: FCS 43.13 renamed
// `recordFields` to `recordFieldsAndSpreads`, and analyzers built against 43.12 then
// threw MissingMethodException on every file they analysed, while producing nothing.
// Such a set is refused before anything in it is loaded.

[<Literal>]
let private FcsAssemblyName = "FSharp.Compiler.Service"

/// An assembly compiled against an FSharp.Compiler.Service outside the host's band.
type FcsMismatch =
    {
        /// The analyzer DLL's path, or for the analyzer SDK, its assembly name.
        Assembly: string
        /// The FCS AssemblyVersion its metadata references.
        Built: Version
        /// The FCS AssemblyVersion this process runs.
        Host: Version
    }

/// The analyzer paths, loaded, or refused whole because an assembly they depend on
/// was compiled against another FCS band than the host's.
[<RequireQualifiedAccess>]
type internal AnalyzerSet =
    | Loaded of LoadedPath list
    | RefusedFcsMismatch of FcsMismatch * FcsMismatch list

/// The FCS this process runs, as its AssemblyVersion carries it. FCS stamps the full
/// release there (package 43.12.402 is AssemblyVersion 43.12.402.0), so the version
/// says more than the band the rule compares.
let internal hostFcs: Version =
    typeof<FSharp.Compiler.Text.Range>.Assembly.GetName().Version

/// Whether an assembly compiled against FCS `built` may run under FCS `host`: the same
/// major.minor. A patch release keeps its band's API (FSharp.Analyzers.SDK 0.39.2
/// references 43.12.400 and runs under 43.12.402); a minor release is where FCS
/// renames what analyzers read.
let internal sameFcsBand (host: Version) (built: Version) : bool =
    host.Major = built.Major && host.Minor = built.Minor

/// The assemblies among `builtAgainst` (name, FCS it references) outside `host`'s band.
let internal fcsMismatches (host: Version) (builtAgainst: (string * Version) list) : FcsMismatch list =
    builtAgainst
    |> List.filter (fun (_, built) -> not (sameFcsBand host built))
    |> List.map (fun (assembly, built) ->
        { Assembly = assembly
          Built = built
          Host = host })

/// The FCS `dll` was compiled against, read from its metadata without loading it, or
/// None when it references none or is not a managed assembly (the loader skips those).
let internal fcsReferenceOf (dll: string) : Version option =
    try
        use stream = File.OpenRead dll
        use pe = new PEReader(stream)
        let reader = Metadata.PEReaderExtensions.GetMetadataReader pe

        reader.AssemblyReferences
        |> Seq.map reader.GetAssemblyReference
        |> Seq.tryFind (fun reference -> reader.GetString reference.Name = FcsAssemblyName)
        |> Option.map _.Version
    with
    | :? BadImageFormatException
    | :? InvalidOperationException -> None

/// The FCS the analyzer SDK this host runs was compiled against: every analyzer runs
/// through it, so it is held to the same band.
let private sdkFcsReference: (string * Version) list =
    let sdk = typeof<CliContext>.Assembly.GetName()

    typeof<CliContext>.Assembly.GetReferencedAssemblies()
    |> Array.filter (fun reference -> reference.Name = FcsAssemblyName)
    |> Array.map (fun reference -> sdk.Name, reference.Version)
    |> Array.toList

/// What every DLL the loader would open under `paths` references of FCS.
let private builtAgainstUnder (paths: string list) : (string * Version) list =
    paths
    |> List.filter Directory.Exists
    |> List.collect analyzerDlls
    |> List.choose (fun dll -> fcsReferenceOf dll |> Option.map (fun built -> dll, built))

/// The SDK's and `paths`' assemblies outside `host`'s FCS band, as a refusal, or None.
let private refusalUnder (host: Version) (paths: string list) : AnalyzerSet option =
    match fcsMismatches host (sdkFcsReference @ builtAgainstUnder paths) with
    | [] -> None
    | first :: rest -> Some(AnalyzerSet.RefusedFcsMismatch(first, rest))

/// Load every path, or refuse the set whole, before loading any of it, when the SDK or
/// a DLL under the paths was compiled against another FCS band than `host`.
let internal loadSet (host: Version) (exclude: string -> bool) (paths: string list) : AnalyzerSet =
    refusalUnder host paths
    |> Option.defaultWith (fun () -> AnalyzerSet.Loaded(paths |> List.map (loadPath exclude)))

/// The paths `set` holds loaded: none when it was refused.
let internal loadedPaths (set: AnalyzerSet) : LoadedPath list =
    match set with
    | AnalyzerSet.Loaded paths -> paths
    | AnalyzerSet.RefusedFcsMismatch _ -> []

/// What reloading part of a set did.
type internal Reload =
    {
        /// The set to use from now on.
        Set: AnalyzerSet
        /// The paths it replaced, for the caller to `unload`.
        Retired: LoadedPath list
        /// How many analyzers the reloaded paths now hold.
        Reloaded: int
    }

/// Load the paths of `set` that `stale` selects again, each into a fresh Client, keeping
/// the others. A refused set has nothing loaded to reload. A reload is a load, so it is
/// held to the band too: a stale path whose DLLs left it refuses the whole set.
let internal reloadWhere
    (host: Version)
    (exclude: string -> bool)
    (stale: LoadedPath -> bool)
    (set: AnalyzerSet)
    : Reload =
    match set with
    | AnalyzerSet.RefusedFcsMismatch _ ->
        { Set = set
          Retired = []
          Reloaded = 0 }
    | AnalyzerSet.Loaded paths ->
        let retired = paths |> List.filter stale

        match refusalUnder host (retired |> List.map _.Path) with
        | Some refused ->
            { Set = refused
              Retired = paths
              Reloaded = 0 }
        | None ->
            let next =
                paths
                |> List.map (fun path -> if stale path then loadPath exclude path.Path else path)

            { Set = AnalyzerSet.Loaded next
              Retired = retired
              Reloaded =
                List.zip paths next
                |> List.sumBy (fun (before, after) -> if stale before then after.Count else 0) }

/// The one message a refused set reports: each refused assembly with the FCS it was
/// built against and the FCS this host runs, then what to do about it.
let describeFcsRefusal (first: FcsMismatch) (rest: FcsMismatch list) : string =
    let named =
        first :: rest
        |> List.map (fun mismatch ->
            $"%s{mismatch.Assembly} was compiled against FSharp.Compiler.Service %O{mismatch.Built}, but this fshw runs %O{mismatch.Host}")
        |> String.concat "; "

    $"Analyzer set refused, no analyzer ran: %s{named}. An analyzer runs only under the FSharp.Compiler.Service major.minor it was built against: rebuild the analyzers against FSharp.Compiler.Service %d{first.Host.Major}.%d{first.Host.Minor}, or run an fshw built on theirs."
