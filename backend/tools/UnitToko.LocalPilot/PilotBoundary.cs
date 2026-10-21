using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MongoDB.Driver;

namespace UnitToko.LocalPilot;

public static class PilotBoundary
{
    /// <summary>"release1" (the persistent pilot), "full" (a separate demo of every maintained workspace) or "review" (the
    /// loopback-only review stack: full release, its own database, personas signed in through minted sessions). Set once at startup.</summary>
    public static string Profile { get; private set; } = "release1";
    public static void UseProfile(string profile)
    {
        if (profile is not ("release1" or "full" or "review")) throw new InvalidOperationException("Profile must be release1, full or review.");
        Profile = profile;
    }
    /// <summary>Full and review both run the full release (every maintained workspace, prices, photos, personas).</summary>
    public static bool IsFullRelease => Profile != "release1";
    /// <summary>The application release the API and Next serve (Release:Profile, NEXT_PUBLIC_UNIT_TOKO_RELEASE).</summary>
    public static string Release => IsFullRelease ? "full" : "release1";
    public static string DatabaseName => "unittoko_local_pilot_" + Profile;
    public const string ReplicaSet = "unitTokoPilot";
    public static int MongoPort => Profile switch { "full" => 27030, "review" => 27031, _ => 27029 };
    public static string MongoUri => $"mongodb://127.0.0.1:{MongoPort}/?replicaSet={ReplicaSet}";
    /// <summary>The production frontend build each profile serves (next.config.ts picks .next-full for the full release).</summary>
    public static string NextBuildFolder => IsFullRelease ? ".next-full" : ".next";
    /// <summary>Each photo-scanning profile runs its own loopback ClamAV daemon, so the review stack never talks to the full pilot's.</summary>
    public static int ClamdPort => Profile == "review" ? 3311 : 3310;
    // Reserved private home.arpa names are resolved only inside the dedicated browser profile. The review profile uses its own
    // names only as the API's internal canonical origin; review browsers open https://localhost:8443 and the edge translates.
    public static string PortalHost => PortalHostFor(Profile);
    public static string ProviderHost => ProviderHostFor(Profile);
    public static string Origin => OriginFor(Profile);
    public static string Issuer => IssuerFor(Profile);
    public static string PortalHostFor(string profile) => profile == "review" ? "portal.unit-toko-review.home.arpa" : "portal.unit-toko-pilot.home.arpa";
    public static string ProviderHostFor(string profile) => profile == "review" ? "login.unit-toko-review.home.arpa" : "login.unit-toko-pilot.home.arpa";
    public static string OriginFor(string profile) => "https://" + PortalHostFor(profile);
    public static string IssuerFor(string profile) => "https://" + ProviderHostFor(profile) + "/";
    /// <summary>Where the edge listens. Every profile binds loopback only; review uses 8443 on both loopback families.</summary>
    public static IReadOnlyList<IPEndPoint> EdgeFor(string profile) => profile == "review"
        ? [new IPEndPoint(IPAddress.Loopback, 8443), new IPEndPoint(IPAddress.IPv6Loopback, 8443)]
        : [new IPEndPoint(IPAddress.Loopback, 443)];
    /// <summary>The origin a browser opens. Review browsers use localhost; the edge translates it to the API's canonical origin.</summary>
    public static string BrowserOriginFor(string profile) => profile == "review" ? "https://localhost:8443" : OriginFor(profile);
    public static IReadOnlyList<IPEndPoint> Edge => EdgeFor(Profile);
    public static string BrowserOrigin => BrowserOriginFor(Profile);

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
