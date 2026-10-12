# Verified checkpoints — slice 1 (seal primitive + receipt-origin seals) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the repeated ~1.7 s receipt-origin reconstruction with a keyed, server-sealed checkpoint that is verified in milliseconds and still refuses tampering at request time.

**Architecture:** A small shared `UnitToko.Platform/Integrity` primitive computes and verifies HMAC seals over owner-record hashes, membership and dependencies, with keys encrypted by the existing certificate-protected Data Protection provider. The Inventory-owned `HeldReceiptOriginProof` writes a seal after its first full proof inside an owning unit-of-work operation and, on later captures, verifies the seal instead of rebuilding the origin graph. Missing seals fall back to the full proof; failed seals refuse.

**Tech Stack:** C#/.NET 10, MongoDB driver, ASP.NET Data Protection, xUnit (`UnitToko.Tests`, `[Collection("Mongo")]` native fixtures), candidate overlay tooling under `tmp/`.

**Spec:** [docs/superpowers/specs/2026-10-12-verified-checkpoints-design.md](../specs/2026-10-12-verified-checkpoints-design.md) — status draft; this plan executes only after the owner approves spec and plan.

## Global Constraints

- Target: staging p95 scoped reads ≤500 ms, ordinary writes ≤1 s (unit-toko-production.md §233); slice 1 must measurably cut the R02 consent and must not slow any read.
- Seal keys: 256-bit HMAC-SHA256, encrypted with `SessionPayloadProtector.Provider` purpose `UnitToko.Integrity.Seals.v1`, stored in the protected key directory; never in MongoDB, config or env as plain text.
- Fail closed: absent seal → existing full proof; present-but-invalid seal → refuse (`*_unavailable`), never fall back.
- Request-time tamper detection (spec §9 option 1).
- Module ownership: Integrity holds only the primitive; the receipt-origin seal collection, writer and reader belong to Inventory (`HeldReceiptOriginProof`) with membership capture from Procurement (`PurchaseOrderSourceReadCapture`).
- Migration 115 appended after R02's 114; forward only; pilot 78 untouched.
- Work happens inside the R02 candidate (`tmp/r02-main113-validation-candidate`) through the recorder; every source change is a recorded correction; temporary `ReplayDiagnostics` code from browser3–13 is removed in Task 7.
- Deadlines (60 s transaction, 30 s browser action) and existing assertions are never weakened.

## Review Focus

- A seal row edited by someone with database access but no key (recomputed unkeyed hashes) must be refused, not re-proven around.
- A membership row deleted or added after sealing must be refused even though the receipt row itself is unchanged.
- Key rotation: seals made under an older retained key must still verify; seals under an unknown `keyId` must refuse.
- A caller-supplied session (tests, migrations, tools) must never write seals; only an owning unit-of-work operation seals.
- Concurrent first proofs of the same receipt must produce exactly one seal row (unique index) and both callers must succeed.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `tmp/correct-r02-validation.py` (modify) | Recorder accepts brand-new files (`"content"`, no baseline). |
| `backend/src/UnitToko.Platform/Integrity/SealKeyRing.cs` (create) | Load/provision protected HMAC keys; lookup by `keyId`. |
| `backend/src/UnitToko.Platform/Integrity/ProvenanceSeal.cs` (create) | Seal payload, MAC computation, canonical member hashing. |
| `backend/src/UnitToko.Platform/Integrity/SealVerifier.cs` (create) | Verify MAC, record hash, membership, per-operation memo. |
| `backend/src/UnitToko.Platform/Mongo/Migrations/ProvenanceSealSchemas.cs` (create) | Validator and keys for `inventoryReceiptOriginSeals`. |
| `backend/src/UnitToko.Platform/Mongo/Migrations/ProvenanceSealMigration.cs` (create) | Migration 115: create the empty seal collection. |
| `backend/src/UnitToko.Platform/Mongo/CollectionMigrations.cs` (modify) | Register 115; `CurrentVersion = 115`. |
| `backend/src/UnitToko.Platform/Procurement/PurchaseOrderHistoricalSourceProof.cs` (modify) | `PurchaseOrderSourceReadCapture` exports sealed scopes and rebuilds from them. |
| `backend/src/UnitToko.Platform/Inventory/HeldReceiptOriginProof.cs` (modify) | Seal after full proof; verify seal on later captures. |
| `backend/src/UnitToko.Api/Program.cs` (modify) | Register `SealKeyRing` from the protected key directory. |
| `backend/tests/UnitToko.Tests/Integrity/ProvenanceSealTests.cs` (create) | Pure MAC/rotation/canonical tests. |
| `backend/tests/UnitToko.Tests/Inventory/ReceiptOriginSealTests.cs` (create) | Native seal/verify/tamper/fallback/concurrency tests. |

---

### Task 0: Recorder support for new files

**Files:**
- Modify: `tmp/correct-r02-validation.py`

**Interfaces:**
- Produces: spec change item form `{"path": "...", "content": "<full text>"}` for paths absent from both the candidate MANIFEST and main.

- [ ] **Step 1: Add the new-file branch.** In the per-change loop, before the `row is None` branch that copies from main, add:

```python
    if 'content' in item:
        assert row is None and not (root/item['path']).exists() and not path.exists(), item['path']
        path.parent.mkdir(parents=True, exist_ok=True); path.write_text(item['content'], encoding='utf-8')
        row={'path':item['path'],'origin':'Explicit native correction (new file): '+spec['reason'],'baselineSha256':None,'candidateSha256':sha(path),'originalCandidateSha256':None}
        manifest['files'].append(row);manifest['ownedPaths'].append(item['path']);manifest['sourceCount']+=1
        changes.append({'path':item['path'],'beforeSha256':None,'afterSha256':row['candidateSha256']}); continue
```

and in the initial precondition loop skip items with `content` (`if 'content' in item: continue`).

- [ ] **Step 2: Verify on a dry spec.** Copy the candidate folder to a scratch directory, point `folder` at it via an env override `R02_CANDIDATE`, record a one-file `content` spec, confirm MANIFEST gains the row and `source.patch` shows `/dev/null` as the source. Expected: exit 0, one new row.

- [ ] **Step 3: Commit.** `git add tmp/correct-r02-validation.py` is under `tmp/` (ignored); record the script hash in the R02 ledger row instead.

### Task 1: Seal key ring and seal payload

**Files:**
- Create: `backend/src/UnitToko.Platform/Integrity/SealKeyRing.cs`
- Create: `backend/src/UnitToko.Platform/Integrity/ProvenanceSeal.cs`
- Test: `backend/tests/UnitToko.Tests/Integrity/ProvenanceSealTests.cs`

**Interfaces:**
- Produces: `SealKeyRing.ForKeys(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)`, `SealKeyRing.Load(IDataProtectionProvider provider, string directory)`, `SealKeyRing.Provision(IDataProtectionProvider provider, string directory)`, `string SealKeyRing.CurrentKeyId`, `byte[] SealKeyRing.Key(string keyId)`; `ProvenanceSeal.Create(SealKeyRing ring, string kind, long ownerId, string recordHash, BsonArray membership, BsonArray dependencies, DateTimeOffset at) : BsonDocument`, `ProvenanceSeal.MacValid(SealKeyRing ring, BsonDocument seal) : bool`, `ProvenanceSeal.RowHash(BsonDocument row) : string`, `SealUnavailableException(string code)`.

- [ ] **Step 1: Write failing tests.**

```csharp
using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Platform.Integrity;
namespace UnitToko.Tests.Integrity;
public sealed class ProvenanceSealTests
{
    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32), KeyB = RandomNumberGenerator.GetBytes(32);
    private static BsonArray Members() => new() { new BsonDocument { { "collection", "c" }, { "filter", new BsonDocument("orderId", 1L) },
        { "members", new BsonArray { new BsonDocument { { "id", 1L }, { "hash", new string('a', 64) } } } } } };
    [Fact] public void Seal_verifies_and_any_payload_change_breaks_the_mac() {
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var seal = ProvenanceSeal.Create(ring, "inventory.receiptOrigin", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch);
        Assert.True(ProvenanceSeal.MacValid(ring, seal));
        foreach (var edit in new Action<BsonDocument>[] { s => s["recordHash"] = new string('c', 64), s => s["ownerId"] = 8L,
            s => s["membership"][0]["members"][0]["hash"] = new string('d', 64), s => s["keyId"] = "k2" }) {
            var copy = seal.DeepClone().AsBsonDocument; edit(copy); Assert.False(ProvenanceSeal.MacValid(ring, copy)); }
    }
    [Fact] public void Retained_key_verifies_old_seals_and_unknown_key_refuses() {
        var old = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var seal = ProvenanceSeal.Create(old, "inventory.receiptOrigin", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch);
        var rotated = SealKeyRing.ForKeys("k2", new Dictionary<string, byte[]> { ["k1"] = KeyA, ["k2"] = KeyB });
        Assert.True(ProvenanceSeal.MacValid(rotated, seal));
        Assert.False(ProvenanceSeal.MacValid(SealKeyRing.ForKeys("k2", new Dictionary<string, byte[]> { ["k2"] = KeyB }), seal));
    }
    [Fact] public void Row_hash_ignores_only_the_freshness_fence() {
        var row = new BsonDocument { { "_id", 1L }, { "fence", 0L }, { "value", "x" } };
        var advanced = row.DeepClone().AsBsonDocument; advanced["fence"] = 5L;
        var changed = row.DeepClone().AsBsonDocument; changed["value"] = "y";
        Assert.Equal(ProvenanceSeal.RowHash(row), ProvenanceSeal.RowHash(advanced));
        Assert.NotEqual(ProvenanceSeal.RowHash(row), ProvenanceSeal.RowHash(changed));
    }
}
```

- [ ] **Step 2: Run to verify failure.** Record the test file through the recorder (Task 0 `content` form), build with `tmp/r02-cycle.ps1` disabled browser start (run `build-candidate.ps1 -Attempt <next>`), then `run-proof.ps1 -Mode Native -BuildAttempt <n> -Attempt <next> -NativeFilter "FullyQualifiedName~UnitToko.Tests.Integrity.ProvenanceSealTests"`. Expected: build FAIL (types missing).

- [ ] **Step 3: Implement.**

```csharp
// SealKeyRing.cs
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
namespace UnitToko.Platform.Integrity;
public sealed class SealUnavailableException(string code) : Exception("Provenance seal is unavailable") { public string Code { get; } = code; }
/// <summary>Server-held HMAC keys for provenance seals. Key material never enters MongoDB.</summary>
public sealed class SealKeyRing
{
    private const string Purpose = "UnitToko.Integrity.Seals.v1";
    private readonly IReadOnlyDictionary<string, byte[]> keys;
    public string CurrentKeyId { get; }
    private SealKeyRing(string current, IReadOnlyDictionary<string, byte[]> keys) {
        if (!keys.ContainsKey(current) || keys.Values.Any(key => key.Length != 32) || keys.Keys.Any(id => id.Length is 0 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new SealUnavailableException("seal_key_ring_invalid");
        CurrentKeyId = current; this.keys = keys;
    }
    public static SealKeyRing ForKeys(string currentKeyId, IReadOnlyDictionary<string, byte[]> keys) => new(currentKeyId, keys.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    public byte[] Key(string keyId) => keys.TryGetValue(keyId, out var key) ? key : throw new SealUnavailableException("seal_key_unknown");
    internal bool Has(string keyId) => keys.ContainsKey(keyId);
    public static SealKeyRing Load(IDataProtectionProvider provider, string directory) {
        var protector = provider.CreateProtector(Purpose);
        var current = File.ReadAllText(Path.Combine(directory, "seal-current.txt")).Trim();
        var keys = Directory.GetFiles(directory, "seal-key-*.protected").ToDictionary(
            file => Path.GetFileNameWithoutExtension(file)["seal-key-".Length..], file => protector.Unprotect(File.ReadAllBytes(file)));
        return new(current, keys);
    }
    /// <summary>Explicit operator provisioning; never called implicitly at startup.</summary>
    public static string Provision(IDataProtectionProvider provider, string directory) {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(directory, "seal-key-" + id + ".protected"), provider.CreateProtector(Purpose).Protect(key));
        File.WriteAllText(Path.Combine(directory, "seal-current.txt"), id); return id;
    }
}
```

```csharp
// ProvenanceSeal.cs
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using UnitToko.Platform.Catalog;
namespace UnitToko.Platform.Integrity;
/// <summary>Keyed seal over an immutable owner record, its proof membership and dependency seals.</summary>
public static class ProvenanceSeal
{
    public static string RowHash(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return CatalogRepository.Digest(copy); }
    private static BsonDocument Payload(BsonDocument seal) => new() { { "kind", seal["kind"] }, { "ownerId", seal["ownerId"] }, { "recordHash", seal["recordHash"] },
        { "membership", seal["membership"] }, { "dependencies", seal["dependencies"] }, { "keyId", seal["keyId"] }, { "sealedAt", seal["sealedAt"] } };
    private static string Mac(byte[] key, BsonDocument seal) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(CatalogRepository.Digest(Payload(seal))))).ToLowerInvariant();
    public static BsonDocument Create(SealKeyRing ring, string kind, long ownerId, string recordHash, BsonArray membership, BsonArray dependencies, DateTimeOffset at) {
        var seal = new BsonDocument { { "_id", ownerId }, { "schemaVersion", 1 }, { "kind", kind }, { "ownerId", ownerId }, { "recordHash", recordHash },
            { "membership", membership.DeepClone() }, { "dependencies", dependencies.DeepClone() }, { "keyId", ring.CurrentKeyId },
            { "sealedAt", DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()).UtcDateTime } };
        seal["mac"] = Mac(ring.Key(ring.CurrentKeyId), seal); return seal;
    }
    public static bool MacValid(SealKeyRing ring, BsonDocument seal) {
        if (!seal.TryGetValue("keyId", out var id) || !id.IsString || !ring.Has(id.AsString) || !seal.TryGetValue("mac", out var mac) || !mac.IsString) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac.AsString), Encoding.ASCII.GetBytes(Mac(ring.Key(id.AsString), seal)));
    }
}
```

- [ ] **Step 4: Run tests.** Same native filter. Expected: 3/3 PASS.
- [ ] **Step 5: Record and commit** the correction (`slice1-seal-primitive`) and the ledger line.

### Task 2: Seal verifier with membership and per-operation memo

**Files:**
- Create: `backend/src/UnitToko.Platform/Integrity/SealVerifier.cs`
- Test: `backend/tests/UnitToko.Tests/Integrity/SealVerifierTests.cs`

**Interfaces:**
- Consumes: `ProvenanceSeal.MacValid`, `ProvenanceSeal.RowHash`, `MongoUnitOfWork.OwnsOperation(IClientSessionHandle)`.
- Produces: `SealVerifier(IMongoDatabase database, SealKeyRing ring)`, `Task<IReadOnlyList<BsonDocument>> SealVerifier.VerifyAsync(IClientSessionHandle tx, BsonDocument seal, string kind, long ownerId, BsonDocument liveRecord, CancellationToken ct)` — returns, per membership scope in seal order, a `BsonDocument { collection, filter, rows: [...] }` of the live rows it verified; throws `SealUnavailableException("seal_mismatch")`. `static BsonArray SealVerifier.Membership(IEnumerable<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)> scopes)`.

- [ ] **Step 1: Write failing native tests** (`[Collection("Mongo")]`, using `MongoFixture` like existing tests): seal a row plus a two-row membership scope in a test collection, then assert (a) verify passes; (b) editing a member row's non-fence field → throws `seal_mismatch`; (c) inserting a third row matching the filter → throws; (d) deleting a member → throws; (e) advancing only `fence` → passes; (f) a seal for another `ownerId` → throws.

```csharp
[Collection("Mongo")]
public sealed class SealVerifierTests(MongoFixture fixture)
{
    [Fact] public async Task Membership_and_record_changes_refuse_while_fence_advance_passes() {
        var database = fixture.Client.GetDatabase("seal-verifier-" + Guid.NewGuid().ToString("N"));
        var rows = database.GetCollection<BsonDocument>("members");
        await rows.InsertManyAsync([new() { { "_id", 1L }, { "orderId", 9L }, { "fence", 0L }, { "v", "a" } }, new() { { "_id", 2L }, { "orderId", 9L }, { "fence", 0L }, { "v", "b" } }]);
        var owner = new BsonDocument { { "_id", 50L }, { "v", "receipt" } };
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
        var filter = new BsonDocument("orderId", 9L);
        var seal = ProvenanceSeal.Create(ring, "test.kind", 50, ProvenanceSeal.RowHash(owner),
            SealVerifier.Membership([("members", filter, await rows.Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync())]), [], DateTimeOffset.UtcNow);
        var verifier = new SealVerifier(database, ring);
        async Task Verify() { using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction(); await verifier.VerifyAsync(tx, seal, "test.kind", 50, owner, default); await tx.AbortTransactionAsync(); }
        await Verify();
        await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("fence", 4L))); await Verify();
        await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("v", "z")));
        Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(Verify)).Code);
        await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("v", "a")));
        await rows.InsertOneAsync(new() { { "_id", 3L }, { "orderId", 9L }, { "fence", 0L }, { "v", "c" } });
        Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(Verify)).Code);
        await rows.DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 2L, 3L })));
        Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(Verify)).Code);
        await database.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
    }
}
```

- [ ] **Step 2: Run to verify failure.** Expected: build FAIL (`SealVerifier` missing).

- [ ] **Step 3: Implement.**

```csharp
using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
namespace UnitToko.Platform.Integrity;
/// <summary>Verifies a seal against live rows. Memoized only inside the owning unit-of-work operation.</summary>
public sealed class SealVerifier(IMongoDatabase database, SealKeyRing ring)
{
    private const int MaximumMembers = 1000;
    private static readonly ConditionalWeakTable<IClientSessionHandle, Dictionary<string, IReadOnlyList<BsonDocument>>> verified = new();
    public static BsonArray Membership(IEnumerable<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)> scopes) =>
        new(scopes.OrderBy(scope => scope.Collection, StringComparer.Ordinal).ThenBy(scope => scope.Filter.ToJson(), StringComparer.Ordinal).Select(scope => new BsonDocument {
            { "collection", scope.Collection }, { "filter", scope.Filter.DeepClone() },
            { "members", new BsonArray(scope.Rows.OrderBy(row => row["_id"]).Select(row => new BsonDocument { { "id", row["_id"] }, { "hash", ProvenanceSeal.RowHash(row) } })) } }));
    public async Task<IReadOnlyList<BsonDocument>> VerifyAsync(IClientSessionHandle tx, BsonDocument seal, string kind, long ownerId, BsonDocument liveRecord, CancellationToken ct) {
        static SealUnavailableException Mismatch() => new("seal_mismatch");
        if (!tx.IsInTransaction || seal["kind"] != kind || seal["ownerId"] != ownerId || !ProvenanceSeal.MacValid(ring, seal) ||
            seal["recordHash"] != ProvenanceSeal.RowHash(liveRecord)) throw Mismatch();
        var memo = MongoUnitOfWork.OwnsOperation(tx) ? tx.WrappedCoreSession.CurrentTransaction.TransactionNumber + ":" + seal["mac"].AsString : null;
        if (memo is not null && verified.TryGetValue(tx, out var done)) lock (done) if (done.TryGetValue(memo, out var known)) return known;
        var result = new List<BsonDocument>();
        foreach (var scope in seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument)) {
            var actual = await database.GetCollection<BsonDocument>(scope["collection"].AsString).Find(tx, scope["filter"].AsBsonDocument)
                .Sort(new BsonDocument("_id", 1)).Limit(MaximumMembers + 1).ToListAsync(ct);
            var expected = scope["members"].AsBsonArray;
            if (actual.Count != expected.Count || actual.Where((row, i) => row["_id"] != expected[i]["id"] || ProvenanceSeal.RowHash(row) != expected[i]["hash"].AsString).Any())
                throw Mismatch();
            result.Add(new BsonDocument { { "collection", scope["collection"] }, { "filter", scope["filter"] }, { "rows", new BsonArray(actual) } });
        }
        if (memo is not null) { var table = verified.GetValue(tx, _ => []); lock (table) table[memo] = result; }
        return result;
    }
}
```

- [ ] **Step 4: Run tests.** Expected: PASS.
- [ ] **Step 5: Record correction `slice1-seal-verifier`; ledger line.**

### Task 3: Migration 115 — receipt-origin seal storage

**Files:**
- Create: `backend/src/UnitToko.Platform/Mongo/Migrations/ProvenanceSealSchemas.cs`, `ProvenanceSealMigration.cs`
- Modify: `backend/src/UnitToko.Platform/Mongo/CollectionMigrations.cs` (append step, `CurrentVersion = 115`)
- Test: `backend/tests/UnitToko.Tests/Inventory/ReceiptOriginSealTests.cs` (migration case)

**Interfaces:**
- Produces: `ProvenanceSealSchemas.Collection = "inventoryReceiptOriginSeals"`, `ProvenanceSealSchemas.Validator()`, `ProvenanceSealSchemas.Keys`, `ProvenanceSealMigration(IMongoDatabase)` with `Version => 115`, `ProvenanceSealSchemas.RequireStorageAsync(IMongoDatabase, CancellationToken)`.

- [ ] **Step 1: Write the failing migration test**: run the genuine migration chain to 115 on a fresh database (pattern of `OrderSubstitutionMigrationTests`), assert the collection exists with strict validator and unique `_id`/`ownerId` index, that a document missing `mac` is rejected by the validator, and that rerunning 115 on non-empty compatible storage throws (no silent rewrite).
- [ ] **Step 2: Run to verify failure** (migration missing).
- [ ] **Step 3: Implement** following `ReplenishmentMigration` exactly: majority read of `schemaMigrations`, `MigrationHistory.IsValid(history, Version)`, refuse non-empty or incompatible storage, create with `validationLevel: strict`, `validationAction: error`, `writeConcern: majority`, then `RequireStorageAsync`. Validator fields: `_id` long ≥1, `schemaVersion` int enum [1], `kind` enum [`inventory.receiptOrigin`], `ownerId` long ≥1, `recordHash`/`mac` 64-hex strings, `membership` array (≤64) of `{collection: string ≤64, filter: object, members: array ≤1000 of {id: long|int|objectId, hash: 64-hex}}`, `dependencies` array ≤64, `keyId` string 1–64 `[A-Za-z0-9-]`, `sealedAt` date; `additionalProperties: false`.
- [ ] **Step 4: Run tests.** Expected: PASS; full R02 migration tests still pass (`OrderSubstitutionMigrationTests`).
- [ ] **Step 5: Record correction `slice1-migration-115`; ledger line.**

### Task 4: Sealed scopes from `PurchaseOrderSourceReadCapture`

**Files:**
- Modify: `backend/src/UnitToko.Platform/Procurement/PurchaseOrderHistoricalSourceProof.cs`
- Test: `ReceiptOriginSealTests.cs` (round-trip case)

**Interfaces:**
- Produces: `IEnumerable<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)> PurchaseOrderSourceReadCapture.SealedScopes()` (requires sealed); `static PurchaseOrderSourceReadCapture PurchaseOrderSourceReadCapture.FromSealed(IMongoDatabase database, IClientSessionHandle tx, IReadOnlyList<BsonDocument> verifiedScopes)` — takes the `{collection, filter, rows}` list returned by `SealVerifier.VerifyAsync`.

- [ ] **Step 1: Failing test**: capture a genuine receipt origin, export `SealedScopes()`, rebuild with `FromSealed`, and assert `RequireAsync` passes, then fails after a member row edit.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement**: `SealedScopes()` maps the existing private `sealedScopes` (`Scope(Collection, Filter, Rows)`) to tuples; `FromSealed` creates a capture bound to `(database, tx)` whose `sealedScopes` are the membership scopes with the freshly read rows; `counts`, `sourceOwners` and `pools` stay empty and the seal's recorded counts/pools are verified by `SealVerifier` membership instead. If the existing capture recorded `counts`, `sourceOwners` or `pools`, `SealedScopes()` throws `Q.Broken()` (not sealable in slice 1) so the caller keeps the full proof.
- [ ] **Step 4: Run tests.** PASS.
- [ ] **Step 5: Record correction `slice1-capture-sealed-scopes`.**

### Task 5: Seal and verify in `HeldReceiptOriginProof`

**Files:**
- Modify: `backend/src/UnitToko.Platform/Inventory/HeldReceiptOriginProof.cs`
- Modify: `backend/src/UnitToko.Api/Program.cs` and the test fixture service construction (`StoreTestData`) to supply a `SealKeyRing`
- Test: `backend/tests/UnitToko.Tests/Inventory/ReceiptOriginSealTests.cs`

**Interfaces:**
- Consumes: `SealVerifier.VerifyAsync`, `ProvenanceSeal.Create`, `PurchaseOrderSourceReadCapture.SealedScopes/FromSealed`, `ProvenanceSealSchemas.Collection`.
- Produces: `HeldReceiptOriginProof.CaptureAsync(..., SealKeyRing? seals, ...)` — `null` keeps today's behavior exactly (no seal read or write).

- [ ] **Step 1: Failing native tests** (genuine procurement receipt fixture from `StockReleaseServiceTests.Data`):
  1. First capture inside `data.Unit.ExecuteAsync` writes exactly one seal row; a second capture in a new operation reads the seal and does **not** reconstruct the origin, and `RequireAsync` passes. Assert this with an `internal static long ProcurementStockReceiptRecords.OriginReconstructions` counter (`Interlocked.Increment` at the top of `ReadOriginCoreAsync`, added in this task; `UnitToko.Platform` already exposes internals to `UnitToko.Tests`): its value must not change during the second capture.
  2. Editing the receipt row with a recomputed `snapshotHash` → second capture refuses `HeldReceiptMovementHistory.Unavailable` (no fallback).
  3. Deleting one sealed membership row → refuses.
  4. Seal row deleted → second capture falls back to the full proof and re-seals.
  5. Seal row with `mac` flipped → refuses.
  6. Caller-supplied raw session (`StartSessionAsync`) → no seal row is written.
  7. Two concurrent operations capturing the same receipt → one seal row (duplicate-key insert treated as "already sealed": re-read and verify the existing row) and both succeed.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** in `CaptureAsync` after `RequireSession`:

```csharp
        var owned = MongoUnitOfWork.OwnsOperation(tx);
        var sealRows = database.GetCollection<BsonDocument>(Mongo.Migrations.ProvenanceSealSchemas.Collection);
        if (seals is not null) {
            var existing = await sealRows.Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct);
            if (existing is not null) {
                var receiptRow = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct)
                    ?? throw HeldReceiptMovementHistory.Unavailable();
                IReadOnlyList<BsonDocument> verifiedScopes;
                try { verifiedScopes = await new SealVerifier(database, seals).VerifyAsync(tx, existing, Kind, receiptId, receiptRow, ct); }
                catch (SealUnavailableException) { throw HeldReceiptMovementHistory.Unavailable(); }
                var sealedCapture = PurchaseOrderSourceReadCapture.FromSealed(database, tx, verifiedScopes);
                var sealedRecords = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources.CaptureHistory(sealedCapture), new ProcurementQualityProofGraph(tx), sealedCapture);
                var proof = new HeldReceiptOriginProof(database, tx, sealedRecords, sealedCapture, clock, receiptRow);
                await sealedRecords.RequireOriginInspectionAsync(tx, receiptRow, ct); // custody/private evidence stay fresh
                return proof;
            }
        }
```

and after the existing full `ReadOriginCoreAsync` + `capture.Seal()`:

```csharp
        if (seals is not null && owned) {
            var seal = ProvenanceSeal.Create(seals, Kind, receiptId, ProvenanceSeal.RowHash(receipt), SealVerifier.Membership(capture.SealedScopes()), [], clock.UtcNow);
            try { await sealRows.InsertOneAsync(tx, seal, cancellationToken: ct); }
            catch (MongoWriteException error) when (error.WriteError.Category == ServerErrorCategory.DuplicateKey) {
                var other = await sealRows.Find(tx, new BsonDocument("_id", receiptId)).SingleAsync(ct);
                await new SealVerifier(database, seals).VerifyAsync(tx, other, Kind, receiptId, receipt, ct);
            }
        }
```

with `private const string Kind = "inventory.receiptOrigin";`. Remove the browser13 in-memory reconstruction reuse (`reconstructed` table) — the seal replaces it. Thread `SealKeyRing?` from the four `CaptureAsync` callers' owners (`ShipmentDispatchFiniteSource`, `PackConversionService.History`, `StockReleaseSource`, `HeldReceiptMovementHistory`) via their constructors; DI registers `SealKeyRing.Load(protector.Provider, keyDirectory)` in `Program.cs` next to `SessionPayloadProtector`; when no key ring is configured, startup fails in `Production` and passes `null` elsewhere.
- [ ] **Step 4: Run tests**: `ReceiptOriginSealTests` all PASS; then the full R02 native suite (`run-proof.ps1` with no filter). Expected: 22/22 + new cases PASS.
- [ ] **Step 5: Record correction `slice1-receipt-origin-seals`; ledger line.**

### Task 6: Latency check and R02 browser acceptance

**Files:**
- Modify (test-only, temporary): browser request-timing diagnostic retained from browser4

- [ ] **Step 1: Run `tmp/r02-cycle.ps1`** (no spec) and read `*-requests.log`. Expected: consent preview and decision each drop by ≥5 s versus browser14 (22.8 s / >30 s).
- [ ] **Step 2: If the decision still exceeds 30 s**, stop and open slice 2 (Fulfillment dispatch and Ordering progress seals) per the spec; do not weaken the bound.
- [ ] **Step 3: On browser PASS**, review all screenshots (desktop 1280 / mobile 360) and write `root-visual-review-browser<N>.json`.

### Task 7: Remove temporary diagnostics and close R02 isolation

**Files:**
- Modify: `ShipmentDispatchRecords.cs`, `OrderDecisions.cs`, `OrderSubmissionService.SubstitutionDecisions.cs`, `ApprovedOrderAdmissionReader.cs`, `HeldReceiptOriginProof.cs`, `PurchaseOrderHistoricalSourceProof.cs`, `OrderSubstitutionBrowserTests.cs`

- [ ] **Step 1: Record one correction `slice1-remove-diagnostics`** deleting every `ReplayDiagnostics` declaration and call, the browser denial/request/replay/aggregate observers, and their files' writes. Grep must return nothing: `rg "ReplayDiagnostics|recordDenial|requests.log|replays.log|aggregate.log" tmp/r02-main113-validation-candidate/backend`.
- [ ] **Step 2: Full gates on the final source:** build, full native suite, client checks, contracts, frontend build, browser. Each must pass on the same candidate manifest.
- [ ] **Step 3: `python tmp/close-r02-isolated114.py build=<n> native=<n> client=<n> contracts=<n> frontend=<n> browser=<n>`** writes the acceptance; then integrate into main per the main113 pattern (`tmp/r10-main113-integration/`), commit, push, verify Vercel.
