using Lakeflow.Contracts;
using Lakeflow.Testing;
using Lakeflow.Testing.InMemory;

namespace Lakeflow.Contracts.Tests;

// The contract suite run against the reference in-memory connector, in both styles it simulates.
// This proves the suite itself before real connectors are held to it.

public class InMemoryChangeTrackingContractTests : ConnectorContractTests
{
    protected override Task<IContractTestBed> CreateBedAsync() =>
        Task.FromResult<IContractTestBed>(new InMemoryTestBed(CaptureMethod.ChangeTracking));
}

public class InMemoryRowVersionContractTests : ConnectorContractTests
{
    protected override Task<IContractTestBed> CreateBedAsync() =>
        Task.FromResult<IContractTestBed>(new InMemoryTestBed(CaptureMethod.RowVersion));
}
