using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Operations;

/// <summary>One collection: document count, SHA-256 over every document's exact BSON in <c>_id</c> order, and SHA-256 over
/// its definition (collection options such as the validator, plus every index specification).</summary>
public sealed record CollectionDigest(string Name, long Count, string ContentSha256, string DefinitionSha256);

/// <summary>One evidence object: its path relative to the evidence root (forward slashes), length and SHA-256.</summary>
public sealed record EvidenceDigest(string Path, long Length, string Sha256);

/// <summary>Q09: what a restore must reproduce exactly. Counts and hashes only; no document content or file bytes.</summary>
public sealed record RestoreManifest(int Format, DateTimeOffset CapturedAt, int SchemaVersion,
    IReadOnlyList<CollectionDigest> Collections, IReadOnlyList<EvidenceDigest> Evidence);

/// <summary>Captures a manifest before a backup and compares a restored copy against it. Independent of how the copy
/// was made (file snapshot, mongodump/mongorestore, managed snapshot); any difference is a finding and fails the restore.</summary>
public static class RestoreVerification
{
    public const int Format = 1;

    public static async Task<RestoreManifest> CaptureAsync(IMongoDatabase database, string? evidenceRoot, DateTimeOffset capturedAt, CancellationToken ct)
    {
        var specs = await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct);
        var collections = new List<CollectionDigest>();
        foreach (var spec in specs.Where(spec => spec.GetValue("type", "collection").AsString == "collection" && !spec["name"].AsString.StartsWith("system.", StringComparison.Ordinal))
                     .OrderBy(spec => spec["name"].AsString, StringComparer.Ordinal))
            collections.Add(await DigestAsync(database, spec, ct));
        var schema = 0;
        if (collections.Any(collection => collection.Name == "schemaMigrations"))
        {
            var applied = await database.GetCollection<BsonDocument>("schemaMigrations").Find(Builders<BsonDocument>.Filter.Eq("state", "applied"))
                .Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(1).FirstOrDefaultAsync(ct);
            schema = applied is null ? 0 : applied["_id"].ToInt32();
        }
        return new(Format, capturedAt, schema, collections, evidenceRoot is null ? [] : DigestEvidence(evidenceRoot, ct));
    }

    /// <summary>Every difference between the manifest captured before the backup and the restored copy, in a stable order.
    /// An empty list is the only passing result.</summary>
    public static IReadOnlyList<string> Compare(RestoreManifest expected, RestoreManifest actual)
    {
        var findings = new List<string>();
        if (expected.Format != Format || actual.Format != Format) findings.Add("manifest format is not " + Format);
        if (expected.SchemaVersion != actual.SchemaVersion) findings.Add($"schema version differs: expected {expected.SchemaVersion}, found {actual.SchemaVersion}");
        var restored = actual.Collections.ToDictionary(collection => collection.Name, StringComparer.Ordinal);
        foreach (var source in expected.Collections)
        {
            if (!restored.Remove(source.Name, out var copy)) { findings.Add("collection missing: " + source.Name); continue; }
            if (copy.Count != source.Count) findings.Add($"document count differs: {source.Name} (expected {source.Count}, found {copy.Count})");
            else if (copy.ContentSha256 != source.ContentSha256) findings.Add("document content differs: " + source.Name);
            if (copy.DefinitionSha256 != source.DefinitionSha256) findings.Add("validator or indexes differ: " + source.Name);
        }
        findings.AddRange(restored.Keys.Order(StringComparer.Ordinal).Select(name => "unexpected collection: " + name));
        var objects = actual.Evidence.ToDictionary(item => item.Path, StringComparer.Ordinal);
        foreach (var source in expected.Evidence)
        {
            if (!objects.Remove(source.Path, out var copy)) findings.Add("evidence object missing: " + source.Path);
            else if (copy.Length != source.Length || copy.Sha256 != source.Sha256) findings.Add("evidence object differs: " + source.Path);
        }
        findings.AddRange(objects.Keys.Order(StringComparer.Ordinal).Select(path => "unexpected evidence object: " + path));
        return findings;
    }

    private static async Task<CollectionDigest> DigestAsync(IMongoDatabase database, BsonDocument spec, CancellationToken ct)
    {
        var name = spec["name"].AsString;
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        // Raw BSON keeps the exact stored bytes (field order and numeric types); each document is length-prefixed.
        using (var cursor = await database.GetCollection<RawBsonDocument>(name).Find(FilterDefinition<RawBsonDocument>.Empty)
                   .Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct))
            while (await cursor.MoveNextAsync(ct))
                foreach (var document in cursor.Current)
                    using (document)
                    {
                        var bytes = new byte[document.Slice.Length];
                        document.Slice.GetBytes(0, bytes, 0, bytes.Length);
                        content.AppendData(bytes); count++;
                    }
        var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
        var definition = new BsonDocument {
            { "options", spec.GetValue("options", new BsonDocument()) },
            { "indexes", new BsonArray(indexes.Select(index => { var copy = index.DeepClone().AsBsonDocument; copy.Remove("ns"); return copy; })
                .OrderBy(index => index["name"].AsString, StringComparer.Ordinal)) },
        };
        return new(name, count, Hex(content.GetHashAndReset()), Hex(SHA256.HashData(definition.ToBson())));
    }

    private static IReadOnlyList<EvidenceDigest> DigestEvidence(string root, CancellationToken ct)
    {
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The evidence root does not exist.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        return Directory.EnumerateFiles(full, "*", options).Select(path => {
            ct.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            return new EvidenceDigest(Path.GetRelativePath(full, path).Replace('\\', '/'), stream.Length, Hex(SHA256.HashData(stream)));
        }).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
    }

    private static string Hex(byte[] value) => Convert.ToHexStringLower(value);
}
