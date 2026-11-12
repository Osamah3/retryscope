# ADR-0001: Simulator in C#, statistics in Python

- Status: accepted
- Date: 2026-11-11

## Context

RetryScope is a .NET developer tool. Its simulator must reproduce Polly's retry, timeout and circuit-breaker
semantics exactly, and is verified by differential tests against real Polly pipelines.

## Decision

The extractor, IR and simulator are written in C# (.NET 10 LTS). Python is used only for statistical analysis,
data processing and figures. The simulation engine is never re-implemented in Python; Python reads the
simulator's CSV/JSON output.

## Consequences

- Differential tests against Polly run in-process with a fake `TimeProvider`.
- One language for every component that touches resilience semantics.
- Python dependencies stay small (pandas, SciPy, statsmodels, matplotlib) and pinned.
