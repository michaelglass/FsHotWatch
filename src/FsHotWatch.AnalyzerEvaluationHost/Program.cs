using System.Reflection;
using System.Runtime.Loader;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace FsHotWatch.AnalyzerEvaluationHost;

internal static class Program
{
    private static void VerifyPrivate(string path, bool directory)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException();
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            FileSystemSecurity security = directory
                ? new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl();
            if (!security.GetOwner(typeof(SecurityIdentifier)).Equals(identity.User) ||
                (directory && !security.AreAccessRulesProtected))
                throw new InvalidDataException();
            var allowed = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow).ToList();
            if (allowed.Count == 0 || allowed.Any(rule => !rule.IdentityReference.Equals(identity.User)))
                throw new InvalidDataException();
        }
        else
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (directory) mode |= UnixFileMode.UserExecute;
            if (File.GetUnixFileMode(path) != mode) throw new InvalidDataException();
        }
    }

    private static string Required(XElement node, string attribute)
    {
        var value = (string?)node.Attribute(attribute);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException();
    }

    public static int Main(string[] args)
    {
        // Neither SDK diagnostics nor exceptions may expose private global values.
        var output = Console.Out;
        var error = Console.Error;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            if (args.Length != 1) throw new InvalidDataException();
            var requestPath = Path.GetFullPath(args[0]);
            VerifyPrivate(Path.GetDirectoryName(requestPath)!, true);
            VerifyPrivate(requestPath, false);
            var request = XElement.Load(requestPath);
            if (request.Name != "AnalyzerEvaluationRequest" || Required(request, "version") != "1" ||
                request.Elements().Count() != 1) throw new InvalidDataException();
            var globalsNode = request.Elements("Globals").Single();
            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in globalsNode.Elements())
            {
                if (property.Name != "Property") throw new InvalidDataException();
                var key = Required(property, "name");
                System.Xml.XmlConvert.VerifyName(key);
                globals.Add(key, (string?)property.Attribute("value") ?? throw new InvalidDataException());
            }
            var project = Path.GetFullPath(Required(request, "project"));
            var sdk = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Required(request, "sdkRoot")));
            var expectedSdk = Required(request, "sdkVersion");
            var expectedMsbuild = Required(request, "msbuildVersion");
            var sdkEntry = Path.Combine(sdk, "MSBuild.dll");
            var buildAssemblyPath = Path.Combine(sdk, "Microsoft.Build.dll");
            if (!File.Exists(project) || !File.Exists(sdkEntry) || !File.Exists(buildAssemblyPath) ||
                !File.Exists(Path.Combine(sdk, "MSBuild.deps.json")) ||
                !File.Exists(Path.Combine(sdk, "MSBuild.runtimeconfig.json"))) throw new InvalidDataException();

            // Establish the SDK host location before any Microsoft.Build type initializes.
            // This is process-local host bootstrap, not captured environment replay.
            Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", sdkEntry);
            var resolver = new AssemblyDependencyResolver(sdkEntry);
            Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
            {
                var path = resolver.ResolveAssemblyToPath(name);
                if (path is null) return null;
                var relative = Path.GetRelativePath(sdk, path);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException();
                return context.LoadFromAssemblyPath(path);
            }
            AssemblyLoadContext.Default.Resolving += Resolve;
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(buildAssemblyPath);
                if (Path.GetFullPath(assembly.Location) != buildAssemblyPath) throw new InvalidDataException();
                var projection = ProjectProjection.Evaluate(assembly, project, globals, expectedSdk, expectedMsbuild);
                output.WriteLine(JsonSerializer.Serialize(projection));
                return 0;
            }
            finally
            {
                AssemblyLoadContext.Default.Resolving -= Resolve;
            }
        }
        catch
        {
            error.WriteLine("Analyzer SDK evaluation refused: request, SDK binding, or evaluated inputs could not be verified.");
            return 2;
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }
}
