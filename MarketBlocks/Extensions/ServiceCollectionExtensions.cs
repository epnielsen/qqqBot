using MarketBlocks.Core.Interfaces;
using MarketBlocks.Infrastructure.Tradier;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketBlocks.Extensions;

/// <summary>
/// Extension methods for registering MarketBlocks services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Tradier brokerage services to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration containing "Tradier" section.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTradier(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Bind TradierOptions from configuration
        var tradierSection = configuration.GetSection("Tradier");
        services.Configure<TradierOptions>(tradierSection);
        
        // Get options for HttpClient configuration
        var options = new TradierOptions();
        tradierSection.Bind(options);
        
        // Register named HttpClient for Tradier REST API
        services.AddHttpClient<TradierExecutionAdapter>("TradierExecution", client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        
        // Register named HttpClient for Tradier Streaming
        services.AddHttpClient<TradierSourceAdapter>("TradierStreaming", client =>
        {
            // Streaming client needs longer timeout for persistent connections
            client.Timeout = TimeSpan.FromMilliseconds(-1); // Infinite timeout for streaming
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        
        // Register TradierExecutionAdapter as IBrokerExecution (Scoped - one per request/operation)
        services.AddScoped<IBrokerExecution, TradierExecutionAdapter>();
        
        // Register TradierSourceAdapter as IMarketDataSource (Singleton - maintains connection state)
        services.AddSingleton<IMarketDataSource, TradierSourceAdapter>();
        
        return services;
    }
    
    /// <summary>
    /// Adds Tradier brokerage services with custom options configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure TradierOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTradier(
        this IServiceCollection services, 
        Action<TradierOptions> configureOptions)
    {
        // Configure options via action
        services.Configure(configureOptions);
        
        // Build options to get base URL
        var options = new TradierOptions();
        configureOptions(options);
        
        // Register named HttpClient for Tradier REST API
        services.AddHttpClient<TradierExecutionAdapter>("TradierExecution", client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        
        // Register named HttpClient for Tradier Streaming
        services.AddHttpClient<TradierSourceAdapter>("TradierStreaming", client =>
        {
            client.Timeout = TimeSpan.FromMilliseconds(-1);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });
        
        // Register adapters
        services.AddScoped<IBrokerExecution, TradierExecutionAdapter>();
        services.AddSingleton<IMarketDataSource, TradierSourceAdapter>();
        
        return services;
    }
}
