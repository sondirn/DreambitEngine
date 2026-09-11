using Xunit;

// Networking integration tests share Dreambit's process-wide runtime asset cache.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
