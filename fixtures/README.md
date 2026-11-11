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

| Fixture | Pattern(s) | Expected attempt bound (GET / POST) |
| --- | --- | --- |
| P01-aspire-defaults | P1 | 4 / 4 |
| P02-per-client-standard | P2 | 4 / 4 |
| P03-lambda-options | P2, P3 | 6 / 6 |
| P04-config-bound | P2, P4 | 3 / 3 |
| P05-custom-pipeline | P5 | 3 / 3 |
| P06-stacked-defaults-and-client | P1, P2, P6 | 16 / 16 |
| P07-remove-then-custom | P1, P5, P7 | 3 / 3 |
| P08-unsafe-methods-disabled | P2, P3, P8 | 4 / 1 |
| P12-polly-v7 | P12 | 3 / 3 |
