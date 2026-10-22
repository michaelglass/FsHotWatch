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
module internal FsHotWatch.Analyzers.AnalyzerLoad

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
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
