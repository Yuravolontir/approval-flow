# ADR-003: Saga Pattern — Orchestration over Choreography

## Status
Accepted

## Context
The payment flow involves multiple steps (reserve budget → execute payment → update status) that must be atomic. On failure, we need compensating actions (release budget, reverse payment).

## Decision
Use orchestration-style saga with the Workflow Service as the coordinator, rather than choreography (event-driven).

## Consequences
- **Positive**: Single place to see the entire saga flow — easier to debug and trace
- **Positive**: Compensation logic is explicit and centralized
- **Positive**: Workflow state stored in Dapr State Store — durable across restarts
- **Negative**: Workflow Service becomes a coordination bottleneck (acceptable at MVP scale)
- **Negative**: Tighter coupling between Workflow and Payment services via Dapr service invocation
