namespace RetryScope.Fixtures.V02;

/// <summary>Minimal typed client.</summary>
public sealed class CatalogClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
