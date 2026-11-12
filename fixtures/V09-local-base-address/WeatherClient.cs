namespace RetryScope.Fixtures.V09;

/// <summary>Minimal typed client.</summary>
public sealed class WeatherClient(HttpClient http)
{
    public HttpClient Http { get; } = http;
}
