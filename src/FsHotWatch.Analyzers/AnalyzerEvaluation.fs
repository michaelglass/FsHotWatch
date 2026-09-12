/// Verify source membership with the producer SDK, outside the daemon process.
module internal FsHotWatch.Analyzers.AnalyzerEvaluation

open System
open System.IO
open System.Xml.Linq
open System.Text
open System.Text.Json
open System.Security.AccessControl
open System.Security.Principal
open System.Collections.Generic
open FsHotWatch.ProcessHelper

exception private EvaluationRefused of string
let private refuse reason = raise (EvaluationRefused reason)

let private name value = XName.Get value
let private required key (node: XElement) =
    match node.Attribute(name key) with
    | null -> refuse $"Missing analyzer evaluation {key}"
    | value -> value.Value

let private section key (node: XElement) =
    match node.Elements(name key) |> Seq.toList with
    | [ value ] -> value
    | _ -> refuse $"Invalid analyzer evaluation {key}"

let private storeRoot () =
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fshw", "analyzer-contexts")

let private verifyPrivate (path: string) directory =
    if File.GetAttributes(path).HasFlag FileAttributes.ReparsePoint then
        refuse "Analyzer evaluation context cannot use a link"

    if OperatingSystem.IsWindows() then
        use identity = WindowsIdentity.GetCurrent()
        let owner = identity.User
        let security: FileSystemSecurity =
            if directory then FileSystemAclExtensions.GetAccessControl(DirectoryInfo(path)) :> FileSystemSecurity
            else FileSystemAclExtensions.GetAccessControl(FileInfo(path)) :> FileSystemSecurity

        if security.GetOwner(typeof<SecurityIdentifier>) <> owner then
            refuse "Analyzer evaluation context has another owner"
        if directory && not security.AreAccessRulesProtected then
            refuse "Analyzer evaluation context inherits access permissions"

        let allowed =
            security.GetAccessRules(true, true, typeof<SecurityIdentifier>)
            |> Seq.cast<FileSystemAccessRule>
            |> Seq.filter (fun rule -> rule.AccessControlType = AccessControlType.Allow)
            |> Seq.toList

        if List.isEmpty allowed || allowed |> List.exists (fun rule -> rule.IdentityReference <> owner) then
            refuse "Analyzer evaluation context is accessible by another identity"
    else
        let expected =
            if directory then UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            else UnixFileMode.UserRead ||| UnixFileMode.UserWrite
        if File.GetUnixFileMode path <> expected then
            refuse "Analyzer evaluation context permissions must be owner-only"

let private quote (value: string) =
    let result = StringBuilder("\"")
    let mutable slashes = 0
    for character in value do
        if character = '\\' then slashes <- slashes + 1
        else
            result.Append('\\', if character = '"' then slashes * 2 + 1 else slashes) |> ignore
            result.Append(character) |> ignore
            slashes <- 0
    result.Append('\\', slashes * 2).Append('"').ToString()

let private effectiveNames =
    [ "MSBuildVersion"; "NETCoreSdkVersion"; "Configuration"; "TargetFramework"; "Platform" ]

let private effectiveProperties sdkVersion (membership: XElement) =
    let fields = (section "Effective" membership).Elements() |> Seq.toList
    let names = fields |> List.map (required "name")
    if fields |> List.exists (fun field -> field.Name <> name "Property") then
        refuse "Invalid analyzer evaluation property element"
    if List.sort names <> List.sort effectiveNames then
        refuse "Analyzer evaluation requires exactly the supported unique effective properties"
    let values = fields |> List.map (fun field -> required "name" field, required "value" field) |> Map.ofList
    if String.IsNullOrWhiteSpace sdkVersion
       || String.IsNullOrWhiteSpace values["MSBuildVersion"]
       || String.IsNullOrWhiteSpace values["NETCoreSdkVersion"] then
        refuse "Analyzer evaluation SDK identities must be nonempty"
    if values["NETCoreSdkVersion"] <> sdkVersion then
        refuse "Analyzer evaluation SDK identity differs from its recorded context"
    values

let validateMembership sdkVersion (membership: XElement) (json: string) =
    let effective = effectiveProperties sdkVersion membership
    use document = JsonDocument.Parse(json)
    let root = document.RootElement
    let compile = root.GetProperty("Items").GetProperty("Compile")
    if compile.ValueKind <> JsonValueKind.Array then refuse "Missing evaluated Compile item array"
    let actual =
        compile.EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("FullPath").GetString() |> Path.GetFullPath)
        |> Seq.toList
    let expected =
        (section "Compile" membership).Elements(name "Item")
        |> Seq.map (required "path" >> Path.GetFullPath)
        |> Seq.toList
    if actual <> expected then refuse "Analyzer producer evaluated Compile membership changed"
    let properties = root.GetProperty("Properties")
    if properties.ValueKind <> JsonValueKind.Object then refuse "Missing evaluated properties object"
    let fields = properties.EnumerateObject() |> Seq.toList
    if (fields |> List.map (fun field -> field.Name) |> List.sort) <> List.sort effectiveNames then
        refuse "SDK output requires exactly the supported unique effective properties"
    for field in fields do
        if field.Value.ValueKind <> JsonValueKind.String then refuse "Evaluated properties must be strings"
        if field.Value.GetString() <> effective[field.Name] then
            refuse $"Analyzer producer evaluation context changed: {field.Name}"

let validateOutcome sdkVersion membership outcome =
    match outcome with
    | Succeeded(ProcessOutput.Drained json) -> validateMembership sdkVersion membership json
    | Succeeded(ProcessOutput.DrainTimedOut _) -> refuse "Analyzer producer SDK output did not finish draining"
    | Failed(code, _) -> refuse $"Analyzer producer SDK evaluation failed with exit {code}"
    | TimedOut _ -> refuse "Analyzer producer SDK evaluation exceeded its time bound"

let private evaluate (context: XElement) =
    let producer = required "project" context |> Path.GetFullPath
    let host = required "host" context |> Path.GetFullPath
    let sdk = required "sdkRoot" context |> Path.GetFullPath
    let hostName = Path.GetFileName(host)
    if hostName <> "dotnet" && hostName <> "dotnet.exe" then
        refuse "Analyzer evaluation context does not identify a dotnet SDK host"

    let globals = section "Globals" context
    let membership = section "Membership" context
    let sdkVersion = required "sdkVersion" context
    effectiveProperties sdkVersion membership |> ignore
    let propertyNames = effectiveNames
    let responsePath = Path.Combine(storeRoot (), Guid.NewGuid().ToString("N") + ".rsp")
    try
        let properties =
            globals.Elements(name "Property")
            |> Seq.map (fun property ->
                let key = required "name" property
                System.Xml.XmlConvert.VerifyName key |> ignore
                // IBuildEngine6.GetGlobalProperties returns SDK-escaped values.
                // Re-escaping '%' changes their meaning on response-file replay.
                quote ("-property:" + key + "=" + required "value" property))
            |> Seq.toList
        let lines =
            [ quote producer; "-nologo"; "-noAutoResponse"; "-getItem:Compile"
              "-getProperty:" + String.concat "," propertyNames ] @ properties
        let options = FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None)
        if not (OperatingSystem.IsWindows()) then
            options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        do
            use stream = new FileStream(responsePath, options)
            use writer = new StreamWriter(stream, UTF8Encoding(false))
            lines |> List.iter writer.WriteLine
        if OperatingSystem.IsWindows() then
            use identity = WindowsIdentity.GetCurrent()
            let security = FileSecurity()
            security.SetOwner identity.User
            security.SetAccessRuleProtection(true, false)
            security.AddAccessRule(FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow))
            FileSystemAclExtensions.SetAccessControl(FileInfo(responsePath), security)
        verifyPrivate responsePath false
        let args = "exec " + quote (Path.Combine(sdk, "MSBuild.dll")) + " " + quote ("@" + responsePath)
        let outcome = runProcess host args (Path.GetDirectoryName producer) [] (ProcessBounds.silent (TimeSpan.FromSeconds 30.0))
        validateOutcome sdkVersion membership outcome
    finally
        if File.Exists responsePath then File.Delete responsePath

/// Cache only within one snapshot operation; no TTL or cross-call reuse.
let validate (evaluations: Dictionary<string, Result<unit, string>>) projectPaths sourcePaths (reference: XElement) =
    let contextId = required "id" reference
    let expectedHash = required "hash" reference
    let key = contextId + ":" + expectedHash + ":" + FsHotWatch.CheckCache.sha256Hex (System.Text.Json.JsonSerializer.Serialize(projectPaths, Unchecked.defaultof<System.Text.Json.JsonSerializerOptions>) + System.Text.Json.JsonSerializer.Serialize(sourcePaths, Unchecked.defaultof<System.Text.Json.JsonSerializerOptions>))
    let result =
        match evaluations.TryGetValue key with
        | true, result -> result
        | _ ->
            let result =
                try
                    match Guid.TryParseExact(contextId, "N") with
                    | false, _ -> refuse "Invalid analyzer evaluation context identifier"
                    | _ -> ()
                    let root = storeRoot ()
                    verifyPrivate root true
                    let path = Path.Combine(root, contextId + ".xml")
                    verifyPrivate path false
                    let bytes = File.ReadAllBytes path
                    let hash = Security.Cryptography.SHA256.HashData bytes |> Convert.ToHexString
                    if hash <> expectedHash then refuse "Analyzer evaluation context digest changed"
                    let context = XElement.Parse(Encoding.UTF8.GetString bytes)
                    if context.Name <> name "AnalyzerEvaluationContext" || required "version" context <> "1" then
                        refuse "Unsupported analyzer evaluation context"
                    let producer = required "project" context |> Path.GetFullPath
                    if not (projectPaths |> List.exists (fun path -> Path.GetFullPath path = producer)) then
                        refuse "Analyzer evaluation context belongs to another producer"
                    let compiledSources =
                        (section "CompilerSources" context).Elements(name "Item")
                        |> Seq.map (required "path" >> Path.GetFullPath)
                        |> Seq.toList
                    if compiledSources <> (sourcePaths |> List.map Path.GetFullPath) then
                        refuse "Analyzer evaluation context names different compiler inputs"
                    evaluate context
                    Ok ()
                with
                | EvaluationRefused reason -> Error reason
                | _ ->
                    // SDK exceptions/output can contain property values. Never
                    // include their raw text in a public diagnostic or run log.
                    Error "Analyzer producer source membership is unverified; rebuild its producer or restore its private invocation context"
            evaluations.Add(key, result)
            result
    match result with
    | Ok () -> ()
    | Error reason -> refuse reason
