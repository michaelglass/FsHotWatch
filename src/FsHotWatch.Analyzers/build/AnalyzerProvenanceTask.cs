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
    static void Validate(XElement root, HashSet<string> seen)
    {
        string output = A(root, "output");
        if (!seen.Add(output)) throw new InvalidDataException("Cyclic analyzer project reference " + output);
        Verify(output, A(root, "outputHash"));
        foreach (var input in root.Element("Inputs").Elements("File")) Verify(A(input, "path"), A(input, "hash"));
        foreach (var missing in root.Element("Inputs").Elements("Missing")) if (File.Exists(A(missing, "path")) || Directory.Exists(A(missing, "path"))) throw new InvalidDataException("New analyzer build input appeared: " + A(missing, "path"));
        foreach (var directory in root.Element("Discovery").Elements("Directory")) if (DiscoveryHash(A(directory, "path")) != A(directory, "hash")) throw new InvalidDataException("Analyzer input membership changed");
        foreach (var dependency in root.Element("Dependencies").Elements("Project"))
        {
            var child = XElement.Load(A(dependency, "output") + ".fshw-analyzer.xml");
            Validate(child, new HashSet<string>(seen));
            if (Identity(child) != A(dependency, "identity")) throw new InvalidDataException("Stale analyzer project dependency");
        }
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
                Validate(child, new HashSet<string>());
                dependencies.Add(new XElement("Project", new XAttribute("key", Label(reference.GetMetadata("MSBuildSourceProjectFile"))), new XAttribute("output", path), new XAttribute("identity", Identity(child))));
            }
            else throw new InvalidDataException("Reference has no package/project provenance: " + path);
        }
        var options = new XElement("Options", Options.Select(o => new XElement("Option", new XAttribute("name", o.ItemSpec), new XAttribute("value", o.GetMetadata("Value").Replace(Root, "$PROJECT")))));
        return new XElement("AnalyzerProvenance", new XAttribute("version", "1"), inputs, discovery, options, packages, dependencies);
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
