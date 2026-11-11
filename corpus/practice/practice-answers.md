# Practice answers (open only after labelling)

| Item | target | pattern_codes | attempt_bound_get | attempt_bound_post | retrying | retry_handlers | attempt_timeout_s | total_timeout_s | breaker | retry_count_source | Why |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| PI-1 | catalog | P1;P14 | 4 | 4 | Y | 1 | 10 | 30 | Y | default | Defaults add one standard handler: 3 retries → 4 attempts, for every method |
| PI-2 | payments | P1;P2;P3;P6;P14 | 24 | 24 | Y | 2 | 5 | 30 | Y | explicit | Defaults (3+1) × per-client (5+1) = 24. Innermost attempt timeout is the per-client handler's 5 s if that handler is innermost; **note the ordering assumption** in notes |
| PI-3 | ordering | P2;P3;P8 | 4 | 1 | Y | 1 | 10 | 30 | Y | default | Unsafe methods excluded, so POST gets 1 attempt |
| PI-4 | inventory | P1;P5;P7;P14 | 3 | 3 | Y | 1 | 3 | none | N | unresolved | Defaults removed. Retry count comes from an environment variable, so the source is `unresolved` (confidence 2); the code's own literal fallback "2" is used (§4.4) → 3 attempts. `AddTimeout` is added after `AddRetry`, so it sits inside the retry and bounds each attempt: 3 s |
| PI-5a | shipping | P12 | 3 | 3 | Y | 1 | none | none | N | explicit | Polly v7 `WaitAndRetryAsync(2, …)`: 2 retries → 3 attempts; transient-error policy applies to all methods |
| PI-5b | reports | — | 1 | 1 | N | 0 | none | none | N | — | `new HttpClient()` has no resilience handler; `client_kind = direct` |

## Discussion points

- **PI-2 ordering.** Both registrations append to the same handler list; which is outer depends on registration
  order, which the attempt count does not depend on but the timeouts do. This is exactly the kind of case the
  executable oracle settles: if your label and the oracle disagree, the oracle wins and the guide gets a worked
  example.
- **PI-4 timeout placement.** In a Polly pipeline, strategies added later sit inside earlier ones. `AddTimeout`
  after `AddRetry` therefore bounds each attempt, not the whole call.
