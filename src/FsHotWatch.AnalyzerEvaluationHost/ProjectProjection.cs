using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace FsHotWatch.AnalyzerEvaluationHost;

// Deliberately independent of FCS/the daemon. Producer integration can reuse this
// projection contract after SDK compatibility and actual receipt tests pass.
internal static class ProjectProjection
{
    internal sealed record Item(string FullPath);
    internal sealed record Import(string FullPath, string Hash);
    internal sealed record SdkIdentity(string AssemblyPath);
    internal sealed record Projection(
        int Schema,
        Dictionary<string, List<Item>> Items,
        Dictionary<string, string> Properties,
        List<Import> Imports,
        SdkIdentity Sdk);

    private static object Property(object instance, string name) =>
        instance.GetType().GetProperty(name)!.GetValue(instance) ?? throw new InvalidDataException();

    internal static Projection Evaluate(Assembly assembly, string projectPath,
        IDictionary<string, string> globals, string expectedSdk, string expectedMsbuild)
    {
        var collectionType = assembly.GetType("Microsoft.Build.Evaluation.ProjectCollection", true)!;
        var projectType = assembly.GetType("Microsoft.Build.Evaluation.Project", true)!;
        using var collection = (IDisposable)Activator.CreateInstance(collectionType)!;
        var constructor = projectType.GetConstructor(new[]
        {
            typeof(string), typeof(IDictionary<string, string>), typeof(string), collectionType
        }) ?? throw new InvalidDataException();
        var project = constructor.Invoke(new object?[] { projectPath, globals, null, collection });
        var getProperty = projectType.GetMethod("GetPropertyValue", new[] { typeof(string) })!;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "MSBuildVersion", "NETCoreSdkVersion", "Configuration", "TargetFramework", "Platform" })
            properties.Add(name, (string)getProperty.Invoke(project, new object[] { name })!);
        if (properties["NETCoreSdkVersion"] != expectedSdk || properties["MSBuildVersion"] != expectedMsbuild)
            throw new InvalidDataException();

        var sources = new List<Item>();
        var items = (IEnumerable)projectType.GetMethod("GetItems", new[] { typeof(string) })!
            .Invoke(project, new object[] { "Compile" })!;
        foreach (var item in items)
        {
            var path = (string)item.GetType().GetMethod("GetMetadataValue", new[] { typeof(string) })!
                .Invoke(item, new object[] { "FullPath" })!;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
            sources.Add(new Item(Path.GetFullPath(path)));
        }

        var rootElementType = assembly.GetType("Microsoft.Build.Construction.ProjectRootElement", true)!;
        var parseRoot = rootElementType.GetMethod("Create", new[]
        {
            typeof(XmlReader), collectionType, typeof(bool)
        }) ?? throw new InvalidDataException();
        using var verificationCollection = (IDisposable)Activator.CreateInstance(collectionType)!;
        var imports = new List<Import>();
        foreach (var import in (IEnumerable)Property(project, "Imports"))
        {
            var evaluatedRoot = Property(import, "ImportedProject");
            var evaluatedXml = (string)Property(evaluatedRoot, "RawXml");
            var preserveFormatting = (bool)Property(evaluatedRoot, "PreserveFormatting");
            var path = (string)Property(evaluatedRoot, "FullPath");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
            path = Path.GetFullPath(path);
            // This is local binding data, never a package/shared semantic identity.
            // Reader integration must classify SDK/NuGet versus first-party imports.
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            // MSBuild uses XmlTextReader: XmlReader.Create normalizes newlines
            // in comments and attribute values before RawXml can witness them.
            using var text = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            using var reader = new XmlTextReader(new Uri(path).AbsoluteUri, text)
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            var readRoot = parseRoot.Invoke(null, new object[]
            {
                reader, verificationCollection, preserveFormatting
            }) ?? throw new InvalidDataException();
            if (!string.Equals(evaluatedXml, (string)Property(readRoot, "RawXml"), StringComparison.Ordinal))
                throw new InvalidDataException("Evaluated import XML does not match its captured bytes.");
            // The parser witness and digest use one read, so evaluation cannot
            // authorize a hash of replacement content that it never consumed.
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            imports.Add(new Import(path, hash));
        }
        return new Projection(1, new() { ["Compile"] = sources }, properties, imports, new(assembly.Location));
    }
}
