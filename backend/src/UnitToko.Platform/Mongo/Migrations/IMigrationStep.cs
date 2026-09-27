namespace UnitToko.Platform.Mongo.Migrations;

internal interface IMigrationStep
{
    int Version { get; }
    Task ApplyAsync(MigrationLease lease, CancellationToken cancellationToken);
}