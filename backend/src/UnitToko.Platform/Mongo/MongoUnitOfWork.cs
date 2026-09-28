using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

public sealed class PersistenceConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class UncertainCommitException(string message, Exception inner) : Exception(message, inner);

public sealed class MongoUnitOfWork(MongoClient client)
{
    public const int MaximumAttempts = 4;

    public async Task<T> ExecuteAsync<T>(Func<IClientSessionHandle, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
            session.StartTransaction(new TransactionOptions(
                readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            try
            {
                var result = await operation(session, cancellationToken);
                Exception? earlierUnknownCommit = null;
                for (var commitAttempt = 1; commitAttempt <= MaximumAttempts; commitAttempt++)
                {
                    try
                    {
                        await session.CommitTransactionAsync(cancellationToken);
                        return result;
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new UncertainCommitException("Commit was interrupted; retry with the same idempotency key", ex);
                    }
                    catch (MongoException ex) when (ex.HasErrorLabel("UnknownTransactionCommitResult"))
                    {
                        earlierUnknownCommit = ex;
                        if (commitAttempt == MaximumAttempts)
                            throw new UncertainCommitException("Commit outcome is unknown; retry with the same idempotency key", ex);
                    }
                    catch (Exception ex) when (earlierUnknownCommit is not null)
                    {
                        throw new UncertainCommitException("Commit outcome remains unknown; retry with the same idempotency key", ex);
                    }
                }
                throw new InvalidOperationException("Unreachable commit state");
            }
            catch (UncertainCommitException) { throw; }
            catch (PersistenceConflictException)
            {
                await AbortIfActiveAsync(session);
                throw;
            }
            catch (MongoException ex) when (ex.HasErrorLabel("TransientTransactionError") || IsKnownConflict(ex))
            {
                await AbortIfActiveAsync(session);
                if (attempt == MaximumAttempts)
                    throw new PersistenceConflictException("version_conflict", "Data changed; refresh and try again");
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken);
            }
            catch
            {
                await AbortIfActiveAsync(session);
                throw;
            }
        }
        throw new PersistenceConflictException("version_conflict", "Data changed; refresh and try again");
    }

    public static async Task RequireVersionAsync(IMongoCollection<BsonDocument> collection,
        IClientSessionHandle session, FilterDefinition<BsonDocument> identityFilter,
        long expectedVersion, UpdateDefinition<BsonDocument> update, CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.And(identityFilter,
            Builders<BsonDocument>.Filter.Eq("version", expectedVersion));
        var result = await collection.UpdateOneAsync(session, filter,
            Builders<BsonDocument>.Update.Combine(update, Builders<BsonDocument>.Update.Inc("version", 1L)),
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1)
            throw new PersistenceConflictException("version_conflict", "Data changed; refresh and try again");
    }

    private static bool IsKnownConflict(MongoException ex) =>
        ex is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey } ||
        ex is MongoBulkWriteException { WriteErrors: { Count: > 0 } errors } &&
        errors.All(error => error.Category == ServerErrorCategory.DuplicateKey);

    private static async Task AbortIfActiveAsync(IClientSessionHandle session)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { if (session.IsInTransaction) await session.AbortTransactionAsync(cleanup.Token); }
        catch (ObjectDisposedException) { /* An inner fail-closed boundary already invalidated this handle. */ }
        catch (OperationCanceledException) { /* Preserve the original operation error. */ }
        catch (MongoException) { /* Preserve the original operation error. */ }
    }
}
