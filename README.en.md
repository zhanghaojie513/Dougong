# Dougong

<p align="center">
  <img src="assets/logo.png" width="96" alt="Dougong">
</p>

<p align="center">
  <strong>Everything is a Plugin · .NET Agent Harness SDK</strong>
</p>

<p align="center">
  <a href="README.md">简体中文</a> · <span>English</span>
</p>

<p align="center">
  A composable plugin kernel that carries agent runtimes, tools, protocols and hosts.
</p>

<p align="center">
  <a href="https://github.com/zhanghaojie513/Dougong"><img src="https://img.shields.io/badge/repository-Dougong-2f6f5e?style=flat-square" alt="Repository"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/zhanghaojie513/Dougong/build.yml?style=flat-square&label=build" alt="Build"></a>
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square" alt=".NET 10"></a>
  <a href="https://github.com/zhanghaojie513/Dougong/releases"><img src="https://img.shields.io/badge/version-0.1.1--alpha.2-orange?style=flat-square" alt="Version 0.1.1-alpha.2"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1f883d?style=flat-square" alt="MIT License"></a>
</p>

> [!WARNING]
> **Early development — do not depend on this yet.** M0–M3 are complete and all four completion
> criteria are met, but the version is still `0.x` with **no backward-compatibility promise**:
> every public API change is caught by ApiCompat and recorded in the
> [CHANGELOG](CHANGELOG.md). Rules: [compatibility policy](docs/compat-policy.md).
> Good for reading, trying out and writing plugins; wait for `1.0` before production use.

> **Dougong** borrows the "Everything is a Plugin" idea from DeepSeek Harness (dsh) to build an independent Agent Harness SDK for the .NET ecosystem. It borrows architectural ideas only — no code is ported, and this is not an official DeepSeek project.

A *dougong* (斗拱) is the bracket set in traditional Chinese timber architecture: standardized components assembled layer by layer to carry the roof. The project uses the same metaphor — the kernel bears and coordinates, plugins provide capabilities, and components can be composed, replaced, disabled and reloaded.

## Current Status

Targeting `net10.0`, version `0.1.1-alpha.2`, **published to nuget.org** (2026-10-29).
All four design commitments — composability, lifecycle integrity, explainable failure,
actually usable — are met; see [design commitments](docs/goal.md).

**Scale**: 7 product projects, 1 kernel test project (**199 tests**), 9 sample/probe projects,
**45 deterministic self-check suites** (no network, no API key required).

**Verified capabilities**:

- **Kernel** — lifecycle state machine, generation-bound service leases, dependency graph,
  bounded async events, collectible ALC dynamic load/unload, five failure stages with hints
- **Agent** — single tool execution entry (`ToolDispatcher`: schema validation → approval →
  resource lock → execute), JSONL commit barrier, session recovery and branching, context
  compaction, tool-result size limit (clamps model context only; logs stay complete),
  plan protection (compaction never summarizes the current plan); reasoning is a separate
  stream (`ReasoningDelta`, never mixed into the answer), plus `AgentPhaseTracker` so hosts
  don't have to guess "what should be on screen right now" from the event stream alone
- **Hosting** — kernel wiring (plugin registration, lease adapters, service resolution).
  Shared by the CLI and a second host (the desktop app) so neither reimplements it — that
  kind of duplication fails silently (registration order, accessor vs. bare instance, cleanup)
- **Ai** — three wire-protocol connectors (chat-completions / responses / anthropic),
  dialect differences contained in `WireCompat`, usage accounting
- **Mcp** — remote tool bridging and change detection (annotations are not authorization)
- **Acp** — agent side (line-delimited JSON-RPC), approval, `ask_user` and `plan`
- **Cli** — reference host, REPL and `--acp` modes; `--doctor` startup checks,
  `--report` diagnostic bundle (no keys, no conversation content), `DOUGONG_LOG` levels

**L1 preference memory** is implemented and verified: hard-coded whitelist, approval bound to
a content hash, revocable, no cross-workspace propagation. Effect measured across 6 providers
× 2 protocols (14 combinations): **preference adherence improved 60%–90% (median 80%)**.
That result applies only to the tested models and task set; the full experiment
record is internal material.

**Not yet done**: `Dougong.Telemetry` (OTel export), power-loss durability verification,
ACP Registry listing (the repo is public; the remaining blocker is auth — the Registry only
accepts OAuth or terminal-interactive auth, while this project has environment-variable auth
only).

## Why Dougong

Dougong focuses on the **supporting layer** for agents — not on any particular model or UI:

- Plugins get a clear lifecycle and scope
- Services collaborate through generation binding and invocation leases
- Missing dependencies, provider replacement and deactivation all go through one coordinator
- Dynamic plugins use collectible `AssemblyLoadContext`s
- Agents, MCP, ACP, Skills and hosts can all compose as plugins
- Model providers plug in through `Microsoft.Extensions.AI`; no provider matrix is baked into the kernel

## Core Architecture

```text
┌──────────────────────────────────────────────────────┐
│                    Host / Desktop                    │
│              CLI · Avalonia · ACP Client             │
└──────────────────────────┬───────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────┐
│ Dougong.Agent                                        │
│ agent loop · sessions · tool pipeline · approval ·   │
│ context compaction                                   │
├──────────────────────────────────────────────────────┤
│ Dougong.Ai · Dougong.Mcp · Dougong.Acp               │
│ model routing · MCP bridge · ACP adapter             │
├──────────────────────────────────────────────────────┤
│ Dougong.Kernel                                       │
│ lifecycle · service registry · dependency graph ·    │
│ events · dynamic loading                             │
└──────────────────────────────────────────────────────┘
```

### `Dougong.Kernel` Design Highlights

| Capability | Description |
|---|---|
| Lifecycle | `Registered`, `Activating`, `Active`, `Deactivating`, `Stopped`, `Blocked`, `Failed` states with one coordinator |
| Service collaboration | `(Type, Name?)` service keys, optional requirements, explicit `ProviderId`, priority selection and activation transactions |
| Invocation lease | `IResolver` returns generation-bound service references; the in-flight count is released after the call |
| Deactivation drain | Close new calls, cancel plugin lifetime, wait for in-flight calls and accepted events, then clean up |
| Event bus | Typed subscriptions, dedicated bounded queues, async pumping, unsubscribe-releases-references and overflow diagnostics |
| State view | Revision-numbered deltas committed atomically in the lock, snapshots, cursor renewal, register/remove projections and resync signals |
| Dynamic loading | Collectible `AssemblyLoadContext`, explicit shared contracts, version checks and cooperative unloading |
| External reference hygiene | Snapshots and handles carry only shared data; the full manifest is fetched explicitly |

> The current implementation is a trusted in-process plugin model. The ALC provides dependency isolation and cooperative unloading — not an OS-level permission sandbox or fault isolation.

## Package Structure

| Package | Role | Status |
|---|---|---|
| `Dougong.Kernel` | Lifecycle, services, events, dependency graph, dynamic loading, failure diagnostics | **Complete** (ApiCompat-guarded) |
| `Dougong.Agent` | Agent runtime contracts, loop, sessions, tools, approval, compaction, L1 memory | **Complete** (ApiCompat-guarded) |
| `Dougong.Ai` | Three wire-protocol connectors, `ChatClientFactory`, usage accounting, metrics | **Complete** (ApiCompat-guarded) |
| `Dougong.Mcp` | MCP bridge, change detection, injectable transport | **Complete** (ApiCompat-guarded) |
| `Dougong.Acp` | ACP agent endpoint (line-framed JSON-RPC over stdio), approval, `ask_user`, `plan` | **Complete** (ApiCompat-guarded) |
| `Dougong.Hosting` | Host assembly: kernel wiring, lease adapters, service resolution. Shared by CLI and Desktop | **Complete** (ApiCompat-guarded) |
| `Dougong.Cli` | Headless reference host: REPL + `--acp` + `--doctor` + `--report` | **Complete** (tool, not a library) |
| `Dougong.Telemetry` | OpenTelemetry bridge | Not started (metrics already exposed via BCL `Meter`) |

## Install

**As a tool** (no need to clone the repo):

```bash
dotnet tool install --global Dougong.Cli --version 0.1.1-alpha.2
dougong --doctor      # check the environment first; it reports each item and the next step
dougong               # start a conversation (model config required)
```

The package is `Dougong.Cli`; the command is `dougong`. The names differ on purpose:
packages follow the `Dougong.*` convention, commands stay short.

> In `0.1.1-alpha.2` the command surface is complete (`--doctor`, `--report`, `--help`,
> REPL, `--acp` are all exercised, behavior pinned by 45 self-check suites), but the
> **terminal presentation and wording are still being polished**.

**As a library**:

```bash
dotnet add package Dougong.Agent     # runtime: loop, tool dispatch, sessions, compaction
dotnet add package Dougong.Hosting   # kernel wiring: plugin registration, lease adapters
dotnet add package Dougong.Ai        # model connections (three wire protocols)
dotnet add package Dougong.Mcp       # MCP tool bridge
dotnet add package Dougong.Acp       # ACP agent side
dotnet add package Dougong.Kernel    # plugin kernel (usually pulled in by Hosting)
```

See the [embedding guide](docs/embedding-guide.md) for a 20-line minimal example.

## Getting Started

### Requirements

- .NET SDK `10.0.402`
- Windows, Linux or macOS
- Git

### Build and Test

```bash
git clone https://github.com/zhanghaojie513/Dougong.git
cd Dougong

dotnet restore Dougong.slnx
dotnet build Dougong.slnx -c Release
dotnet test Dougong.slnx -c Release --no-build
```

### Packaging and publishing

```bash
dotnet pack Dougong.slnx -c Release --output ./artifacts/packages
```

To publish to nuget.org use `bash scripts/publish-nuget.sh` (run with `--dry-run` first).
It pushes in dependency order, stops on the first failure, and never reads or prints the
API key (taken only from the `NUGET_API_KEY` environment variable).

> **Compatibility**: the project is `0.x` and **makes no backward-compatibility promise**,
> but every public API change is caught by ApiCompat and recorded in
> [CHANGELOG](CHANGELOG.md). Rules in [compat policy](docs/compat-policy.md).

## Documentation

> Full index (including development logs and retrospectives) in [docs/README.md](docs/README.md).
> **The documentation is currently written in Chinese.**

**Start here**

| Document | When to read |
|---|---|
| [Getting started](docs/getting-started.md) | First time. Every command says what you should see |
| [Plugin author guide](docs/plugin-author-guide.md) | Writing a kernel or tool plugin; five common mistakes and how to locate failures |
| [Embedding guide](docs/embedding-guide.md) | Putting Dougong inside your own .NET service (20-line minimum) |
| [Troubleshooting](docs/troubleshooting.md) | When something breaks; tells a defect apart from an environment issue |

**Understanding the design**

| Document | When to read |
|---|---|
| [Architecture](docs/architecture.md) | Layering, kernel contracts, state machine, dynamic loading, host boundary. Code comments cite its section numbers |
| [Overall design](docs/design.md) | Why it is shaped this way: niche, milestones, key trade-offs |
| [Goal](docs/goal.md) | Four non-negotiable properties and anti-goals; read before deciding what to build |
| [Compatibility policy](docs/compat-policy.md) | Before changing a public API: what is promised, three tiers of breaking change, deprecation flow |

**Status and planning**

| Document | Contents |
|---|---|
| [Performance baseline](docs/performance-baseline.md) | Measured numbers and environment; recorded, not promised |
| [Troubleshooting](docs/troubleshooting.md) | Real cases for build, provider and plugin loading |

> Internal working records (status board, schedule, milestone plans, development logs) are
> not in this repository — they are by-products of maintenance and change as findings come in.
> The remaining documents are decision records and comparisons with similar projects.
> See the full index in
> [docs/README.md](docs/README.md).

## Roadmap

```text
M0  plugin kernel and lifecycle counterexample verification   ✅ closed
 │
 ▼
M1  agent runtime comparison and a minimal usable agent       ✅ closed (in-house loop chosen)
 │
 ▼
M2  ACP · MCP · telemetry · restricted memory                 ✅ closed (metrics via BCL Meter; no OTel exporter)
 │
 ▼
M3  plugin API stabilization and ecosystem tooling            ✅ all four criteria met
 │
 ▼
I0  stabilization and release                                 ✅ closed 2026-10-28 (published to nuget.org)
```

The four M3 completion criteria — an outside author can write a plugin from docs alone,
API changes are caught automatically, repeated load/unload does not leak, and failures are
locatable — are all met.

I0's exit condition — a person not involved in development completes install → configure →
real task → write a plugin, on clean Windows and Linux — has evidence on both platforms.

**What comes next is driven by real demand, not the roadmap.** The criterion for I1
"embeddable runtime" is already satisfied: a desktop host runs as a separate process on top of
this SDK, and the first gap it hit became part of the API.

## Design Boundaries

Dougong currently **is not**:

- a port of, or a compatible runtime for, dsh
- an in-process security sandbox
- an LLM provider matrix
- a desktop UI or IDE
- a multi-agent orchestration framework
- a finished AI coding assistant

The companion desktop client will be developed as a separate Dougong Desktop host project; this repository owns the SDK, the kernel and the reference CLI.

## Contributing

The project is in a period of rapid API evolution. Before opening an issue or pull request, please read the [architecture doc](docs/architecture.md) and the [design commitments](docs/goal.md), and add deterministic failure-path tests for any behavior change.

### Naming convention

The project name is always written **Dougong** — never DouGong or "dou gong":

- NuGet packages and namespaces: `Dougong.*` (e.g. `Dougong.Kernel`)
- CLI command and dotnet tool: lowercase `dougong`
- Project name in repositories, directories and URLs: `Dougong`

## Acknowledgements

Dougong is an independent open-source project. The following projects provided public ideas or technical references:

- [deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) — the "Everything is a Plugin" architectural reference
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) — agent runtime capability reference
- [Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) — model abstraction and connector foundation
- [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) — MCP protocol foundation
- [Hermes Agent](https://github.com/NousResearch/hermes-agent) — reference for the skill self-evolution idea
- [TKCode](https://github.com/AtomUICommunity/TKCode) — design reference for self-describing permissions, concurrency tiers and commit barriers
- [pi.net](https://github.com/AIDotNet/pi.net) — reference for how to split "libraries vs application" on .NET
  (`CodingAgent` vs `Cli` layers); the "keep samples alive via CI" practice also comes from it.
  **Only its README and directory structure were read for architectural comparison — no code was copied**;
  see [host-layering-decision.md](docs/host-layering-decision.md)

The projects above are **idea and design references only**; Dougong contains none of their code.
Each borrowed idea is recorded with its provenance in `docs/` (who, which point, why adopted or rejected) —
including the rejected ones, e.g. [kernel-vs-cordis.md](docs/kernel-vs-cordis.md) explains why
"a .NET dsh equivalent" is not a claim this project makes.

## License

MIT — see [LICENSE](LICENSE). © 2026 Haojie Zhang.

Dougong is an independent implementation, not a port or fork of any project.
The projects under "Acknowledgements" were read for architectural comparison
only; no source code was copied, so no upstream copyright is retained here.
Each borrowed idea is recorded with its provenance in `docs/`, including the
ideas that were reviewed and rejected.
