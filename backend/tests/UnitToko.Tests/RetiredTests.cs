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

    /// <summary>The scenario is correct but a command inside it still re-proves procurement history in full and exceeds the
    /// 60 s Mongo transaction bound on the two-order fixture, even sealed. Tracked by the latency workstream (next-wave plan E).</summary>
    internal const string TransactionLatency =
        "Pending latency: a command in this two-order scenario exceeds the 60 s transaction bound; open, not retired (docs/quality/retired-tests.md).";

    /// <summary>The browser flow passes every step up to a command that still takes longer than the 30 s browser request bound.
    /// Same cause and workstream as <see cref="TransactionLatency"/>; the bound itself is not relaxed.</summary>
    internal const string BrowserLatency =
        "Pending latency: a command in this browser flow exceeds the 30 s browser request bound; open, not retired (docs/quality/retired-tests.md).";

    /// <summary>The customer side attaches an order fixture to a procurement fixture that has already received physical stock into the
    /// same warehouse. Activating that pool for orders is correctly refused (existing stock needs its own reviewed migration and valuation
    /// handoff), so the order fixture needs its own warehouse before these scenarios can run again.</summary>
    internal const string SharedWarehouseFixture =
        "Pending rewrite: the order fixture reuses a warehouse the procurement fixture already stocked; open test debt, not retired (docs/quality/retired-tests.md).";
}
