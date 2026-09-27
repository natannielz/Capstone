var builder = Host.CreateApplicationBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["Mongo:Uri"]) ||
    string.IsNullOrWhiteSpace(builder.Configuration["Mongo:Database"]))
    throw new InvalidOperationException("Mongo:Uri and Mongo:Database are required.");

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("UnitToko.Worker")
    .LogWarning("No outbox processors are registered; external delivery is disabled.");
host.Run();
