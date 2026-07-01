# ADR-001: C# / .NET 9 as Primary Language

## Status
Accepted

## Context
We need to choose a primary language and runtime for building the ApprovalFlow microservice system. The team has strongest experience with C#.

## Decision
Use C# with .NET 9 for all services, including the UI (Blazor Server).

## Consequences
- **Positive**: Single language across entire stack; strong typing; excellent Dapr SDK support; mature ecosystem for enterprise applications
- **Positive**: Blazor keeps UI in C# — no need for a separate JavaScript framework
- **Negative**: Container images are larger than Go/Node alternatives (~200MB vs ~20MB)
- **Negative**: Cold start is slower than interpreted languages (mitigated by keeping containers warm)
