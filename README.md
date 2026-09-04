# Harness

Harness is an open-source, local-first AI desktop workbench for indie developers.
It aims for the density, speed, and keyboard fluency of a terminal multiplexer
without limiting capable models to a text-only terminal interface.

Harness is a standalone C# application built with Avalonia. It is not Electron,
does not embed a browser runtime, and targets Windows, macOS, and Linux from one
desktop codebase.

## Product principles

- **Responsiveness first.** The UI thread is reserved for UI work. Provider,
  storage, and background processing must not interrupt typing, navigation,
  scrolling, or window interaction—even as projects and histories grow.
- **Compact, not austere.** A dense workspace with projects, tasks, execution
  trace, conversation, context, model controls, diff, and terminal surfaces.
- **Capability-complete.** Text, vision, tools, reasoning, audio, image
  generation, caching, and computer use are negotiated per model.
- **Local-first.** Projects and normalized history live on the user's machine.
- **Bring your runtime.** Subscription-backed agent CLIs, direct APIs, and local
  OpenAI-compatible servers are adapters behind the same workbench.
- **Replace, do not wrap.** Other harnesses are optional migration sources. Once
  imported, Harness owns the local project/session record and does not require
  the source harness to remain installed.
- **Loss-aware imports.** Imported projects and conversations retain provenance
  and provider-native records; credentials are never copied.
- **Keyboard-first, mouse-friendly.** Common flows should be one shortcut away,
  while images, attachments, drag-and-drop, and inspection remain first-class.

## Current milestone

Direct API connections are now available in **Settings → Providers** for OpenAI,
Anthropic, Gemini, xAI, Mistral, DeepSeek, OpenRouter, and local compatible servers.
This first slice includes native streaming, client workspace tools, explicit
approvals, attachments, and durable continuation. It is not yet full provider
feature parity or live-account certified. See [API provider coverage and setup](docs/api-providers.md)
for discovery limits, credential handling, and remaining work.

The repository now contains a compiled desktop shell and the first canonical
capability/content contracts:

- compact three-surface workbench with a continuous activity trace;
- integrated Harness title bar with native move, resize, minimize, maximize,
  restore, and close behavior;
- model selector and provider-specific control area;
- reactive per-model capability badges and vision attachment availability;
- model-specific reasoning and service-tier choices generated from the
  connected provider's model descriptor, with provider-confirmed effective
  settings shown in the inspector;
- a stable turn-attachment menu for image, video, and text/code: native image
  delivery for vision-capable Codex models, native file references for text and
  code, removable multi-file chips, and visible disabled rows for modalities the
  connected runtime cannot accept;
- generated-image links rendered as durable inline previews with open and
  copy-path actions;
- rolling and weekly usage-window surfaces backed by authenticated runtime data;
- a Codex app-server adapter for model discovery and authenticated rate-limit
  snapshots without starting a model turn;
- explicit ChatGPT subscription sign-in through the Codex runtime;
- persistent subscription connection management in Settings, including the real
  account identity, plan, runtime source, complete reported model list, refresh,
  sign-in, and sign-out without removing local projects or chats;
- provider-neutral model-picker preferences for visibility, favorites, and
  ordering, retained across catalog refreshes, restarts, and provider reconnects;
- unclean-shutdown recovery with a visible next-launch notice and bounded,
  privacy-safe diagnostics that exclude prompts, credentials, commands, and paths;
- real Codex thread/turn startup, streamed assistant messages, activity events,
  and token-usage updates;
- active-context tracking from the provider's latest input footprint (kept
  distinct from cumulative thread throughput), native compaction requests, and
  restart restoration from retained provider telemetry;
- persistent Ask, Approve for me, and Full access modes mapped to the provider's
  native reviewer, approval, and sandbox controls;
- native developer-instruction delivery for saved personalization without
  inserting hidden transcript messages;
- live auto-follow for new and streaming chat content plus a compact turn-phase
  indicator that distinguishes delivered text from a completed turn;
- streamed reasoning summaries, plans, commands and command output, tool events,
  provider errors, file-change records, and turn-level diff inspection;
- inline command and file-change approval prompts with explicit accept/decline;
- versioned SQLite storage for projects, multiple named sessions, streaming
  transcript checkpoints, provider-native events, and model/thread settings;
- restart recovery that restores the local transcript at its newest message,
  resumes the saved provider thread when supported, and preserves the session's
  provider, model, reasoning effort, and service tier;
- session creation, selection, renaming, and confirmed deletion from the task rail;
- persistent session context files copied into Harness-owned content-addressed
  storage; text is delivered as actual model input, images use native vision
  input, and references retain their original filenames;
- a Skills Library inside Settings with a bookshelf shortcut, repository-level
  GitHub tree indexes beyond code-search limits, progressively cached searchable
  descriptions, topic/source/status/connected-model filtering, collision-safe
  provider identities, explicit package inspection, pinned downloads, and Codex
  user/workspace installation;
- live Git branch and working-tree inspection with staged, unstaged, and
  untracked status; full per-file diff review; stage and unstage actions; and
  confirmed revert actions that preserve a Harness recovery copy first;
- a dedicated modeless diff module with old/new line numbers, colored additions
  and removals, hunk headers, and per-file added/removed totals;
- chat-native composer behavior (`Enter` sends; `Ctrl+Enter` or `Shift+Enter`
  inserts a line break) and explicit UTF-8 provider transport;
- selectively copyable chat text with normal clickable hyperlinks, Markdown
  labels, concise raw-URL summaries, and full destinations available as tooltips;
- an auditable runtime resolver that prefers Harness-bundled or Harness-managed
  runtimes and never binds to another desktop harness;
- honest unavailable states whenever a provider has not reported context or
  working-tree data;
- headless visual-check utility for repeatable UI renders;
- preserved Go terminal experiment under `prototypes/go-tui`.

Ordinary application startup contains no representative model, conversation, or
usage data. The visual-check tool deliberately opts into labeled preview data and
never runs in the production application path. General file/context attachment,
full Git inspection, additional providers, and import adapters remain on the
roadmap.

## Build and run

Development requires the .NET 8 SDK. Harness can download the current official
Codex package, verify the SHA-256 digest published with its GitHub release, and
activate its CLI, code-mode host, command runner, sandbox helper, and bundled
search tool from Harness-owned storage. Incomplete older installs repair in the
background. A standalone system CLI remains a
development fallback when no managed runtime is installed; another desktop
harness is never searched or required.
The first launch discovers models without starting a turn. Authentication is
requested only during initial setup or after the stored provider connection is
no longer valid; model and usage refreshes otherwise happen in the background.

```powershell
dotnet restore Harness.sln
dotnet build Harness.sln --no-restore
dotnet run --project src\Harness.App\Harness.App.csproj
```

Render the UI off-screen for visual inspection:

```powershell
dotnet run --project tools\Harness.VisualCheck\Harness.VisualCheck.csproj -- .artifacts\harness-shell.png
dotnet run --project tools\Harness.CodexProbe\Harness.CodexProbe.csproj
```

## Repository layout

```text
src/Harness.Core/          provider-neutral models and contracts
src/Harness.App/           Avalonia desktop application
src/Harness.Storage/       versioned SQLite persistence
src/Harness.Workspace/     Git status, diff, index, and recovery operations
src/Harness.Providers.Codex/ Codex app-server transport
tools/Harness.VisualCheck/ headless UI renderer
docs/                      architecture and portable import format
prototypes/go-tui/         preserved terminal exploration
```

Read [the architecture](docs/architecture.md), [the interface direction](docs/interface.md),
[the portable import format](docs/portable-format.md), and the
[production data-integrity rules](docs/data-integrity.md). The ordered replacement
plan is tracked in [the product roadmap](docs/roadmap.md), with local persistence
documented in [storage](docs/storage.md). The complete product contract is defined
in [product completeness](docs/product-completeness.md), including the searchable,
provider-aware [Skills Library](docs/skills-library.md). The Windows
[reference browser](docs/reference-browser.md) provides browser-scoped agent
control and real image observations without another harness or Electron.

## License

Harness is released under the [MIT License](LICENSE).
