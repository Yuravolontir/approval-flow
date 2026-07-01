# ADR-004: LLM Provider — OpenRouter

## Status
Accepted

## Context
The AI agent needs access to an LLM for invoice analysis. We need a provider that supports multiple models and is OpenAI-API compatible.

## Decision
Use OpenRouter as the LLM provider, accessed via OpenAI-compatible HTTP API. Provider is configurable via environment variables (M15).

## Consequences
- **Positive**: Access to multiple models (Claude, GPT, Gemini) via single API
- **Positive**: OpenAI-compatible API means we can swap to direct OpenAI, Azure, or local Ollama with config change only
- **Positive**: No vendor lock-in — `ILlmClient` interface abstracts the provider
- **Negative**: External dependency — requires internet access and API key
- **Mitigation**: Stub LLM implementation for CI/tests returns deterministic responses based on fixtures
