# RetryScope labelling guide — v1

Status: draft for the pre-registration (`prereg-v1`). Any change after registration bumps the version and is logged
in `docs/deviations.md`.

## 1. Purpose

You are recording, for each HttpClient registration, **what retry behaviour the code actually produces in
Production**. These labels are the ground truth that the extractor is measured against (RQ1). Label from the code
alone. **Never look at RetryScope's output while labelling.**

## 2. Unit of labelling

One row per **HttpClient registration in one service project**:

| Case | One row for |
| --- | --- |
| `services.AddHttpClient("name")` or `AddHttpClient<TClient>()` | each call |
| Process-wide defaults (`ConfigureHttpClientDefaults`) **and** the code resolves an unnamed client (`IHttpClientFactory.CreateClient()`) | the default client |
| `new HttpClient(...)` used to call another service | each construction site |

Registrations that only call third-party APIs (payment providers, email) are still labelled; set `target` to the
host name.

## 3. Fields

| Field | Values | How to fill |
| --- | --- | --- |
| `reg_id` | given | Pre-filled by the sampling script |
| `owner_service` | text | The project that makes the call |
| `client_kind` | `named` / `typed` / `default` / `direct` | `direct` = `new HttpClient()` |
| `target` | resource name, host, or `?` | From `BaseAddress` or the URL used |
| `pattern_codes` | `P1`–`P14`, `;`-separated | Every pattern that affects this client (§5) |
| `attempts_get` | integer, or `?` | Maximum attempts for one GET call (§4) |
| `attempts_post` | integer, or `?` | Maximum attempts for one POST call (§4) |
| `retrying` | `Y` / `N` | `Y` if `attempts_get` or `attempts_post` > 1 |
| `retry_handlers` | integer | Number of handlers on this client that contain a retry (2+ = stacking) |
| `attempt_timeout_s` | number / `none` / `?` | Innermost per-attempt timeout |
| `total_timeout_s` | number / `none` / `?` | Outermost whole-call timeout |
| `breaker` | `Y` / `N` / `?` | Any circuit breaker in any handler |
| `retry_count_source` | `explicit` / `config` / `default` / `unresolved` / `opaque` | Where the retry count comes from (§4.4) |
| `conditional` | `Y` / `N` | Registration depends on an `if` / environment check |
| `confidence` | 1 (unsure) – 3 (certain) | Your confidence in `attempts_get` |
| `notes` | text | Anything a reviewer needs; required when confidence < 3 |

## 4. Computing attempts

### 4.1 The rule

**Attempts per call = the product of (retries + 1) over every retry strategy on that client that applies to the
method.** Order does not matter for the count. A client with no retry strategy has 1 attempt.

Examples: one standard handler → (3+1) = 4. Aspire defaults plus a second standard handler → 4 × 4 = 16.

### 4.2 Defaults (verified from source, 11 Nov 2026)

| Setting | Standard handler | Plain Polly v8 `AddRetry(new RetryStrategyOptions…)` |
| --- | --- | --- |
| Retries | 3 (→ 4 attempts) | 3 |
| Base delay | 2 s | 2 s |
| Backoff | exponential, with jitter | constant |
| Attempt timeout | 10 s | — |
| Total timeout | 30 s | — |
| Circuit breaker | yes | — |

`HttpRetryStrategyOptions` (used inside `AddResilienceHandler`) has the standard handler's retry defaults:
3 retries, exponential, jitter.

### 4.3 Process-wide defaults and Aspire

If the service calls `AddServiceDefaults()`, **open that solution's ServiceDefaults `Extensions.cs` and read it**.
Do not assume the template: projects often edit it. If it calls
`ConfigureHttpClientDefaults(h => h.AddStandardResilienceHandler())`, every IHttpClientFactory client in that service
gets a standard handler (pattern P1), **in addition to** any per-client handler (stacking, P6).

### 4.4 Where the retry count comes from

- `explicit`: a literal in code (`MaxRetryAttempts = 5`).
- `config`: bound from a configuration section; look the key up in `appsettings.json` and
  `appsettings.Production.json` (Production wins). If the key is absent, the default applies and the source is
  `default`.
- `default`: not set anywhere; use §4.2.
- `unresolved`: comes from an environment variable, secret store, or a computed expression you cannot evaluate.
  **Still fill `attempts_*`**: use the code's own literal fallback if one is visible (e.g. `?? "2"`), otherwise the
  library default from §4.2. Set confidence ≤ 2 and write the expression in `notes`.
- `opaque`: count is known but a custom `ShouldHandle` decides which failures are retried. Count as if failures are
  retried.

### 4.5 Methods

`Retry.DisableForUnsafeHttpMethods()` disables retries for POST, PUT, PATCH, DELETE and CONNECT. `DisableFor(...)`
disables the listed methods. Apply this per retry strategy before multiplying.

## 5. Pattern codes

| Code | Pattern | Labelling note |
| --- | --- | --- |
| P1 | `ConfigureHttpClientDefaults(...AddStandardResilienceHandler())`, incl. Aspire ServiceDefaults | Applies to every factory client in the process |
| P2 | `.AddStandardResilienceHandler()` on one client | |
| P3 | `AddStandardResilienceHandler(o => …)` with option assignments | Read each assignment |
| P4 | Options bound from configuration | §4.4 |
| P5 | `AddResilienceHandler("name", b => b.AddRetry(...)...)` | Strategy order as written |
| P6 | Stacking (two or more handlers with retries) | Multiply (§4.1) |
| P7 | `RemoveAllResilienceHandlers()` | Removes handlers added **before** it in registration order |
| P8 | `DisableForUnsafeHttpMethods()` / `DisableFor(...)` | §4.5 |
| P9 | Custom `ShouldHandle` | `retry_count_source = opaque` unless the count is also unresolved |
| P10 | Keyed pipeline (`AddResiliencePipeline(key, …)`) executed around HttpClient calls | Count it only if you can see it wraps this client's calls |
| P11 | `AddStandardHedgingHandler` | `attempts_* = ?`, note it |
| P12 | Polly v7 (`AddPolicyHandler`, `AddTransientHttpErrorPolicy`) | Count `WaitAndRetry(n, …)` as n retries if literal |
| P13 | Hand-written retry loop around the call | Count the loop's bound if literal; note it |
| P14 | Aspire AppHost `WithReference` | Use it to fill `target` |

## 6. Hard cases

1. **Conditional registration** (`if (env.IsProduction())`): label the Production behaviour and set `conditional = Y`.
2. **Helper extension methods** (`services.AddMyClients()`): follow them into their definition.
3. **Code you cannot follow** (reflection, dynamic registration): `attempts_* = ?`, confidence 1, explain.
4. **Two retry strategies inside one `AddResilienceHandler`**: they also multiply.
5. **Never guess silently.** A `?` with a clear note is a correct label; a confident wrong number is not.

## 7. Process

1. Label in the order given by the sampling script.
2. Work from the pinned commit link only, not the repository's current state.
3. Save the form and commit its SHA-256 hash before any comparison with extractor output.
4. Intra-rater pass (≥ 14 days later): use the freshly shuffled 60-item form; do not open the first-pass file.
