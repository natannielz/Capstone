using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
internal sealed class EvidenceAdmissionTestData : IAsyncDisposable
{
    private readonly string marker = Guid.NewGuid().ToString("N");
    private readonly string parent = Path.GetFullPath(Environment.CurrentDirectory);
    internal string Root { get; }
    internal string StoreId { get; } = new('a', 64);
    internal CatalogAuthoringTestData Catalog { get; private set; } = null!;
    internal EvidenceService Service { get; private set; } = null!;
    internal EvidenceStoreRegistration Registration { get; }
    internal IMongoDatabase DB => Catalog.Database;
    internal MongoClient Client => Catalog.Client;
    internal ActorContext Actor => Catalog.Maker;
    internal EvidenceTargetRef Target { get; private set; } = null!;
    internal MongoUnitOfWork Unit => new(Client);
    internal static AuthorizationCommandMetadata Meta(string key = "reserve-one", string reason = "Synthetic evidence review") => new(key, reason, "synthetic-evidence");
    internal static readonly AuthorizationReadMetadata Read = new("evidence-status", "synthetic-evidence-status");
    private EvidenceAdmissionTestData()
    {
        Root = Path.Combine(parent, ".evidence-test-" + marker);
        var security = new DirectorySecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).Create(security);
        File.WriteAllText(Path.Combine(Root, ".fixture-id"), marker);
        Registration = EvidenceStoreRegistration.OpenLocal(Root, StoreId, ["S-1-5-18", "S-1-5-32-544",
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"]);
        Assert.True(Registration.Store is not null, Registration.Reason);
    }
    internal static async Task<EvidenceAdmissionTestData> CreateAsync(MongoFixture fixture, bool sku = false, bool policy = true)
    {
        var data = new EvidenceAdmissionTestData();
        try
        {
            data.Catalog = await CatalogAuthoringTestData.CreateAsync(fixture);
            var target = sku ? await data.Catalog.SkuAsync() : await data.Catalog.FamilyAsync();
            data.Target = new(sku ? EvidenceTargetKind.CatalogSku : EvidenceTargetKind.CatalogFamily, target.Id.Value);
            data.Service = data.NewService();
            if (policy)
            {
                var row = EvidenceAdmissionSchemaTests.Policy();
                row["_id"] = (await new IdAllocator(data.DB).NextAsync("evidenceStorePolicies")).Value;
                await data.Rows("evidenceStorePolicies").InsertOneAsync(row);
            }
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }
    internal EvidenceService NewService(EvidenceStoreRegistration? registration = null, IMongoDatabase? db = null, MongoClient? client = null) =>
        new(db ?? DB, new(client ?? Client), new(db ?? DB), Catalog.Clock, registration ?? Registration);
    internal IMongoCollection<BsonDocument> Rows(string name) => DB.GetCollection<BsonDocument>(name);
    internal Task<BsonDocument> RowAsync(string name, long id) => Rows(name).Find(new BsonDocument("_id", id)).SingleAsync();
    internal Task<ReservationResult> ReserveAsync(string key = "reserve-one", ActorContext? actor = null, ReserveCatalogImage? request = null, string reason = "Synthetic evidence review") =>
        Unit.ExecuteAsync((tx, ct) => Service.ReserveAsync(tx, actor ?? Actor, request ?? new(Target), Meta(key, reason), ct));
    internal Task<SafeStatus> StatusAsync(long id, ActorContext? actor = null) =>
        Unit.ExecuteAsync((tx, ct) => Service.GetStatusAsync(tx, actor ?? Actor, id, Read, ct));
    internal Task<StageClaim> StageClaimAsync(ReservationResult reservation, string? requestId = null, ActorContext? actor = null)
    {
        var deadline = Catalog.Clock.UtcNow.AddSeconds(120);
        if (reservation.ExpiresAt < deadline) deadline = reservation.ExpiresAt;
        return Unit.ExecuteAsync((tx, ct) => Service.ClaimStageAsync(tx, actor ?? Actor, reservation.ReservationId, reservation.Revision, requestId ?? new string('b', 64), deadline, Meta(), ct));
    }
    public async ValueTask DisposeAsync()
    {
        if (Catalog is not null) await Catalog.DisposeAsync();
        var exact = Path.GetFullPath(Root);
        if (Path.GetDirectoryName(exact) != parent.TrimEnd(Path.DirectorySeparatorChar) || Path.GetFileName(exact) != ".evidence-test-" + marker ||
            (File.GetAttributes(exact) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(Path.Combine(exact, ".fixture-id")) != marker)
            throw new InvalidOperationException("Evidence fixture cleanup identity mismatch.");
        if (Directory.EnumerateFileSystemEntries(exact, "*", SearchOption.AllDirectories).Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Unexpected evidence fixture link.");
        Directory.Delete(exact, true);
    }
}
