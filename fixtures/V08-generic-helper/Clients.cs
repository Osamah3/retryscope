namespace RetryScope.Fixtures.V08;

/// <summary>Minimal typed clients.</summary>
public sealed class CatalogClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}

/// <summary>Minimal typed client.</summary>
public sealed class OrdersClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
