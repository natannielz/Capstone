using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnitToko.LocalPilot;

/// <summary>An opaque session the API issued for one persona, and when it ends at the latest.</summary>
public sealed record MintedSession(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Signed-in review personas without passwords: the review stack asks the API's own session store for one session per persona
/// (the way the browser rehearsal fixtures do) and writes each as a Playwright storage state to
/// <c>&lt;repository&gt;/tmp/website-ready/sessions/&lt;persona&gt;.json</c>. Only the LocalPilot executable does this, only for a
/// loopback-only edge. Tokens go to those files and nowhere else: never to the console, the logs or <c>personas.json</c>.
/// Sessions are refreshed every few minutes (a fresh file always carries a recent factor time) and revoked when they are
/// 45 minutes old or the stack stops; stopping also removes the files.
/// </summary>
public sealed partial class PilotSessions
{
    public const string CookieName = "__Host-ut_session";
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RevokeAfter = TimeSpan.FromMinutes(45);
    private const string IndexName = "personas.json";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string directory;
    private readonly Func<PilotPersona, CancellationToken, Task<MintedSession>> issue;
    private readonly Func<string, CancellationToken, Task> revoke;
    private readonly List<(string Token, DateTimeOffset MintedAt)> live = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    private PilotSessions(string directory, Func<PilotPersona, CancellationToken, Task<MintedSession>> issue, Func<string, CancellationToken, Task> revoke)
    {
        this.directory = directory; this.issue = issue; this.revoke = revoke;
    }

    [GeneratedRegex("^[a-z][a-z-]{1,39}$")] private static partial Regex PersonaName();

    /// <summary>Refuses unless every bind address is loopback (127.0.0.0/8 or ::1).</summary>
    public static void RequireLoopback(IEnumerable<IPEndPoint> binds)
    {
        var list = binds.ToArray();
        if (list.Length == 0 || list.Any(bind => !IPAddress.IsLoopback(bind.Address)))
            throw new InvalidOperationException("Review sessions are minted only for an edge bound to loopback addresses.");
    }

    public static string SessionDirectory(string repository) => Path.Combine(Path.GetFullPath(repository), "tmp", "website-ready", "sessions");

    public static string FilePath(string repository, string persona)
    {
        if (persona is null || !PersonaName().IsMatch(persona)) throw new ArgumentException("A persona name is lower-case letters and hyphens.", nameof(persona));
        var folder = SessionDirectory(repository);
        var path = Path.GetFullPath(Path.Combine(folder, persona + ".json"));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Session files stay in the sessions folder.", nameof(persona));
        return path;
    }

    public static PilotSessions Create(string repository, IReadOnlyList<IPEndPoint> binds,
        Func<PilotPersona, CancellationToken, Task<MintedSession>> issue, Func<string, CancellationToken, Task> revoke)
    {
        RequireLoopback(binds);
        var root = Path.GetFullPath(repository);
        var folder = SessionDirectory(root);
        for (var item = new DirectoryInfo(folder); item is not null && item.FullName.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Session files cannot be written through a junction or symbolic link.");
        return new(folder, issue, revoke);
    }

    /// <summary>One new session per signed-in persona, each file replaced whole; then sessions older than <see cref="RevokeAfter"/> are revoked.</summary>
    public async Task MintAllAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var persona in PilotPersonas.SignedIn)
            {
                var minted = await issue(persona, ct);
                live.Add((minted.Token, DateTimeOffset.UtcNow));
                Replace(Path.Combine(directory, persona.Name + ".json"), JsonSerializer.Serialize(new
                {
                    cookies = new[] { new { name = CookieName, value = minted.Token, domain = "localhost", path = "/", expires = minted.ExpiresAt.ToUnixTimeSeconds(),
                        httpOnly = true, secure = true, sameSite = "Lax" } },
                    origins = Array.Empty<object>(),
                }, Indented));
            }
            Replace(Path.Combine(directory, IndexName), Index());
            var now = DateTimeOffset.UtcNow;
            foreach (var old in live.Where(item => now - item.MintedAt > RevokeAfter).ToArray())
            {
                await revoke(old.Token, ct);
                live.Remove(old);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Revokes every session this stack minted and removes the session files.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var persona in PilotPersonas.SignedIn) File.Delete(Path.Combine(directory, persona.Name + ".json"));
            File.Delete(Path.Combine(directory, IndexName));
            foreach (var item in live.ToArray()) { await revoke(item.Token, ct); live.Remove(item); }
        }
        finally { gate.Release(); }
    }

    /// <summary>Who each persona is, for the harness and the reviewers. No token, password or factor code.</summary>
    private static string Index() => JsonSerializer.Serialize(new
    {
        origin = PilotBoundary.BrowserOriginFor("review"),
        refreshedAt = DateTimeOffset.UtcNow.ToString("O"),
        refreshMinutes = (int)RefreshEvery.TotalMinutes,
        personas = PilotPersonas.All.Select(persona => new
        {
            name = persona.Name, label = persona.Label, userId = persona.SignedIn ? persona.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            session = persona.SignedIn ? persona.Name + ".json" : null,
            grants = persona.Grants.Select(grant => new
            {
                template = grant.TemplateId, financePacks = grant.FinancePacks,
                scope = new { kind = grant.Scope.Kind.ToString(), grant.Scope.OrganizationId, grant.Scope.BranchId, grant.Scope.ShopId, grant.Scope.WarehouseId, grant.Scope.ChannelId },
                permissions = grant.Permissions,
            }),
        }),
    }, Indented);

    private static void Replace(string path, string content)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
