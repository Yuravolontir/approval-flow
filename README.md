# ApprovalFlow

[![CI](https://github.com/Yuravolontir/approval-flow/actions/workflows/ci.yml/badge.svg?branch=dev)](https://github.com/Yuravolontir/approval-flow/actions/workflows/ci.yml)

AI-assisted invoice approval system with deterministic policy enforcement.

## What It Does

ApprovalFlow automates expense invoice approvals using a two-layer approach:
1. **AI Agent** analyzes invoices against company expense policy and recommends a decision
2. **Deterministic Router** enforces hard rules — amount ceilings, policy violations, fraud signals — and makes the final routing decision

Invoices are either auto-approved (fast path), escalated for human review, or rejected. Payment processing uses a saga pattern with automatic compensation on failure.

## Tech Stack

| Component | Technology |
|-----------|------------|
| Backend | C# / .NET 9 |
| UI | Blazor Server |
| API Gateway | YARP reverse proxy |
| Middleware | Dapr (pub/sub, state store, service invocation, secrets) |
| Infrastructure | Docker Compose, Redis |
| LLM | OpenRouter (OpenAI-compatible, swappable) |

## Architecture

```
┌──────────┐     ┌──────────────┐     ┌─────────────────┐     ┌─────────────┐
│  Blazor  │     │   Gateway    │────▶│ Invoice Service  │────▶│  Workflow    │
│   UI     │     │   (YARP)     │     │ (intake, dedup)  │     │  Service    │
│  :3000   │     │   :8080      │     │                  │     │ (AI+Router) │
└──────────┘     └──────────────┘     └─────────────────┘     └──────┬──────┘
                                                                      │
                                                               ┌──────▼──────┐
                                                               │  Payment    │
                                                               │  Service    │
                                                               │ (budget+pay)│
                                                               └─────────────┘
                        All services connected via Dapr sidecars + Redis
```

## Quick Start

### Prerequisites
- Docker & Docker Compose
- (Optional) .NET 9 SDK for local development

### Run

```bash
# 1. Copy environment config
cp .env.example .env
# Edit .env with your OpenRouter API key

# 2. Start all services
docker compose up --build

# 3. Access
# UI:      http://localhost:3000
# Gateway: http://localhost:8080
```

### Verify

```bash
./scripts/verify.sh
```

Runs 4 journeys:
- **A**: Auto-approve (INV-1001, $42.50 meal)
- **B**: Escalate → human approve → paid (INV-1003, $1820)
- **C**: Duplicate detection (INV-1001 again)
- **D**: Payment failure → saga compensation (INV-1012)

## Key Design Decisions

See [docs/adr/](docs/adr/) for Architecture Decision Records.

### The Dilemma: Autonomy Posture

- **Ceiling**: $250 — auto-approve only below this amount
- **Confidence**: 0.80 — AI must be 80%+ confident
- **Hard stops**: Unknown vendor, math mismatch, fraud signals — always human review

Details: [docs/PRODUCT-DILEMMA.md](docs/PRODUCT-DILEMMA.md)

## Project Structure

```
├── src/
│   ├── ApprovalFlow.Gateway/      # YARP reverse proxy
│   ├── ApprovalFlow.Invoice/      # Invoice intake + dedup
│   ├── ApprovalFlow.Workflow/     # AI agent + router + saga
│   ├── ApprovalFlow.Payment/      # Budget + payment
│   ├── ApprovalFlow.Shared/       # DTOs, contracts
│   └── ApprovalFlow.UI/           # Blazor Server UI
├── dapr/
│   ├── components/                # Dapr component configs
│   ├── config.yaml                # Dapr configuration
│   └── secrets.json               # Local secrets (not committed)
├── docs/
│   ├── ARCHITECTURE.md            # System architecture
│   ├── PRODUCT-DILEMMA.md         # Autonomy posture justification
│   └── adr/                       # Architecture Decision Records
├── docker-compose.yml
├── policy.md                      # Expense policy rules
└── sample-invoices.json           # 19 test fixtures
```

## License

MIT — see [LICENSE](LICENSE)
