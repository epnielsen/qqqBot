namespace MarketBlocks.Infrastructure.Tradier;

/// <summary>
/// Configuration options for Tradier brokerage integration.
/// </summary>
public sealed class TradierOptions
{
    /// <summary>
    /// Tradier API access token.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;
    
    /// <summary>
    /// Tradier account ID for trading operations.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether to use the sandbox (paper trading) environment.
    /// </summary>
    public bool UseSandbox { get; set; } = true;
    
    /// <summary>
    /// Gets the base URL for API requests based on environment.
    /// </summary>
    public string BaseUrl => UseSandbox 
        ? "https://sandbox.tradier.com/v1" 
        : "https://api.tradier.com/v1";
    
    /// <summary>
    /// Gets the base URL for streaming data.
    /// </summary>
    public string StreamUrl => "https://stream.tradier.com/v1";
}
