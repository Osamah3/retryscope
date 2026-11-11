# Empirical findings from the fixture oracle

Behaviour established by running the fixtures, not by reading source or documentation. Each entry names the
fixtures that show it and the package versions it was observed with. If a pinned version changes, rerun the oracle
tests; a changed result is logged here and in `docs/deviations.md` if it falls after pre-registration.

Versions for all entries below: Microsoft.Extensions.Http.Resilience 10.11.0, Microsoft.Extensions.Http 10.0.15,
.NET 10 SDK, Windows x64. Observed 11 Nov 2026.

## F1. `ConfigureHttpClientDefaults` handlers are outermost regardless of call order

- **Fixtures:** V06-stack-order-defaults-first, V07-stack-order-client-first (`StackOrderOracleTests`).
- **Setup:** the per-client standard handler has `TotalRequestTimeout` 1 s and `AttemptTimeout` 500 ms; the
  defaults handler has the standard settings (10 s attempt, 30 s total). The stub never responds.
- **Prediction if per-client were outer:** the call ends at about 1 s.
- **Observed:** V06 lasted 13.13 s, V07 lasted 11.39 s, each over 4 outer attempts. Each per-client handler gives up
  after its 1 s total timeout; the defaults handler treats that `TimeoutRejectedException` as transient and retries it
  with exponential, jittered backoff. The roughly 2 s difference between the runs is consistent with jitter.
- **Consequence:** labelling guide §4.6; the extractor orders handlers by where they are registered (defaults vs
  per-client), not by source order; the simulator nests defaults handlers outside per-client ones.

## F2. The standard hedging handler made 2 attempts against an always-503 stub

- **Fixture:** P11-hedging (`AttemptBoundOracleTests`, logged only).
- **Observed:** GET = 2, POST = 2.
- **Explanation:** Polly's `HedgingConstants.DefaultMaxHedgedAttempts = 1` (Polly commit 98eaf12), and the standard
  hedging handler does not override it. That gives the primary request plus one hedge. The per-endpoint pipeline has no
  retry strategy.
- **Status:** informative only. Hedging remains out of scope for the V1 simulator (ADR 0002), so P11's label stays
  `?`, and extractor output for hedging is checked only for detection (diagnostic RS001). Under other settings,
  for example a faster-failing primary or a changed `MaxHedgedAttempts`, the count can differ, so this number is not
  a general rule.
