using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// Build-only task. The semantic projection intentionally matches the reader;
// output/reference digests are local binding evidence, never semantic identity.
public sealed class FsHotWatchAnalyzerProvenance : Task
{
    [Required] public string Mode { get; set; }
    [Required] public string Project { get; set; }
    [Required] public string Output { get; set; }
    [Required] public string CaptureFile { get; set; }
    public ITaskItem[] Sources { get; set; } = new ITaskItem[0];
    public ITaskItem[] Imports { get; set; } = new ITaskItem[0];
    public ITaskItem[] ExtraInputs { get; set; } = new ITaskItem[0];
    public ITaskItem[] References { get; set; } = new ITaskItem[0];
    public ITaskItem[] Options { get; set; } = new ITaskItem[0];
    public ITaskItem[] Copies { get; set; } = new ITaskItem[0];
    public string OutputDirectory { get; set; }
    public string SdkRoot { get; set; }
    public string SdkVersion { get; set; }
    static Tuple<string,string> PackageOf(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))); directory != null && directory.Parent != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".nupkg.metadata"))) return Tuple.Create(directory.Parent.Name, directory.Name);
        return null;
    }
    static string RelativePath(string root, string path)
    {
        return Uri.UnescapeDataString(new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString());
    }
    string Root { get { return Path.GetDirectoryName(Path.GetFullPath(Project)); } }
    static string Hash(string path) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    static string Encode(IEnumerable<string> values) { return string.Concat(values.Select(v => v.Length + ":" + v)); }
    static string Digest(string value) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
    static string A(XElement e, string n) { var a = e.Attribute(n); if (a == null) throw new InvalidDataException("Missing " + n); return a.Value; }
    static void Verify(string path, string expected) { if (!string.Equals(Hash(path), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Inputs/output changed during analyzer build: " + path); }
    string Resolve(string path) { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path)); }
    string Label(string path) { return RelativePath(Root, Resolve(path)).Replace('\\', '/'); }
    static IEnumerable<string> Discover(string directory)
    {
        var extensions = new HashSet<string>(new[] { ".fs", ".fsi", ".fsx", ".cs", ".fsproj", ".csproj", ".props", ".targets", ".json" });
        var excluded = new HashSet<string>(new[] { "bin", "obj", ".git", ".jj", ".workspaces", "node_modules" });
        foreach (string file in Directory.GetFiles(directory)) if (extensions.Contains(Path.GetExtension(file))) yield return file;
        foreach (string child in Directory.GetDirectories(directory)) if (!excluded.Contains(Path.GetFileName(child))) foreach (string file in Discover(child)) yield return file;
    }
    static string DiscoveryHash(string directory) { return Digest(Encode(Discover(directory).Select(p => RelativePath(directory, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal))); }
    static void SaveChanged(string path, XElement data)
    {
        string text = data.ToString(SaveOptions.DisableFormatting);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }
    static string Identity(XElement root)
    {
        var values = new List<string>();
        foreach (var f in root.Element("Inputs").Elements("File"))
            if (!A(f, "key").StartsWith("binding:", StringComparison.Ordinal)) values.Add(Encode(new[] { "input", A(f, "key"), A(f, "hash").ToUpperInvariant() }));
        foreach (var o in root.Element("Options").Elements("Option")) values.Add(Encode(new[] { "option", A(o, "name"), A(o, "value") }));
        foreach (var p in root.Element("Packages").Elements("Package")) values.Add(Encode(new[] { "package", A(p, "id").ToLowerInvariant(), A(p, "version").ToLowerInvariant() }));
        foreach (var d in root.Element("Dependencies").Elements("Project")) values.Add(Encode(new[] { "project", A(d, "key"), A(d, "identity") }));
        values.Sort(StringComparer.Ordinal);
        return Digest(Encode(new[] { "first-party-v1" }.Concat(values)));
    }
    static void Validate(XElement root, HashSet<string> seen, Dictionary<string, bool> evaluations = null)
    {
        if (evaluations == null) evaluations = new Dictionary<string, bool>();
        ValidateEvaluation(root, evaluations);
        string output = A(root, "output");
        if (!seen.Add(output)) throw new InvalidDataException("Cyclic analyzer project reference " + output);
        Verify(output, A(root, "outputHash"));
        foreach (var input in root.Element("Inputs").Elements("File")) Verify(A(input, "path"), A(input, "hash"));
        foreach (var missing in root.Element("Inputs").Elements("Missing")) if (File.Exists(A(missing, "path")) || Directory.Exists(A(missing, "path"))) throw new InvalidDataException("New analyzer build input appeared: " + A(missing, "path"));
        foreach (var directory in root.Element("Discovery").Elements("Directory")) if (DiscoveryHash(A(directory, "path")) != A(directory, "hash")) throw new InvalidDataException("Analyzer input membership changed");
        foreach (var dependency in root.Element("Dependencies").Elements("Project"))
        {
            var child = XElement.Load(A(dependency, "output") + ".fshw-analyzer.xml");
            Validate(child, new HashSet<string>(seen), evaluations);
            if (Identity(child) != A(dependency, "identity")) throw new InvalidDataException("Stale analyzer project dependency");
        }
    }
    static XElement EvaluateMembership(string projectPath, IDictionary<string, string> globals)
    {
        var assembly = System.Reflection.Assembly.Load("Microsoft.Build");
        var collectionType = assembly.GetType("Microsoft.Build.Evaluation.ProjectCollection", true);
        var projectType = assembly.GetType("Microsoft.Build.Evaluation.Project", true);
        using (var collection = (IDisposable)Activator.CreateInstance(collectionType))
        {
            var constructor = projectType.GetConstructor(new[] { typeof(string), typeof(IDictionary<string, string>), typeof(string), collectionType });
            object project = constructor.Invoke(new object[] { projectPath, globals, null, collection });
            var items = (System.Collections.IEnumerable)projectType.GetMethod("GetItems", new[] { typeof(string) }).Invoke(project, new object[] { "Compile" });
            var sources = new XElement("Compile");
            foreach (object item in items)
            {
                string path = (string)item.GetType().GetMethod("GetMetadataValue", new[] { typeof(string) }).Invoke(item, new object[] { "FullPath" });
                sources.Add(new XElement("Item", new XAttribute("path", Path.GetFullPath(path))));
            }
            var effective = new XElement("Effective");
            foreach (string name in new[] { "MSBuildVersion", "NETCoreSdkVersion", "Configuration", "TargetFramework", "Platform" })
            {
                string value = (string)projectType.GetMethod("GetPropertyValue", new[] { typeof(string) }).Invoke(project, new object[] { name });
                effective.Add(new XElement("Property", new XAttribute("name", name), new XAttribute("value", value)));
            }
            return new XElement("Membership", sources, effective);
        }
    }
    XElement CaptureEvaluation()
    {
        try
        {
            // Only this producer invocation's explicit globals are replayed.
            // No process environment values are captured, logged, or persisted.
            var globals = ((IBuildEngine6)BuildEngine).GetGlobalProperties().ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrWhiteSpace(host)) host = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            var context = new XElement("AnalyzerEvaluationContext", new XAttribute("version", "1"),
                new XAttribute("project", Path.GetFullPath(Project)), new XAttribute("host", Path.GetFullPath(host)),
                new XAttribute("sdkRoot", Path.GetFullPath(SdkRoot)), new XAttribute("sdkVersion", SdkVersion),
                new XElement("Globals", globals.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new XElement("Property", new XAttribute("name", p.Key), new XAttribute("value", p.Value)))),
                new XElement("CompilerSources", Sources.Select(source => new XElement("Item", new XAttribute("path", Resolve(source.ItemSpec))))),
                EvaluateMembership(Path.GetFullPath(Project), globals));
            XElement previous = File.Exists(CaptureFile) ? XElement.Load(CaptureFile).Element("Evaluation") : null;
            return AnalyzerReplayStore.Save(context, previous);
        }
        catch { throw new InvalidDataException("Could not capture the analyzer producer invocation context"); }
    }
    static void ValidateEvaluation(XElement receipt, Dictionary<string, bool> evaluations)
    {
        try
        {
            var reference = receipt.Elements("Evaluation").Single();
            var context = AnalyzerReplayStore.Read(reference);
            if (receipt.Name != "AnalyzerProvenance" || A(receipt, "version") != "1" ||
                context.Name != "AnalyzerEvaluationContext" || A(context, "version") != "1") throw new InvalidDataException();
            var inputs = receipt.Elements("Inputs").Single().Elements("File").ToList();
            var projects = inputs.Where(p => A(p, "key").StartsWith("project:", StringComparison.Ordinal)).Select(p => Path.GetFullPath(A(p, "path"))).ToList();
            var sources = inputs.Where(p => A(p, "key").StartsWith("source:", StringComparison.Ordinal)).Select(p => Path.GetFullPath(A(p, "path"))).ToList();
            string producer = Path.GetFullPath(A(context, "project"));
            var compiledSources = context.Elements("CompilerSources").Single().Elements("Item").Select(p => Path.GetFullPath(A(p, "path"))).ToList();
            if (!projects.Contains(producer) || !sources.SequenceEqual(compiledSources)) throw new InvalidDataException();
            var membership = context.Elements("Membership").Single();
            var effective = membership.Elements("Effective").Single().Elements().ToList();
            var expectedNames = new[] { "MSBuildVersion", "NETCoreSdkVersion", "Configuration", "TargetFramework", "Platform" };
            if (effective.Any(p => p.Name != "Property") ||
                !effective.Select(p => A(p, "name")).OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(expectedNames.OrderBy(p => p, StringComparer.Ordinal))) throw new InvalidDataException();
            var values = effective.ToDictionary(p => A(p, "name"), p => A(p, "value"));
            if (string.IsNullOrWhiteSpace(values["MSBuildVersion"]) || string.IsNullOrWhiteSpace(values["NETCoreSdkVersion"]) ||
                values["NETCoreSdkVersion"] != A(context, "sdkVersion")) throw new InvalidDataException();
            string key = Encode(new[] { A(reference, "id"), A(reference, "hash"), Encode(projects), Encode(sources) });
            if (evaluations.ContainsKey(key)) return;
            var globals = context.Element("Globals").Elements("Property").ToDictionary(p => A(p, "name"), p => (string)p.Attribute("value"), StringComparer.OrdinalIgnoreCase);
            if (!XNode.DeepEquals(context.Element("Membership"), EvaluateMembership(A(context, "project"), globals)))
                throw new InvalidDataException();
            evaluations.Add(key, true);
        }
        catch { throw new InvalidDataException("Analyzer producer evaluated source membership changed or could not be verified"); }
    }
    XElement Capture()
    {
        var inputs = new XElement("Inputs");
        Action<string, string> input = (key, path) => { path = Resolve(path); inputs.Add(new XElement("File", new XAttribute("key", key), new XAttribute("path", path), new XAttribute("hash", Hash(path)))); };
        input("project:" + Path.GetFileName(Project), Project);
        int index = 0;
        foreach (var source in Sources) input("source:" + index++ + ":" + Label(source.ItemSpec), source.ItemSpec);
        if (index == 0) throw new InvalidDataException("Analyzer producer has no evaluated source inputs");
        var packages = new XElement("Packages");
        foreach (var import in Imports.Select(i => Resolve(i.ItemSpec)).Distinct().OrderBy(p => p, StringComparer.Ordinal))
        {
            if (import == Path.GetFullPath(Project)) continue;
            var package = PackageOf(import);
            if (package != null) packages.Add(new XElement("Package", new XAttribute("id", package.Item1), new XAttribute("version", package.Item2)));
            else if (!string.IsNullOrEmpty(SdkRoot) && import.StartsWith(Path.GetFullPath(SdkRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                packages.Add(new XElement("Package", new XAttribute("id", "dotnet-sdk"), new XAttribute("version", SdkVersion)));
            else input("import:" + Label(import), import);
        }
        foreach (var extra in ExtraInputs.Select(i => Resolve(i.ItemSpec)).Where(File.Exists).Distinct().OrderBy(p => p, StringComparer.Ordinal)) input("resource:" + Label(extra), extra);
        foreach (var filename in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" })
            for (var directory = new DirectoryInfo(Root); directory != null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, filename);
                if (File.Exists(path))
                {
                    if (!inputs.Elements("File").Any(f => A(f, "path") == path)) input("context:" + Label(path), path);
                    break; // MSBuild/SDK search stops at the nearest file.
                }
                inputs.Add(new XElement("Missing", new XAttribute("path", path)));
            }
        var discovery = new XElement("Discovery", new XElement("Directory", new XAttribute("path", Root), new XAttribute("hash", DiscoveryHash(Root))));
        var dependencies = new XElement("Dependencies");
        var dependencyEvaluations = new Dictionary<string, bool>();
        foreach (var reference in References)
        {
            string path = Resolve(reference.ItemSpec);
            string id = reference.GetMetadata("NuGetPackageId"), version = reference.GetMetadata("NuGetPackageVersion");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(version))
                packages.Add(new XElement("Package", new XAttribute("id", id), new XAttribute("version", version)));
            else if (!string.IsNullOrEmpty(reference.GetMetadata("MSBuildSourceProjectFile")))
            {
                input("binding:" + Path.GetFileName(path), path);
                var child = XElement.Load(path + ".fshw-analyzer.xml");
                Validate(child, new HashSet<string>(), dependencyEvaluations);
                dependencies.Add(new XElement("Project", new XAttribute("key", Label(reference.GetMetadata("MSBuildSourceProjectFile"))), new XAttribute("output", path), new XAttribute("identity", Identity(child))));
            }
            else throw new InvalidDataException("Reference has no package/project provenance: " + path);
        }
        var options = new XElement("Options", Options.Select(o => new XElement("Option", new XAttribute("name", o.ItemSpec), new XAttribute("value", o.GetMetadata("Value").Replace(Root, "$PROJECT")))));
        return new XElement("AnalyzerProvenance", new XAttribute("version", "1"), inputs, discovery, options, packages, dependencies, CaptureEvaluation());
    }
    public override bool Execute()
    {
        try
        {
            Output = Resolve(Output);
            CaptureFile = Resolve(CaptureFile);
            if (!string.IsNullOrEmpty(OutputDirectory)) OutputDirectory = Resolve(OutputDirectory);
            if (Mode == "capture") SaveChanged(CaptureFile, Capture());
            else if (Mode == "compiled")
            {
                var captured = XElement.Load(CaptureFile);
                if (!XNode.DeepEquals(captured, Capture())) throw new InvalidDataException("Analyzer inputs changed while compiler was running");
                captured.SetAttributeValue("output", Path.GetFullPath(Output));
                captured.SetAttributeValue("outputHash", Hash(Output));
                SaveChanged(CaptureFile + ".compiled", captured);
            }
            else if (Mode == "publish")
            {
                var compiled = XElement.Load(CaptureFile + ".compiled");
                Validate(compiled, new HashSet<string>());
                var captured = new XElement(compiled); captured.Attribute("output").Remove(); captured.Attribute("outputHash").Remove();
                if (!XNode.DeepEquals(captured, Capture())) throw new InvalidDataException("Analyzer inputs no longer match successful compilation");
                Verify(Output, A(compiled, "outputHash"));
                compiled.SetAttributeValue("output", Path.GetFullPath(Output));
                SaveChanged(Output + ".fshw-analyzer.xml", compiled);
            }
            else if (Mode != "packages") throw new InvalidDataException("Unknown analyzer provenance operation " + Mode);
            if (Mode == "publish" || Mode == "packages")
                foreach (var copy in Copies)
                {
                    string id = copy.GetMetadata("NuGetPackageId"), version = copy.GetMetadata("NuGetPackageVersion");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) continue;
                    string destination = Path.Combine(OutputDirectory, copy.GetMetadata("DestinationSubDirectory"), Path.GetFileName(copy.ItemSpec));
                    if (!destination.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string hash = Hash(Resolve(copy.ItemSpec)); Verify(destination, hash);
                    SaveChanged(destination + ".fshw-package.xml", new XElement("PackageProvenance", new XAttribute("schema", "1"), new XAttribute("id", id), new XAttribute("version", version), new XAttribute("output", Path.GetFullPath(destination)), new XAttribute("outputHash", hash)));
                }
            return true;
        }
        catch (Exception exception) { Log.LogErrorFromException(exception, true); return false; }
    }
}


// Local-only replay state. Reflection bridges the inline task's netstandard
// reference surface to the current SDK runtime's atomic permission APIs.
internal static class AnalyzerReplayStore
{
    static readonly string StoreRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fshw", "analyzer-contexts");
    static Type RuntimeType(string name) { return Type.GetType(name, true); }
    static bool Windows { get { return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows); } }
    static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Analyzer replay store cannot use a link");
    }
    static string CurrentSid()
    {
        var type = RuntimeType("System.Security.Principal.WindowsIdentity, System.Security.Principal.Windows");
        using (var identity = (IDisposable)type.GetMethod("GetCurrent", Type.EmptyTypes).Invoke(null, null))
        {
            object user = type.GetProperty("User").GetValue(identity);
            return (string)user.GetType().GetProperty("Value").GetValue(user);
        }
    }
    static void VerifyWindows(string path, bool directory)
    {
        Type extension = RuntimeType("System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl");
        Type infoType = directory ? typeof(DirectoryInfo) : typeof(FileInfo);
        object info = directory ? (object)new DirectoryInfo(path) : new FileInfo(path);
        object security = extension.GetMethod("GetAccessControl", new[] { infoType }).Invoke(null, new[] { info });
        Type sidType = RuntimeType("System.Security.Principal.SecurityIdentifier, System.Security.Principal.Windows");
        string sid = CurrentSid();
        object owner = security.GetType().GetMethod("GetOwner").Invoke(security, new object[] { sidType });
        if ((string)owner.GetType().GetProperty("Value").GetValue(owner) != sid) throw new InvalidDataException("Analyzer replay store has another owner");
        if (directory && !(bool)security.GetType().GetProperty("AreAccessRulesProtected").GetValue(security)) throw new InvalidDataException("Analyzer replay store inherits access permissions");
        var rules = (System.Collections.IEnumerable)security.GetType().GetMethod("GetAccessRules").Invoke(security, new object[] { true, true, sidType });
        bool ownerAllowed = false;
        foreach (object rule in rules)
        {
            if (rule.GetType().GetProperty("AccessControlType").GetValue(rule).ToString() != "Allow") continue;
            object identity = rule.GetType().GetProperty("IdentityReference").GetValue(rule);
            if ((string)identity.GetType().GetProperty("Value").GetValue(identity) != sid) throw new InvalidDataException("Analyzer replay store is accessible by another identity");
            ownerAllowed = true;
        }
        if (!ownerAllowed) throw new InvalidDataException("Analyzer replay store does not grant its owner access");
    }
    static void Verify(string path, bool directory)
    {
        NoLink(path);
        if (Windows) { VerifyWindows(path, directory); return; }
        object mode = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) }).Invoke(null, new object[] { path });
        int expected = directory ? 448 : 384; // 0700 / 0600
        if (Convert.ToInt32(mode) != expected) throw new InvalidDataException("Analyzer replay store permissions must be owner-only");
    }
    static void EnsureRoot()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoreRoot));
        if (!Directory.Exists(StoreRoot))
        {
            if (Windows)
            {
                Type securityType = RuntimeType("System.Security.AccessControl.DirectorySecurity, System.IO.FileSystem.AccessControl");
                object security = Activator.CreateInstance(securityType);
                string sid = CurrentSid();
                securityType.GetMethod("SetSecurityDescriptorSddlForm", new[] { typeof(string) }).Invoke(security, new object[] { "O:" + sid + "D:P(A;OICI;FA;;;" + sid + ")" });
                RuntimeType("System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl").GetMethod("CreateDirectory", new[] { securityType, typeof(string) }).Invoke(null, new[] { security, StoreRoot });
            }
            else
            {
                Type modeType = RuntimeType("System.IO.UnixFileMode, System.Private.CoreLib");
                typeof(Directory).GetMethod("CreateDirectory", new[] { typeof(string), modeType }).Invoke(null, new[] { (object)StoreRoot, Enum.ToObject(modeType, 448) });
            }
        }
        Verify(StoreRoot, true);
    }
    static string PathFor(string id)
    {
        Guid parsed;
        if (!Guid.TryParseExact(id, "N", out parsed)) throw new InvalidDataException("Invalid analyzer replay context identifier");
        return Path.Combine(StoreRoot, id + ".xml");
    }
    static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", ""); }
    public static XElement Read(XElement reference)
    {
        try
        {
            EnsureRoot();
            string path = PathFor((string)reference.Attribute("id"));
            Verify(path, false);
            byte[] bytes = File.ReadAllBytes(path);
            if (Hash(bytes) != (string)reference.Attribute("hash")) throw new InvalidDataException();
            return XElement.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch { throw new InvalidDataException("Analyzer replay context is missing, changed, or not private; rebuild its producer"); }
    }
    public static XElement Save(XElement context, XElement previous)
    {
        try
        {
            EnsureRoot();
            byte[] bytes = Encoding.UTF8.GetBytes(context.ToString(SaveOptions.DisableFormatting));
            string hash = Hash(bytes);
            if (previous != null && (string)previous.Attribute("hash") == hash)
            {
                try { if (XNode.DeepEquals(Read(previous), context)) return new XElement(previous); }
                catch (InvalidDataException) { /* Rebuilding repairs missing private context. */ }
            }
            string id = Guid.NewGuid().ToString("N");
            string path = PathFor(id);
            // The parent is already private. Tighten the new empty file before
            // writing context bytes; no sensitive bytes have public permissions.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!Windows)
                {
                    Type modeType = RuntimeType("System.IO.UnixFileMode, System.Private.CoreLib");
                    typeof(File).GetMethod("SetUnixFileMode", new[] { typeof(string), modeType }).Invoke(null, new[] { (object)path, Enum.ToObject(modeType, 384) });
                }
                else
                {
                    Type securityType = RuntimeType("System.Security.AccessControl.FileSecurity, System.IO.FileSystem.AccessControl");
                    object security = Activator.CreateInstance(securityType);
                    string sid = CurrentSid();
                    securityType.GetMethod("SetSecurityDescriptorSddlForm", new[] { typeof(string) }).Invoke(security, new object[] { "O:" + sid + "D:P(A;;FA;;;" + sid + ")" });
                    RuntimeType("System.IO.FileSystemAclExtensions, System.IO.FileSystem.AccessControl").GetMethod("SetAccessControl", new[] { typeof(FileInfo), securityType }).Invoke(null, new object[] { new FileInfo(path), security });
                }
                Verify(path, false);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            return new XElement("Evaluation", new XAttribute("id", id), new XAttribute("hash", hash));
        }
        catch { throw new InvalidDataException("Could not preserve private analyzer replay context"); }
    }
}
