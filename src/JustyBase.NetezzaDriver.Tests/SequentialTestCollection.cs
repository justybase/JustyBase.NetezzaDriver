using Xunit;

namespace JustyBase.NetezzaDriver.Tests;

/// <summary>
/// Collection definition to ensure all tests run sequentially
/// </summary>
[CollectionDefinition("Sequential")]
public class SequentialTestCollection : ICollectionFixture<SequentialTestFixture>
{
}

/// <summary>
/// Fixture for sequential test collection
/// </summary>
public class SequentialTestFixture
{
}