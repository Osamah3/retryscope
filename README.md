# RetryScope

RetryScope extracts the effective HTTP resilience configuration of a .NET service system (Polly v8,
Microsoft.Extensions.Http.Resilience, Aspire), builds a model from it, and predicts retry amplification and recovery
from overload. A controlled testbed checks those predictions.

> **Status:** pre-implementation research project. The methodology is fixed in the technical specification and will
> be pre-registered (git tag `prereg-v1` + OSF) before the main experiments. Nothing here is a result yet.

## Principles

- Free and local: .NET SDK, Docker and Python only. No paid APIs, AI services or paid infrastructure.
- Independent: public repositories and self-built configurations only. No workplace code or data.
- Honest: every extracted value records its provenance; missed targets are reported, not tuned away.

## Build and test

Requires the .NET 10 SDK (any 10.0.x feature band; see `global.json`).

```bash
dotnet test --solution RetryScope.slnx
```

Tests run on Microsoft.Testing.Platform; `global.json` opts the .NET 10 SDK into it.

The first restore creates `packages.lock.json` files. Commit them: CI restores in locked mode.

## Layout

| Path | Contents |
| --- | --- |
| `src/RetryScope.Model` | The intermediate representation (`ResilienceGraph`), value provenance, validation, canonical JSON |
| `fixtures/` | One small project per configuration pattern, with expected labels (`expected.json`) |
| `tests/RetryScope.Model.Tests` | Unit tests for the IR |
| `tests/RetryScope.Fixtures.OracleTests` | Behavioural oracle: runs each fixture against an in-memory failing stub and counts real attempts |
| `docs/` | Labelling guide, ADRs, empirical findings, deviations log |
| `corpus/practice/` | Practice items for the labelling guide |

Planned: `RetryScope.Extraction`, `RetryScope.Topology`, `RetryScope.Simulation`, `RetryScope.Cli`, `fixtures/`,
`testbed/`, `experiments/`, `analysis/`, `paper/`.

## Licence

Code: MIT. Datasets (when published): CC BY 4.0.
