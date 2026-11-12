# Development set

Repositories used to find and fix extractor gaps on real code. **They are excluded from the evaluation corpus**, so
accuracy reported on the corpus is not inflated by tuning on them. Recorded before any extractor output on real code
was seen (12 Nov 2026).

| Repository | Commit | Projects run |
| --- | --- | --- |
| dotnet/eShop | 913acea (2026-10-15) | `src/WebApp/WebApp.csproj`, `src/WebhookClient/WebhookClient.csproj` |
| dotnet/aspire-samples | 4b20948 (2026-11-11) | `samples/aspire-shop/AspireShop.Frontend`, `samples/health-checks-ui/HealthChecksUI.Web`, `samples/Metrics/MetricsApp.Client` |

Why these: they are Microsoft's reference Aspire applications, so they are the code the authors (and most readers) have
already seen; they could not be labelled blind anyway.

## Findings

Each finding: what the extractor reported, what the code does, and what changed (fixture, extractor, or documented
limitation). Gaps fixed here get a new fixture so the regression suite keeps them fixed.

Run 1 (12 Nov 2026, extractor at commit after slice 3 + CLI). All five projects built and loaded.

| # | Repository / project | Observation | Verified cause | Resolution |
| --- | --- | --- | --- | --- |
| D1 | all five | RS008 *errors* CS0234/CS0246 for `Components`, `App` | Razor (`.razor`) source-generator output is not in the workspace compilation; the failing code (`MapRazorComponents<App>()`, `using ...Components`) registers no clients | Compiler errors become warnings with a count; a new warning flags any *registration-path* call that does not bind; Error is kept for load failures |
| D2 | eShop (both) | RS009: references Http.Resilience 10.10.0, defaults verified for 10.11.0 | `src/Libraries/Microsoft.Extensions.Http.Resilience` is identical between tags v10.10.0 (294665a) and v10.11.0 (zero diff) | Verified versions = {10.10, 10.11}; recorded in verified-defaults.md |
| D3 | eShop, health-checks-ui | Undecidable-condition warnings for `useOtlpExporter`, endpoint handlers | Warning emitted for every call under a condition, even when nothing is registered there | Warn only when a registration happens under the condition |
| D4 | aspire-shop Frontend | Client named `TClient`, target unresolved | Registered via generic helper `AddHttpServiceReference<TClient>(string baseAddress)`; the extractor did not substitute the caller's type argument or constant argument | Call context: substitute type arguments and arguments when following a helper; new fixture |
| D5 | health-checks-ui Web | Target unresolved | `var apiServiceUri = new Uri("https+http://apiservice"); ... BaseAddress = apiServiceUri` | Resolve locals with a single initializer and no reassignment; new fixture |
| D6 | Metrics Client | Client name printed blank | `AddHttpClient("", ...)`: the empty name is the default client (`Options.DefaultName`) | Kind `default`, shown as `(default)` |
| D7 | eShop WebApp | Basket client absent, silently | `AddGrpcClient<Basket.BasketClient>` (gRPC; out of V1 scope, ADR 0002), although ConfigureHttpClientDefaults handlers also apply to it | Info diagnostic so excluded gRPC clients are visible |

All seven resolved in extractor slice 4; D4 and D5 have regression fixtures V08 and V09.

Correct on first run: eShop `CatalogService`, `OrderingService`, `WebhooksClient` (typed, Aspire service-discovery
targets `catalog-api`, `ordering-api`, `webhooks-api`, defaults handler traced into eShop.ServiceDefaults, custom
`AddApiVersion`/`AddAuthToken` builder extensions followed); bounds 4/4 match the source.

Run 2 (12 Nov 2026, after slice 4): all seven findings confirmed fixed on the real code. aspire-shop
`CatalogServiceClient` → target `catalogservice` (D4); health-checks-ui `WeatherApiClient` → `apiservice` (D5); Metrics
client shown as `(default)` with target unresolved, which is correct (its address is `builder.HostEnvironment.BaseAddress`,
a runtime value); gRPC clients reported as RS011 (eShop Basket; aspire-shop Basket and Health); compiler errors summarised
as one warning, and no RS010 was raised, so no registration-path call failed to bind.

