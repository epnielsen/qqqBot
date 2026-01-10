using qqqBot.Core.Domain;

namespace qqqBot.Core.Interfaces;

/// <summary>
/// Broker-agnostic interface for order execution.
/// Implementations adapt to specific brokers (Alpaca, IBKR, etc.).
/// </summary>
public interface IBrokerExecution
{
    /// <summary>
    /// Submits an order to the broker.
    /// </summary>
    /// <param name="request">Order request details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The submitted order with broker-assigned ID.</returns>
    Task<BotOrder> SubmitOrderAsync(BotOrderRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current status of an order.
    /// </summary>
    /// <param name="orderId">Broker-assigned order ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Current order state.</returns>
    Task<BotOrder> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Cancels an open order.
    /// </summary>
    /// <param name="orderId">Broker-assigned order ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if cancel request was accepted.</returns>
    Task<bool> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current position for a symbol.
    /// </summary>
    /// <param name="symbol">Ticker symbol.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Position details, or null if no position.</returns>
    Task<BotPosition?> GetPositionAsync(string symbol, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets all current positions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all positions.</returns>
    Task<IReadOnlyList<BotPosition>> GetAllPositionsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the current account buying power.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Available buying power in account currency.</returns>
    Task<decimal> GetBuyingPowerAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets the latest trade price for a symbol.
    /// </summary>
    /// <param name="symbol">Ticker symbol.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Latest trade price.</returns>
    Task<decimal> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Validates that a symbol is tradable.
    /// </summary>
    /// <param name="symbol">Ticker symbol.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the symbol can be traded.</returns>
    Task<bool> ValidateSymbolAsync(string symbol, CancellationToken cancellationToken = default);
}
