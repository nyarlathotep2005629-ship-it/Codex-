using Xunit;

// DatabaseTestFixture clears SQLite's process-wide connection pools at disposal. Running
// independent fixture tests concurrently can therefore dispose a pool another query is using.
// Keep all real-SQLite regressions deterministic without removing any test coverage.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
