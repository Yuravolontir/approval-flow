# ADR-005: Dapr as Distributed Application Middleware

## Status
Accepted

## Context
Microservices need reliable inter-service communication, state management, pub/sub messaging, and secret management. Building these from scratch is complex and error-prone.

## Decision
Use Dapr (Distributed Application Runtime) for:
- **Service Invocation**: Service-to-service calls via Dapr sidecars
- **State Store**: Redis-backed state management with ETag concurrency
- **Pub/Sub**: Redis Streams for asynchronous event messaging
- **Secret Store**: Local file-based secret store for API keys

## Consequences
- **Positive**: Infrastructure concerns are abstracted — switching from Redis to PostgreSQL requires only a YAML config change
- **Positive**: Built-in retry, circuit breaking, and observability
- **Positive**: Sidecar pattern means no Dapr SDK dependency in application code (HTTP API)
- **Negative**: Additional container per service (Dapr sidecar) — doubles container count
- **Negative**: Learning curve for Dapr concepts and configuration
- **Negative**: Local development requires `dapr init` or Docker Compose with sidecars
