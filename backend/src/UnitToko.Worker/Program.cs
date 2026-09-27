using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = Host.CreateApplicationBuilder(args.Where(arg => arg != "--migrate").ToArray());
var mongoUri = builder.Configuration["Mongo:Uri"];
var databaseName = builder.Configuration["Mongo:Database"];
if (string.IsNullOrWhiteSpace(mongoUri) || string.IsNullOrWhiteSpace(databaseName) ||
    databaseName is "admin" or "local" or "config" || databaseName.Contains(' '))
    throw new InvalidOperationException("Mongo:Uri and a dedicated Mongo:Database are required.");

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(_ => new MongoConnection(mongoUri, databaseName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Client);
builder.Services.AddSingleton(sp => sp.GetRequiredService<MongoConnection>().Database);
builder.Services.AddSingleton<MongoUnitOfWork>();
builder.Services.AddSingleton<IdAllocator>();
builder.Services.AddSingleton<IdempotencyStore>();
builder.Services.AddSingleton<OutboxStore>();
builder.Services.AddSingleton<CollectionMigrations>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnitToko.Worker");
if (migrate)
{
    await host.Services.GetRequiredService<CollectionMigrations>().ApplyAsync();
    logger.LogInformation("MongoDB schema migration complete at version {Version}", CollectionMigrations.CurrentVersion);
    return;
}

logger.LogWarning("No outbox processors are registered; external delivery is disabled.");
await host.RunAsync();
