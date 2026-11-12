# Extractor fixtures

Each folder is one small project that registers HttpClients using one configuration pattern (P-codes from
`docs/labelling-guide.md`). Every fixture has:

- `Registration.cs`: a `Register(IHostApplicationBuilder)` method holding only the registration code. This is the
  code the extractor analyses.
- `expected.json`: the expected labels, in the labelling guide's fields.
- `appsettings.json` when the pattern reads configuration.

`Shared.ServiceDefaults` is a trimmed copy of the Aspire ServiceDefaults template. Only the HttpClient part is kept;
OpenTelemetry, health checks and service discovery are left out because they don't change resilience behaviour.

`tests/RetryScope.Fixtures.OracleTests` runs every fixture's registration code against an in-memory stub server and
counts real attempts. **The fixtures are regression tests for the extractor, written by the author; they are not
evaluation evidence.**

| Fixture | Pattern(s) | Expected attempt bound (GET / POST) | Note |
| --- | --- | --- | --- |
| P01-aspire-defaults | P1 | 4 / 4 |  |
| P02-per-client-standard | P2 | 4 / 4 |  |
| P03-lambda-options | P2, P3 | 6 / 6 |  |
| P04-config-bound | P2, P4 | 3 / 3 |  |
| P05-custom-pipeline | P5 | 3 / 3 |  |
| P06-stacked-defaults-and-client | P1, P2, P6 | 16 / 16 |  |
| P07-remove-then-custom | P1, P5, P7 | 3 / 3 |  |
| P08-unsafe-methods-disabled | P2, P3, P8 | 4 / 1 |  |
| P09-custom-predicate | P2, P3, P9 | 4 / 4 |  |
| P10-keyed-pipeline | P10 | 3 / 3 |  |
| P11-hedging | P11 | ? / ? |  |
| P12-polly-v7 | P12 | 3 / 3 |  |
| P13-manual-retry-loop | P13 | 3 / 3 |  |
| V01-direct-httpclient | — | 1 / 1 | static only |
| V02-typed-client | P2 | 4 / 4 |  |
| V03-helper-extension | P2, P3 | 5 / 5 |  |
| V04-conditional-environment | P2, P3 | 2 / 2 |  |
| V05-production-override | P2, P4 | 6 / 6 |  |
| V06-stack-order-defaults-first | P1, P2, P3, P6 | 16 / 16 | stack-order probe |
| V07-stack-order-client-first | P1, P2, P3, P6 | 16 / 16 | stack-order probe |

P14 (Aspire AppHost topology) comes with the topology importer: it needs a multi-project system and the Aspire SDK.
