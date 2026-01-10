using Xunit;
using MarketBlocks.Infrastructure.Tradier; // Logic to be implemented

namespace MarketBlocks.Tests.Infrastructure.Tradier;

public class TradierIdMappingTests
{
    [Fact]
    public void MapToGuid_AndBack_PreservesId()
    {
        // Arrange
        int originalTradierId = 123456789;

        // Act
        // We expect the implementation to have a static helper or internal logic for this
        Guid mappedGuid = TradierIdHelper.ToGuid(originalTradierId);
        int recoveredId = TradierIdHelper.ToTradierId(mappedGuid);

        // Assert
        Assert.Equal(originalTradierId, recoveredId);
        Assert.NotEqual(Guid.Empty, mappedGuid);
    }

    [Fact]
    public void MapToGuid_DifferentIds_ProduceDifferentGuids()
    {
        // Arrange
        int id1 = 1001;
        int id2 = 1002;

        // Act
        Guid guid1 = TradierIdHelper.ToGuid(id1);
        Guid guid2 = TradierIdHelper.ToGuid(id2);

        // Assert
        Assert.NotEqual(guid1, guid2);
    }
}