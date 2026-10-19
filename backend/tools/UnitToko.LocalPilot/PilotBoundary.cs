using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MongoDB.Driver;

namespace UnitToko.LocalPilot;

public static class PilotBoundary
{
    /// <summary>"release1" (the persistent pilot) or "full" (a separate demo of every maintained workspace). Set once at startup.</summary>
    public static string Profile { get; private set; } = "release1";
    public static void UseProfile(string profile)
    {
        if (profile is not ("release1" or "full")) throw new InvalidOperationException("Profile must be release1 or full.");
        Profile = profile;
    }
    public static string DatabaseName => "unittoko_local_pilot_" + Profile;
    public const string ReplicaSet = "unitTokoPilot";
    public static int MongoPort => Profile == "full" ? 27030 : 27029;
    public static string MongoUri => $"mongodb://127.0.0.1:{MongoPort}/?replicaSet={ReplicaSet}";
    /// <summary>The production frontend build each profile serves (next.config.ts picks .next-full for the full release).</summary>
    public static string NextBuildFolder => Profile == "full" ? ".next-full" : ".next";
    // Reserved private home.arpa names are resolved only inside the dedicated browser profile.
    public const string PortalHost = "portal.unit-toko-pilot.home.arpa";
    public const string ProviderHost = "login.unit-toko-pilot.home.arpa";
    public const string Origin = "https://" + PortalHost;
    public const string Issuer = "https://" + ProviderHost + "/";

    public static void RequireDatabase(string uri, string database)
    {
        var parsed = new MongoUrl(uri);
        var servers = parsed.Servers.ToArray();
        if (database != DatabaseName || parsed.DatabaseName is not null || servers.Length != 1 ||
            servers[0].Host != "127.0.0.1" || servers[0].Port != MongoPort || parsed.ReplicaSetName != ReplicaSet ||
            parsed.Username is not null || parsed.Password is not null)
            throw new InvalidOperationException("The synthetic pilot permits only its dedicated loopback replica set and database.");
    }

    public static string StateDirectory(string repository)
    {
        var root = Path.GetFullPath(repository);
        if (!File.Exists(Path.Combine(root, "backend", "src", "UnitToko.Api", "UnitToko.Api.csproj")) ||
            !File.Exists(Path.Combine(root, "frontend", "package.json")))
            throw new InvalidOperationException("Use the repository root, not an arbitrary data directory.");
        var path = Path.Combine(root, "tmp", "pilot", Profile);
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Pilot state cannot be stored through a junction or symbolic link.");
        return path;
    }

    [SupportedOSPlatform("windows")]
    public static void ProtectDirectory(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows user is unavailable.");
        var access = new DirectorySecurity();
        access.SetOwner(current);
        access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.Create(access);
        directory.SetAccessControl(access);
        var actual = directory.GetAccessControl();
        if (!actual.AreAccessRulesProtected || actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != current))
            throw new InvalidOperationException("Pilot state directory must be private to the current Windows user.");
    }
}
