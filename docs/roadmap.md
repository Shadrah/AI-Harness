# Product roadmap

The roadmap is ordered around one test: can Harness replace an existing daily
driver without keeping that driver installed?

## P0 — Complete working loop

- Harness-managed provider runtimes with visible version, source, health, and
  explicit update controls.
- Authenticated model discovery, model-specific modalities, and reasoning levels
  sourced from each connection rather than a built-in model menu.
- Streaming assistant text, reasoning summaries when exposed, tool lifecycle,
  command input/output, file-change patches, errors, token usage, and cancellation.
- Correct STOP semantics across every adapter: acknowledge immediately, invalidate
  the turn generation before cancelling transport, terminate Harness-owned provider
  and tool process trees, cancel pending approvals/browser actions, and discard all
  late deltas or terminal events from the stopped turn. Preserve partial output and
  mark it STOPPED—never COMPLETED—without permitting stale callbacks to restart work.
- Inline approvals, working-tree inspection, diff review, and recoverable
  file- and hunk-level stage/unstage/discard actions and integrated terminal
  access are implemented. Every discard creates a recovery copy first.
- Durable projects, sessions, normalized messages, provider events,
  content-addressed context-file attachments, and restart recovery are
  implemented in local SQLite storage. Durable turn-level projections and
  additional attachment types remain.
- Activity includes a durable project journal for meaningful workspace events
  and task milestones, alongside a separately bounded current-run detail stream.

## Implemented foundation

- Persistent Settings module for workspace visibility, personalization, startup,
  providers, imports, GitHub account connection, and advanced controls. All
  repository and branch operations live in the primary workspace Git surface.
- Provider-native personalization delivery and persistent Ask, safeguarded
  automatic review, and Full access permission modes are wired through thread
  and turn configuration.
- Active context occupancy is separated from cumulative provider throughput;
  chat streams auto-follow and expose a compact turn-level working phase.
  OpenAI Responses, Anthropic Messages, and Gemini expose explicit native
  pending-request token preflight; providers without a count contract remain unknown.
- Loss-aware transcript import for Markdown, text, JSON, and JSONL with preview,
  normalized durable messages, provenance, and a retained source copy.
- Installed-history detection for Codex and Claude Code, including selectable
  conversation previews, workspace hints, source IDs, and explicit loss reports.
- Direct Git/GitHub workflow for init, origin attach, repository creation,
  identity and initial-branch configuration, oversized-file preflight, commit,
  fetch, fast-forward pull, and push with visible workspace feedback.

## P1 — Replacement and migration

- Add OpenCode, Aider, Continue, Cline/Roo, Cursor, and compatible project-folder
  detectors; Codex and Claude Code conversation detection is implemented.
- Expand the working scan-and-preview importer with duplicate detection and
  source-specific loss reports.
- Copy all required context and attachments into Harness-owned storage while
  preserving source-native records for audit and future re-import.
- Resume imported conversations using reconstructed instructions, messages,
  summaries, tool results, model settings, and working directory.
- Export a provider-neutral Harness archive that another installation can open.

## P2 — Provider breadth

- OpenAI subscription runtime and direct API connections.
- Anthropic subscription runtime and direct API connections. Claude Code now
  supports sign-in/out, live account model/effort/fast-mode discovery, usage,
  structured streaming, native tool permissions, cancellation, continuation,
  attachments, diffs, provider-native skill paths, persistent isolated
  multi-account profiles, explicit account switching, and continuity handoff.
- The subscription orchestrator provides Manual, Suggest, and opt-in Automatic
  modes. It stores provider threads, exact live usage, billing mode, and model
  catalogs per identity without representing separate account meters as one quota.
- Automatic mode continuously hands a Harness task among
  all participating, eligible subscription accounts belonging to the same active
  provider. Keep an identity sticky during each turn; route at safe boundaries or
  after a limit response; preserve the Harness workspace and durable task context;
  and pause when every account is unavailable. Never cross from Claude to OpenAI
  (or vice versa), and never fall through to a direct API/pay-as-you-go connection.
- Settings includes a restrained usage overview with one card per subscription
  identity. Show its exact provider-reported five-hour and weekly remaining values,
  reset times, plan, scheduler participation, and snapshot freshness without making
  the user activate accounts individually. Missing data must read "not reported";
  refresh account probes independently off the UI thread and batch visual updates.
- Claude models use the shared provider/model visibility controls in Settings. The main model
  picker must contain only models both reported and usable by the selected Claude
  account; plan/API-credit-only models such as Fable must remain absent unless that
  account's live provider catalog confirms access. User-hidden Claude models stay
  hidden across refreshes and restarts.
- Local OpenAI-compatible endpoints plus explicit Ollama/llama.cpp discovery are
  implemented. Detection is user-invoked, metadata-only, and never downloads or
  loads a model.
- Implemented foundation: a shared model/adapter conformance report and strict
  preflight now prevent reported-but-unimplemented features or unadvertised
  controls from being sent. Offline fixtures cover current streaming, vision,
  tools, reasoning, metadata, native PDF delivery, Gemini audio/video input, and
  opt-in Anthropic prompt caching with provider-reported cache telemetry, OpenAI
  and Anthropic hosted artifact references/downloads, opt-in OpenAI Responses
  native compaction with restart-safe usage state, provider-native OpenAI,
  Anthropic, and Gemini input-token preflight, plus native-state paths across four
  wire formats.
- Complete the adapters and conformance fixtures for streaming, vision,
  tools, reasoning controls, structured output, caching, audio, and generated
  artifacts wherever the provider exposes them.

## P3 — Skills Library

- Implemented foundation: Settings library and command-strip shortcut, SQLite
  catalog/provenance, repository-level GitHub source totals, progressive
  description indexing, direct source search, topics/source/status filters,
  pre-download directory inspection, explicit confirmation, content hashing,
  Codex user/workspace `.agents/skills` setup, Claude Code user/workspace
  `.claude/skills` setup, and direct-API connection/model
  activation through bounded on-demand discovery and resource tools. Installed
  targets now expose integrity-aware metadata-only update review, atomic provider
  copy replacement with rollback, reversible disable/enable, and recoverable
  removal.
- Implement the complete discovery, compatibility, trust, installation, provider
  delivery, update, and removal contract in [Skills Library](skills-library.md).
- Search public catalogs and configured GitHub sources by category, capability,
  provider, language, license, and compatibility.
- Inspect a skill's source, manifest, requested tools, permissions, and version
  diff before installation.
- Install pinned, hashed copies into user or workspace scope without requiring
  the source harness or catalog to remain available.
- Add pin, fork, and per-skill export workflows. Update, disable/enable, recoverable
  removal, and local-modification detection are implemented.
- Community metadata and discovery remain separate from trust. Popularity is not
  permission, and every skill executes through Harness approvals and sandboxing.

## Release rule

A feature is not presented as available until its complete user-visible path is
implemented. Unsupported or unavailable provider state remains explicit; Harness
does not populate operational surfaces with representative data.

Deferred visual refinements are tracked separately in
[the final workspace UX pass](ux-backlog.md) so they are not lost while the
functional surfaces continue to change.
