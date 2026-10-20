namespace UnitToko.Tests;

/// <summary>Reasons for tests retired in Phase E (docs/quality/retired-tests.md lists every one with the test that covers the current path).</summary>
internal static class Retired
{
    /// <summary>The test rewinds a fully migrated fixture to the schema it was written against (for example "history.Count == 59").
    /// Fixtures now migrate to the current schema, which later migrations cannot be rewound from, and no deployable database holds
    /// that intermediate schema: the first real deployment migrates an empty database to the current version.</summary>
    internal const string HistoricalUpgrade =
        "Retired: historical upgrade path from an intermediate schema no deployable database holds (docs/quality/retired-tests.md).";
}

/// <summary>Open test debt, not a retirement: the guarantee still applies to deployable states and the test must be rewritten.</summary>
internal static class Pending
{
    /// <summary>Since 7f8541b the shared procurement fixtures set the stock source up before the purchase order and bind the order
    /// to it, so the unbound or source-after-order states these tests were written against no longer exist. The product now
    /// refuses those reads fail-closed (for example missing causal coverage membership); the tests need an explicit fixture path.</summary>
    internal const string FixtureRewrite =
        "Pending rewrite: written against the procurement fixture state before 7f8541b; open test debt, not retired (docs/quality/retired-tests.md).";
}
