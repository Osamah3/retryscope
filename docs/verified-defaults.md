# Verified library defaults

Every package default that RetryScope reports with provenance `packageDefault` must appear here, with the release tag
it was read from. Re-verify whenever a pinned package version changes.

## Provenance

| Library | Pinned / resolved version | Source read | Notes |
| --- | --- | --- | --- |
| Microsoft.Extensions.Http.Resilience | 10.11.0 (assembly version 10.11.0.0) | dotnet/extensions tag `v10.11.0` (25f07af) | |
| Microsoft.Extensions.Http.Resilience | 10.10.0 (assembly version 10.10.0.0) | dotnet/extensions tag `v10.10.0` (294665a) | Library folder identical to v10.11.0 (zero diff, 12 Nov 2026; dev-set D2) |
| Polly.Core, Polly.RateLimiting | 8.4.2, resolved in `packages.lock.json` (12 Nov 2026; Polly.Core contentHash `BpE2I6HB…`) | App-vNext/Polly tag `8.4.2` | Exactly the version read; constants also identical on Polly main (98eaf12) |
| Microsoft.Extensions.Http | 10.0.x | dotnet/runtime tag `v10.0.0` | API shapes only (`AddHttpClient`, `ConfigureHttpClientDefaults`, typed-client naming) |

**Correction (11 Nov 2026).** These defaults were first read from development commits (`dotnet/extensions` main
f8ee271, versioned 11.0.0; Polly main 98eaf12), not from the pinned releases. Re-reading at the release tags gave
identical values for every default below. The one difference is that extensions main adds an experimental retry
budget (`HttpRetryStrategyOptions.RetryBudget`, `UseBudgetedRetries`), which **10.11.0 does not have**. Pinned code
must not assume it exists. The pre-registration was not affected, because it had not yet been made.

## Standard resilience handler (`AddStandardResilienceHandler`)

Pipeline order, outermost first (`ResilienceHttpClientBuilderExtensions.StandardResilience.cs`): rate limiter → total
timeout → retry → circuit breaker → attempt timeout.

| Setting | Default | Defined in |
| --- | --- | --- |
| Rate limiter permits / queue | 1000 / 0 | Polly `RateLimiterConstants` |
| Total request timeout | 30 s | Polly `TimeoutStrategyOptions.Timeout` (inherited by `HttpTimeoutStrategyOptions`) |
| Retry: max retry attempts | 3 | Polly `RetryConstants.DefaultRetryCount` |
| Retry: base delay | 2 s | Polly `RetryConstants.DefaultBaseDelay` |
| Retry: backoff / jitter / Retry-After | exponential / on / honoured | extensions `HttpRetryStrategyOptions` |
| Circuit breaker | ratio 0.1, min throughput 100, sampling 30 s, break 5 s | Polly `CircuitBreakerConstants` |
| Attempt timeout | 10 s | extensions `HttpStandardResilienceOptions` |
| Transient outcomes | `HttpRequestException`, `TimeoutRejectedException`, status ≥ 500, 408, 429 | extensions `HttpClientResiliencePredicates` |

## Plain Polly v8 options (custom pipelines)

| Options type | Defaults that differ from the HTTP subclass | Defined in |
| --- | --- | --- |
| `RetryStrategyOptions<T>` | **constant** backoff, jitter **off**, predicate **exceptions only** (any exception except `OperationCanceledException`; no results) | Polly `RetryConstants`, `RetryStrategyOptions<T>`, `DefaultPredicates.HandleOutcome` |
| `CircuitBreakerStrategyOptions<T>` | same numbers (0.1, 100, 30 s, 5 s); predicate exceptions only | Polly `CircuitBreakerConstants` |
| `TimeoutStrategyOptions` | 30 s | Polly `TimeoutStrategyOptions.Timeout` |
| `AddConcurrencyLimiter(permitLimit, queueLimit = 0)` | queue 0 | Polly.RateLimiting |

`HttpRetryStrategyOptions` sets exponential backoff, jitter, `ShouldRetryAfterHeader` and the HTTP transient predicate
in its constructor; `HttpCircuitBreakerStrategyOptions` sets the HTTP transient predicate.

## Polly v7 (Microsoft.Extensions.Http.Polly)

Polly 7.2.4 `AsyncRetryTResultSyntax`: `RetryAsync()` retries once; `RetryAsync(int retryCount)`,
`WaitAndRetryAsync(int retryCount, ...)`. `AddTransientHttpErrorPolicy` handles `HttpRequestException`, 5xx and 408
(not 429). Recorded, not simulated in V1.

## Removal

`RemoveAllResilienceHandlers()` (`ResilienceHttpClientBuilderExtensions.Resilience.cs`) registers a
`ConfigureAdditionalHttpMessageHandlers` action that removes every `ResilienceHandler` added before it.

## Standard hedging handler (`AddStandardHedgingHandler`)

`MaxHedgedAttempts` = 1 (Polly `HedgingConstants`), so a call makes at most 2 attempts by default; this was also
observed in the fixture oracle (`docs/findings.md`, F2). Structure: total request timeout 30 s outside the hedging
strategy; per endpoint a rate limiter, circuit breaker (HTTP defaults) and 10 s timeout
(`HttpStandardHedgingResilienceOptions`, `HedgingEndpointOptions`). Not simulated in V1.
