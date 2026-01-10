using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Alpaca.Markets;
using qqqBot.Core.Domain;
using qqqBot.Infrastructure.Alpaca;

namespace qqqBot.Tests.Infrastructure;

/// <summary>
/// Tests for AlpacaExecutionAdapter - verifies correct mapping and optimization paths.
/// </summary>
public class AlpacaAdapterTests
{
    /// <summary>
    /// Test 1: Verify BotOrderRequest fields are correctly mapped to NewOrderRequest.
    /// We verify this by checking that the mock received the correct parameters.
    /// </summary>
    [Fact]
    public async Task SubmitOrder_MapsRequestCorrectly()
    {
        // Arrange
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        // Capture the order that was submitted - verify Symbol, LimitPrice, ClientOrderId
        mockClient
            .Setup(c => c.PostOrderAsync(It.Is<NewOrderRequest>(req => 
                req.Symbol == "TQQQ" &&
                req.LimitPrice == 100.50m &&
                req.ClientOrderId == "test-order-123"), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockOrder(OrderStatus.Filled, 100.50m, 100))
            .Verifiable();
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        var request = BotOrderRequest.IocLimitBuy("TQQQ", 100, 100.50m, "test-order-123");
        
        // Act
        var result = await adapter.SubmitOrderAsync(request);
        
        // Assert - Verify the mock was called with correct parameters
        mockClient.Verify();
        
        // Also verify the result mapping
        Assert.Equal(BotOrderStatus.Filled, result.Status);
        Assert.Equal("TQQQ", result.Symbol);
    }
    
    /// <summary>
    /// Test 2: Optimized path - when order returns Filled immediately, GetOrderAsync should NOT be called.
    /// This is the critical optimization for low-latency trading.
    /// </summary>
    [Fact]
    public async Task SubmitOrder_OptimizedPath_NoGetOrderCallWhenFilled()
    {
        // Arrange
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        // Setup PostOrderAsync to return a FILLED order immediately
        mockClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockOrder(OrderStatus.Filled, 100.50m, 100, 100.50m));
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        var request = BotOrderRequest.MarketBuy("TQQQ", 100);
        
        // Act
        var result = await adapter.SubmitOrderAsync(request);
        
        // Assert
        Assert.Equal(BotOrderStatus.Filled, result.Status);
        Assert.Equal(100, result.FilledQuantity);
        Assert.Equal(100.50m, result.AverageFillPrice);
        
        // CRITICAL: Verify GetOrderAsync was NEVER called
        mockClient.Verify(
            c => c.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), 
            Times.Never,
            "GetOrderAsync should NOT be called when PostOrderAsync returns a terminal status (Filled)");
    }
    
    /// <summary>
    /// Test 2b: Optimized path also works for Canceled orders (IOC that didn't fill).
    /// </summary>
    [Fact]
    public async Task SubmitOrder_OptimizedPath_NoGetOrderCallWhenCanceled()
    {
        // Arrange
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        // Setup PostOrderAsync to return a CANCELED order (IOC that didn't match)
        mockClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockOrder(OrderStatus.Canceled, 100.50m, 0));
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        var request = BotOrderRequest.IocLimitBuy("TQQQ", 100, 100.50m);
        
        // Act
        var result = await adapter.SubmitOrderAsync(request);
        
        // Assert
        Assert.Equal(BotOrderStatus.Canceled, result.Status);
        Assert.Equal(0, result.FilledQuantity);
        
        // CRITICAL: Verify GetOrderAsync was NEVER called
        mockClient.Verify(
            c => c.GetOrderAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), 
            Times.Never,
            "GetOrderAsync should NOT be called when PostOrderAsync returns Canceled status");
    }
    
    /// <summary>
    /// Test 3: Slow path - when order returns New, caller can poll GetOrderAsync for updates.
    /// This verifies the adapter correctly returns non-terminal orders.
    /// </summary>
    [Fact]
    public async Task SubmitOrder_SlowPath_ReturnsNewOrderForPolling()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        // Setup PostOrderAsync to return a NEW order (not yet filled)
        mockClient
            .Setup(c => c.PostOrderAsync(It.IsAny<NewOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockOrder(OrderStatus.New, 100.50m, 0, orderId: orderId));
        
        // Setup GetOrderAsync for when caller polls for status
        mockClient
            .Setup(c => c.GetOrderAsync(orderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMockOrder(OrderStatus.Filled, 100.50m, 100, 100.50m, orderId));
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        var request = BotOrderRequest.MarketBuy("TQQQ", 100);
        
        // Act - Submit order
        var initialResult = await adapter.SubmitOrderAsync(request);
        
        // Assert - Initial result is non-terminal
        Assert.Equal(BotOrderStatus.New, initialResult.Status);
        Assert.False(initialResult.IsTerminal);
        Assert.Equal(0, initialResult.FilledQuantity);
        
        // Act - Caller polls for update
        var finalResult = await adapter.GetOrderAsync(orderId);
        
        // Assert - Final result is filled
        Assert.Equal(BotOrderStatus.Filled, finalResult.Status);
        Assert.True(finalResult.IsTerminal);
        Assert.Equal(100, finalResult.FilledQuantity);
    }
    
    /// <summary>
    /// Test 4: Verify sell order mapping works correctly.
    /// </summary>
    [Fact]
    public async Task SubmitOrder_SellOrderMapsCorrectly()
    {
        // Arrange
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        // Verify sell order parameters (check Symbol only, OrderQuantity is complex type)
        mockClient
            .Setup(c => c.PostOrderAsync(It.Is<NewOrderRequest>(req => 
                req.Symbol == "SQQQ"), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var mock = new Mock<IOrder>();
                mock.Setup(o => o.OrderId).Returns(Guid.NewGuid());
                mock.Setup(o => o.Symbol).Returns("SQQQ");
                mock.Setup(o => o.OrderSide).Returns(OrderSide.Sell);
                mock.Setup(o => o.OrderType).Returns(OrderType.Market);
                mock.Setup(o => o.OrderStatus).Returns(OrderStatus.Filled);
                mock.Setup(o => o.Quantity).Returns(50);
                mock.Setup(o => o.FilledQuantity).Returns(50);
                mock.Setup(o => o.AverageFillPrice).Returns(101.00m);
                return mock.Object;
            })
            .Verifiable();
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        var request = BotOrderRequest.MarketSell("SQQQ", 50);
        
        // Act
        var result = await adapter.SubmitOrderAsync(request);
        
        // Assert
        mockClient.Verify();
        
        Assert.Equal(BotOrderSide.Sell, result.Side);
        Assert.Equal(50, result.FilledQuantity);
        Assert.Equal("SQQQ", result.Symbol);
    }
    
    /// <summary>
    /// Test 5: Verify GetAllPositionsAsync maps positions correctly.
    /// </summary>
    [Fact]
    public async Task GetAllPositions_MapsPositionsCorrectly()
    {
        // Arrange
        var mockClient = new Mock<IAlpacaTradingClient>();
        
        var mockPosition1 = new Mock<IPosition>();
        mockPosition1.Setup(p => p.Symbol).Returns("TQQQ");
        mockPosition1.Setup(p => p.Quantity).Returns(100);
        mockPosition1.Setup(p => p.AverageEntryPrice).Returns(80.50m);
        mockPosition1.Setup(p => p.AssetCurrentPrice).Returns(85.00m);
        mockPosition1.Setup(p => p.MarketValue).Returns(8500m);
        
        var mockPosition2 = new Mock<IPosition>();
        mockPosition2.Setup(p => p.Symbol).Returns("SQQQ");
        mockPosition2.Setup(p => p.Quantity).Returns(50);
        mockPosition2.Setup(p => p.AverageEntryPrice).Returns(12.25m);
        mockPosition2.Setup(p => p.AssetCurrentPrice).Returns(12.00m);
        mockPosition2.Setup(p => p.MarketValue).Returns(600m);
        
        mockClient
            .Setup(c => c.ListPositionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mockPosition1.Object, mockPosition2.Object });
        
        var adapter = new AlpacaExecutionAdapter(mockClient.Object);
        
        // Act
        var positions = await adapter.GetAllPositionsAsync();
        
        // Assert
        Assert.Equal(2, positions.Count);
        
        var tqqq = positions.First(p => p.Symbol == "TQQQ");
        Assert.Equal(100, tqqq.Quantity);
        Assert.Equal(80.50m, tqqq.AverageEntryPrice);
        Assert.Equal(85.00m, tqqq.CurrentPrice);
        Assert.True(tqqq.IsLong);
        
        var sqqq = positions.First(p => p.Symbol == "SQQQ");
        Assert.Equal(50, sqqq.Quantity);
        Assert.Equal(12.25m, sqqq.AverageEntryPrice);
    }
    
    /// <summary>
    /// Helper to create mock IOrder objects.
    /// </summary>
    private static IOrder CreateMockOrder(
        OrderStatus status, 
        decimal limitPrice, 
        long filledQty,
        decimal? avgFillPrice = null,
        Guid? orderId = null)
    {
        var mockOrder = new Mock<IOrder>();
        mockOrder.Setup(o => o.OrderId).Returns(orderId ?? Guid.NewGuid());
        mockOrder.Setup(o => o.ClientOrderId).Returns("test-client-order");
        mockOrder.Setup(o => o.Symbol).Returns("TQQQ");
        mockOrder.Setup(o => o.OrderSide).Returns(OrderSide.Buy);
        mockOrder.Setup(o => o.OrderType).Returns(OrderType.Limit);
        mockOrder.Setup(o => o.OrderStatus).Returns(status);
        mockOrder.Setup(o => o.Quantity).Returns(100);
        mockOrder.Setup(o => o.FilledQuantity).Returns(filledQty);
        mockOrder.Setup(o => o.AverageFillPrice).Returns(avgFillPrice);
        mockOrder.Setup(o => o.LimitPrice).Returns(limitPrice);
        mockOrder.Setup(o => o.SubmittedAtUtc).Returns(DateTime.UtcNow);
        mockOrder.Setup(o => o.FilledAtUtc).Returns(filledQty > 0 ? DateTime.UtcNow : null);
        return mockOrder.Object;
    }
}
