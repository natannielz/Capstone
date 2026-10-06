using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Tests.Inventory;

/// <summary>Test-only exact preserved95 migration registry. It receives the already genuine94
/// database and captured clock; no fixture creation, producer invocation, policy seeding or repair.</summary>
internal sealed class Preserved95MigrationRuntime : IDisposable
{
    private readonly PreservedContext context;
    private readonly object registry;
    private readonly MethodInfo apply, current;
    private bool disposed;
    private Preserved95MigrationRuntime(PreservedContext context, object registry, MethodInfo apply, MethodInfo current)
    { this.context = context; this.registry = registry; this.apply = apply; this.current = current; }

    internal static Preserved95MigrationRuntime Create(IMongoDatabase database, DateTimeOffset capturedClock)
    {
        if (capturedClock == default || capturedClock.Offset != TimeSpan.Zero) throw Invalid();
        var root = FindRoot(); var artifacts = Path.GetFullPath(Path.Combine(root, "tmp", "production", "aftersales-stock-source-artifacts-release6"));
        var runtime = Path.Combine(artifacts, "bin", "UnitToko.Tests", "release");
        var bindingPath = Path.Combine(root, "tmp", "production", "aftersales-stock-source-compiled-binding-release6.json");
        Assert.Equal("D6083C3B95F54CAFD0759AD8DFBC1982A24A1034F13268DA59F5DAF7EEC83166", Hash(bindingPath));
        using var binding = JsonDocument.Parse(File.ReadAllText(bindingPath)); Assert.Equal(95, binding.RootElement.GetProperty("schema").GetInt32());
        var required = new Dictionary<string, string> {
            ["UnitToko.Api"] = "1D918E87B5951A2F8E29E9417B07CFF60EBD5437F22A8B233E0153022014234B",
            ["UnitToko.Platform"] = "A41D82D00FF9109D0A5B5900D9A157D75F3F2CBC6AB331676215E2E3AE1BEE97",
            ["UnitToko.Modules"] = "1BB4C7998E2084533754BC827F92A78B970A08E3250DD357F8223F5679A2ED7D",
            ["UnitToko.Tests"] = "A2CE16992B7399B64D878205C0129DD0B762CBEDF66F0BAE97876B715EAD3D52" };
        foreach (var (name, digest) in required) {
            var binary = binding.RootElement.GetProperty("binaries").EnumerateArray().Single(row => row.GetProperty("name").GetString() == name);
            var path = Path.GetFullPath(binary.GetProperty("path").GetString()!);
            Assert.True(path.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "preserved95 binary must stay within its frozen artifact owner");
            Assert.Equal(digest, binary.GetProperty("sha256").GetString()); Assert.Equal(digest, Hash(path));
            Assert.Equal(digest, Hash(Path.Combine(runtime, name + ".dll")));
        }
        // Only the exact already-loaded Mongo/BSON identities cross this load boundary.
        Assert.Equal(Hash(Path.Combine(runtime, "MongoDB.Driver.dll")), Hash(typeof(IMongoDatabase).Assembly.Location));
        Assert.Equal(Hash(Path.Combine(runtime, "MongoDB.Bson.dll")), Hash(typeof(BsonDocument).Assembly.Location));
        var context = new PreservedContext(new AssemblyDependencyResolver(Path.Combine(runtime, "UnitToko.Tests.dll")), artifacts);
        try {
            var platform = context.LoadFromAssemblyPath(Path.Combine(runtime, "UnitToko.Platform.dll"));
            var tests = context.LoadFromAssemblyPath(Path.Combine(runtime, "UnitToko.Tests.dll"));
            var type = platform.GetType("UnitToko.Platform.Mongo.CollectionMigrations", throwOnError: true)!;
            Assert.Equal(95, type.GetField("CurrentVersion")!.GetRawConstantValue());
            var clockType = tests.GetType("UnitToko.Tests.Authorization.MutableAuthorizationClock", throwOnError: true)!;
            var clock = Activator.CreateInstance(clockType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: [capturedClock], culture: CultureInfo.InvariantCulture) ?? throw Invalid();
            Assert.Equal(capturedClock, clockType.GetProperty("UtcNow")!.GetValue(clock));
            var registry = Activator.CreateInstance(type, [database, clock]) ?? throw Invalid();
            return new(context, registry, type.GetMethod("ApplyAsync") ?? throw Invalid(), type.GetMethod("IsCurrentAsync") ?? throw Invalid());
        } catch { context.Unload(); throw; }
    }
    internal async Task ApplyAsync(CancellationToken ct = default)
    {
        if (disposed) throw Invalid();
        await InvokeAsync(apply, ct);
    }
    internal async Task<bool> IsCurrentAsync(CancellationToken ct = default)
    {
        if (disposed) throw Invalid();
        var result = await InvokeAsync(current, ct);
        return result is bool value ? value : throw Invalid();
    }
    private async Task<object?> InvokeAsync(MethodInfo method, CancellationToken ct)
    {
        Task task;
        try { task = method.Invoke(registry, [ct]) as Task ?? throw Invalid(); }
        catch (TargetInvocationException error) when (error.InnerException is not null) {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw;
        }
        await task; return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    public void Dispose() { if (disposed) return; disposed = true; context.Unload(); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static string WorkspaceRoot() => FindRoot();
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx"))) return directory.FullName;
        throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("Exact preserved95 registry and genuine94 captured clock are required");
    private sealed class PreservedContext(AssemblyDependencyResolver resolver, string artifacts) : AssemblyLoadContext("Retained94ToExact95Migration", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "MongoDB.Driver") return typeof(IMongoDatabase).Assembly;
            if (name.Name == "MongoDB.Bson") return typeof(BsonDocument).Assembly;
            var path = resolver.ResolveAssemblyToPath(name);
            if (path is null) {
                if (name.Name?.StartsWith("UnitToko.", StringComparison.Ordinal) == true) throw Invalid();
                return null;
            }
            if (!Path.GetFullPath(path).StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return LoadFromAssemblyPath(path);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            if (path is null) return 0;
            if (!Path.GetFullPath(path).StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return LoadUnmanagedDllFromPath(path);
        }
    }
}
