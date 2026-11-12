# Extractor scope (V1)

What `RetryScope.Extraction` recognises, the semantic decisions it makes, and what it does not do. Every decision
below is backed by library source (docs/verified-defaults.md) or by the behaviour oracle (docs/findings.md). Anything
outside this list produces a diagnostic, never a silent guess.

## Recognised

| Pattern | API | Notes |
| --- | --- | --- |
| P1 | `ConfigureHttpClientDefaults(h => h.AddStandardResilienceHandler())`, incl. Aspire `AddServiceDefaults()` | Followed into ServiceDefaults source |
| P2 | `AddStandardResilienceHandler()` | |
| P3 | options lambda: `o.Path = value;`, `o.Retry.DisableForUnsafeHttpMethods()`, `o.Retry.DisableFor(HttpMethod.X, ...)` | Literals, `const` fields, `TimeSpan.FromX`, `new TimeSpan`, enum members |
| P4 | `AddStandardResilienceHandler(config.GetSection("A").GetSection("B"))`, chained `.Configure(section)` | appsettings.json + appsettings.{Environment}.json |
| P5 | `AddResilienceHandler(name, b => b.AddRetry(...).AddTimeout(...)...)` | retry, timeout, circuit breaker, concurrency limiter |
| P6 | stacking: defaults + per-client, or several per-client handlers | Defaults are outermost (F1) |
| P7 | `RemoveAllResilienceHandlers()` | See semantics below |
| P8 | `DisableForUnsafeHttpMethods` | DELETE, POST, PUT, CONNECT, PATCH |
| P9 | custom `ShouldHandle` | Recorded as opaque |
| P11 | `AddStandardHedgingHandler()` | Detected (RS001); bound unknown |
| P12 | Polly v7 `AddTransientHttpErrorPolicy` / `AddPolicyHandler` with `RetryAsync`/`WaitAndRetryAsync` | Literal count only (RS002) |
| V01 | `new HttpClient(...)` anywhere in the project | Direct client, no factory handlers (RS003) |
| V02 | typed clients `AddHttpClient<T>()` | Named after the short type name |
| V03 | helper methods with source in the solution | Followed up to depth 8 |
| V04 | `if (env.IsProduction())`, `IsDevelopment`, `IsStaging`, `IsEnvironment("X")`, `EnvironmentName == "X"`, `!`, `&&`, `\|\|`, `?:` | Decided for the configured environment |
| — | builder held in a local: `var b = services.AddHttpClient(...); b.AddStandardResilienceHandler();` | Initializer only |

## Semantic decisions

1. **Stacking order.** Handlers from `ConfigureHttpClientDefaults` wrap per-client handlers regardless of call order
   (F1). Among per-client handlers, the first registered is outermost.
2. **Removal.** `RemoveAllResilienceHandlers()` runs as a `ConfigureAdditionalHttpMessageHandlers` action that deletes
   every `ResilienceHandler` added before it. Defaults' actions run first, so the defaults are removed too; handlers
   registered after the call survive. Polly v7 `PolicyHttpMessageHandler`s are not `ResilienceHandler`s and survive.
3. **Custom pipeline timeouts.** In Polly v8 the first strategy added is outermost. A timeout added after a retry runs
   inside it and bounds each attempt (`attempt`); a timeout added before any retry bounds the whole call (`total`).
4. **Default predicates differ by options type.** `HttpRetryStrategyOptions` / `HttpCircuitBreakerStrategyOptions` handle
   HTTP transient outcomes (5xx, 408, 429, `HttpRequestException`, `TimeoutRejectedException`). Plain Polly
   `RetryStrategyOptions<HttpResponseMessage>` handles **exceptions only** (`ExceptionsOnly`): it does not retry a
   503 response. Its backoff is constant and jitter is off.
5. **Conditions.** Only host-environment checks are decided (default environment: Production, as the oracle and the
   labelling guide use). A branch ruled out is skipped, but the client is still marked `conditional`. A condition
   that cannot be decided keeps the registrations in **both** branches, marks the client conditional, and warns. In an
   if/else, this can over-count stacking; the warning makes it visible.
6. **Configuration.** Only `appsettings.json` and `appsettings.{Environment}.json` are read, with the runtime's own
   configuration library. Environment variables, command-line arguments, user secrets and other providers are not
   visible statically; every config-bound value's note says so.
7. **Unresolved values.** A modelled option set to something non-constant keeps its previous value as a fallback with
   provenance `unresolved`. Options the model does not represent are reported as `Info` and ignored.

## Known gaps (reported, not hidden)

| Fixture | Gap | Effect |
| --- | --- | --- |
| P10 | A keyed pipeline (`AddResiliencePipeline`) executed manually inside a typed client | Client reported with no resilience; the bound is under-counted |
| P13 | A hand-written retry loop around `HttpClient.SendAsync` | Same |

Both need data-flow analysis inside the typed client's methods. Their frequency in the real-repository corpus is
measured and reported as the recall of those strata; if either proves common, it is a candidate for V2.

Other limitations: reassigned locals (only the initializer is tracked), conditions in `switch` statements and loops
(not treated as conditional), `ConfigureHttpClientDefaults` with a method group, options configured via
`services.Configure<HttpStandardResilienceOptions>(...)` named options, and hedging options.
