# ADR-0002: Version 1 scope

- Status: accepted
- Date: 2026-11-11

## Decision

V1 covers HTTP between services only: Polly v8 pipelines, Microsoft.Extensions.Http.Resilience's standard handler,
and Aspire ServiceDefaults/AppHost. Modelled behaviour: retries, attempt and total timeouts, circuit breakers and the
standard rate limiter, under transient failures, latency spikes, load spikes and capacity drops. Testbed: at most
6 services.

Out of scope: message brokers, databases, gRPC and other protocols; hedging and Polly v7 (detected, not simulated);
hand-written retry loops; service-mesh and gateway retries.

## Consequences

Extension points are kept in the architecture (`ITransportModel`, `IConfigurationSource`, `ITopologySource`, new
`Strategy` kinds) so later versions can add scope without changing the IR's existing contracts.

## Constraints that apply to every version

No paid APIs, AI services or paid infrastructure; no workplace code or data; everything reproducible locally with
free tools.
