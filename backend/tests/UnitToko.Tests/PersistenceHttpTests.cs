using System.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests;

[Collection("Mongo")]
public sealed class PersistenceHttpTests(MongoFixture mongo)
{
    private WebApplicationFactory<Program> Factory(string databaseName) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Mongo:Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0",
                    ["Mongo:Database"] = databaseName
                })));

    [Fact]
    public async Task Api_startup_does_not_migrate_and_readiness_requires_current_schema()
    {
        var name = mongo.DatabaseName + "_readiness";
        var database = mongo.Client.GetDatabase(name);
        try
        {
            using var factory = Factory(name);
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            await AssertUnavailable(client);
            Assert.Equal(0, await database.GetCollection<BsonDocument>("schemaMigrations")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));

            await factory.Services.GetRequiredService<CollectionMigrations>().ApplyAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            await database.GetCollection<BsonDocument>("schemaMigrations").InsertOneAsync(
                new BsonDocument { { "_id", CollectionMigrations.CurrentVersion + 1 },
                    { "appliedAt", DateTime.UtcNow } });
            await AssertUnavailable(client);
        }
        finally { await mongo.Client.DropDatabaseAsync(name); }
    }

    [Fact]
    public async Task Worker_migrate_command_applies_once_without_business_seed()
    {
        var name = "ut_cli_" + Guid.NewGuid().ToString("N");
        var database = mongo.Client.GetDatabase(name);
        try
        {
            await RunMigrationWorkerAsync(name);
            var migration = await database.GetCollection<BsonDocument>("schemaMigrations")
                .Find(Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion))
                .SingleAsync();
            Assert.Equal("applied", migration["state"].AsString);
            Assert.True(await new CollectionMigrations(database, new FixedClock(DateTimeOffset.UtcNow))
                .IsCurrentAsync());
            Assert.Empty(await database.GetCollection<BsonDocument>("organizations")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());

            var appliedAt = migration["appliedAt"].AsBsonDateTime.MillisecondsSinceEpoch;
            await RunMigrationWorkerAsync(name);
            var repeated = await database.GetCollection<BsonDocument>("schemaMigrations")
                .Find(Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion))
                .SingleAsync();
            Assert.Equal(appliedAt, repeated["appliedAt"].AsBsonDateTime.MillisecondsSinceEpoch);
            Assert.Equal(CollectionMigrations.CurrentVersion, await database.GetCollection<BsonDocument>("schemaMigrations")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await mongo.Client.DropDatabaseAsync(name, cleanup.Token);
        }
    }

    [Fact]
    public async Task Persistence_conflict_is_stable_redacted_409()
    {
        using var factory = ThrowingFactory(() =>
            throw new PersistenceConflictException("version_conflict", "private conflict detail"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(text);
        Assert.Equal("version_conflict", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private conflict detail", text);
    }

    [Fact]
    public async Task Uncertain_commit_is_safe_503_and_requests_same_key_retry()
    {
        using var factory = ThrowingFactory(() =>
            throw new UncertainCommitException("private commit detail",
                new InvalidOperationException("private inner detail")));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/system/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString() ??
            response.Headers.GetValues("Retry-After").Single());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(text);
        Assert.Equal("commit_outcome_unknown", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains("same idempotency key", problem.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("private commit detail", text);
        Assert.DoesNotContain("private inner detail", text);
    }

    [Fact]
    public void Persistence_services_share_connection_and_test_clock()
    {
        var fixedClock = new FixedClock(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        using var factory = Factory(mongo.DatabaseName).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(fixedClock);
            }));
        var services = factory.Services;
        var connection = services.GetRequiredService<MongoConnection>();
        Assert.Same(connection.Client, services.GetRequiredService<MongoClient>());
        Assert.Same(connection.Database, services.GetRequiredService<IMongoDatabase>());
        Assert.Same(fixedClock, services.GetRequiredService<IClock>());
        services.GetRequiredService<MongoUnitOfWork>();
        services.GetRequiredService<IdAllocator>();
        services.GetRequiredService<IdempotencyStore>();
        services.GetRequiredService<OutboxStore>();
        services.GetRequiredService<CollectionMigrations>();
    }

    [Fact]
    public async Task Actual_value_type_dto_has_string_openapi_and_strict_http_json()
    {
        using var factory = Factory(mongo.DatabaseName).WithWebHostBuilder(web =>
            web.ConfigureTestServices(services => services.AddTransient<IStartupFilter, NumericDtoEndpointFilter>()));
        using var client = factory.CreateClient();
        var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        using var document = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        var bodySchema = document.RootElement.GetProperty("paths").GetProperty("/__test/numeric")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        var properties = ResolveSchema(document.RootElement, bodySchema).GetProperty("properties");
        AssertNumericStringSchema(document.RootElement, properties.GetProperty("id"), 19, "Int64");
        AssertNumericStringSchema(document.RootElement, properties.GetProperty("money"), 18, "two");
        AssertNumericStringSchema(document.RootElement, properties.GetProperty("signedMoney"), 19, "two");
        var idPattern = ResolveSchema(document.RootElement, properties.GetProperty("id"))
            .GetProperty("pattern").GetString()!;
        Assert.Matches(idPattern, "1");
        Assert.Matches(idPattern, "9007199254740993");
        Assert.Matches(idPattern, long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotMatch(idPattern, "9223372036854775808");
        Assert.DoesNotMatch(idPattern, "9999999999999999999");
        Assert.DoesNotMatch(idPattern, "01");
        Assert.DoesNotMatch(idPattern, "1\n");
        var moneyPattern = ResolveSchema(document.RootElement, properties.GetProperty("money"))
            .GetProperty("pattern").GetString()!;
        var signedPattern = ResolveSchema(document.RootElement, properties.GetProperty("signedMoney"))
            .GetProperty("pattern").GetString()!;
        Assert.Matches(moneyPattern, "1.00");
        Assert.DoesNotMatch(moneyPattern, "1.00\n");
        Assert.Matches(signedPattern, "-1.00");
        Assert.DoesNotMatch(signedPattern, "-1.00\n");

        foreach (var json in new[]
        {
            "{\"id\":123,\"money\":\"1.00\",\"signedMoney\":\"-1.00\"}",
            "{\"id\":\"1\",\"money\":1.00,\"signedMoney\":\"-1.00\"}",
            "{\"id\":\"1\",\"money\":\"1.001\",\"signedMoney\":\"-1.00\"}"
        })
        {
            var response = await client.PostAsync("/__test/numeric",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var valid = await client.PostAsync("/__test/numeric", new StringContent(
            "{\"id\":\"9007199254740993\",\"money\":\"1.00\",\"signedMoney\":\"-1.00\"}",
            System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var output = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, output.RootElement.GetProperty("id").ValueKind);
        Assert.Equal("9007199254740993", output.RootElement.GetProperty("id").GetString());
    }

    private static JsonElement ResolveSchema(JsonElement document, JsonElement schema)
    {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;
        var name = reference.GetString()!.Split('/').Last();
        return document.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    private static void AssertNumericStringSchema(JsonElement document, JsonElement schema,
        int maxLength, string descriptionFragment)
    {
        var resolved = ResolveSchema(document, schema);
        Assert.Equal("string", resolved.GetProperty("type").GetString());
        Assert.Equal(maxLength, resolved.GetProperty("maxLength").GetInt32());
        Assert.StartsWith("^", resolved.GetProperty("pattern").GetString());
        Assert.Contains(descriptionFragment, resolved.GetProperty("description").GetString());
    }

    private WebApplicationFactory<Program> ThrowingFactory(Func<SystemStatus> action) =>
        Factory(mongo.DatabaseName).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.AddAuthentication("TestActor")
                .AddScheme<AuthenticationSchemeOptions, TestActorHandler>("TestActor", _ => { });
            services.RemoveAll<ISystemStatusProvider>();
            services.AddSingleton<ISystemStatusProvider>(new ThrowingStatusProvider(action));
        }));

    private static async Task AssertUnavailable(HttpClient client)
    {
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dependency_unavailable", problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task RunMigrationWorkerAsync(string databaseName)
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "MigrationWorker", "UnitToko.Worker.dll");
        if (!File.Exists(worker)) throw new FileNotFoundException("Build the test project to copy its migration worker.", worker);
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent;
        var alongsideRuntime = Path.Combine(runtimeRoot?.FullName ?? "", executableName);
        var dotnet = !string.IsNullOrEmpty(configured) && File.Exists(configured) &&
            string.Equals(Path.GetFileName(configured), executableName, StringComparison.OrdinalIgnoreCase)
            ? configured : File.Exists(alongsideRuntime) ? alongsideRuntime :
                throw new InvalidOperationException("Cannot locate dotnet beside the active test runtime.");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(worker)!
        };
        start.ArgumentList.Add(worker);
        start.ArgumentList.Add("--migrate");
        start.Environment["Mongo__Uri"] = "mongodb://127.0.0.1:27028/?replicaSet=rs0";
        start.Environment["Mongo__Database"] = databaseName;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start migration worker.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(process.ExitCode == 0,
                $"Migration worker exited {process.ExitCode}: {await output}; {await errors}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}

internal sealed record NumericDto(LongId Id, Money Money, SignedMoney SignedMoney);

internal sealed class NumericDtoEndpointFilter : IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
        Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        next(app);
        app.UseEndpoints(endpoints => endpoints.MapPost("/__test/numeric", (NumericDto dto) =>
            TypedResults.Ok(dto)).AllowAnonymous());
    };
}
