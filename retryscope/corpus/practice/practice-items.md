# Practice items (labelling guide v1)

Label each item with the guide's fields before opening `practice-answers.md`. These items are synthetic and are
**not** part of the evaluation sample.

Assume every service's `Program.cs` shown is complete for HttpClient registration purposes, and that
ServiceDefaults `Extensions.cs` is exactly as shown when it appears.

---

## PI-1 — Aspire defaults only

```csharp
// ServiceDefaults/Extensions.cs (excerpt)
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddStandardResilienceHandler();
    http.AddServiceDiscovery();
});

// Store.Api/Program.cs
builder.AddServiceDefaults();
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new("https+http://catalog"));
```

## PI-2 — Defaults plus a tuned per-client handler

```csharp
// ServiceDefaults/Extensions.cs: same as PI-1

// Orders.Api/Program.cs
builder.AddServiceDefaults();
builder.Services.AddHttpClient("payments", c => c.BaseAddress = new("https+http://payments"))
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 5;
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
    });
```

## PI-3 — Unsafe methods excluded

```csharp
// Basket.Api/Program.cs (no ServiceDefaults)
builder.Services.AddHttpClient<OrderingClient>(c => c.BaseAddress = new("http://ordering"))
    .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
```

## PI-4 — Removed defaults, custom pipeline, environment variable

```csharp
// ServiceDefaults/Extensions.cs: same as PI-1

// Gateway/Program.cs
builder.AddServiceDefaults();
var retries = int.Parse(Environment.GetEnvironmentVariable("GATEWAY_RETRIES") ?? "2");
builder.Services.AddHttpClient("inventory", c => c.BaseAddress = new("https+http://inventory"))
    .RemoveAllResilienceHandlers()
    .AddResilienceHandler("inventory-pipeline", b =>
    {
        b.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = retries });
        b.AddTimeout(TimeSpan.FromSeconds(3));
    });
```

## PI-5 — Legacy Polly v7 and a direct client

```csharp
// Legacy.Api/Program.cs (no ServiceDefaults)
builder.Services.AddHttpClient("shipping", c => c.BaseAddress = new("http://shipping"))
    .AddTransientHttpErrorPolicy(p => p.WaitAndRetryAsync(2, i => TimeSpan.FromMilliseconds(200 * i)));

// Legacy.Api/Reports/ReportJob.cs
using var client = new HttpClient { BaseAddress = new Uri("http://reports") };
var report = await client.GetStringAsync("/daily");
```
Label **two** rows for PI-5: the `shipping` client and the `new HttpClient` in `ReportJob`.
