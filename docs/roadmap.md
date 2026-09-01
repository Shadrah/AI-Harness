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
- Inline approvals, working-tree inspection, diff review, and recoverable
  stage/unstage/revert actions are implemented. Integrated terminal access and
  broader patch-level apply controls remain.
- Durable projects, sessions, normalized messages, provider events,
  content-addressed context-file attachments, and restart recovery are
  implemented in local SQLite storage. Durable turn-level projections and
  additional attachment types remain.

## Implemented foundation

- Persistent Settings module for workspace visibility, personalization, startup,
  providers, imports, GitHub, and advanced controls.
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
- Anthropic subscription runtime and direct API connections.
- Local OpenAI-compatible endpoints plus explicit Ollama/llama.cpp discovery.
- Capability-complete adapters with conformance fixtures for streaming, vision,
  tools, reasoning controls, structured output, caching, audio, and generated
  artifacts wherever the provider exposes them.

## P3 — Skills Library

- Search public catalogs and configured GitHub sources by category, capability,
  provider, language, license, and compatibility.
- Inspect a skill's source, manifest, requested tools, permissions, and version
  diff before installation.
- Install pinned, hashed copies into user or workspace scope without requiring
  the source harness or catalog to remain available.
- Update, disable, fork, export, and remove skills; detect local modifications and
  avoid overwriting them silently.
- Community metadata and discovery remain separate from trust. Popularity is not
  permission, and every skill executes through Harness approvals and sandboxing.

## Release rule

A feature is not presented as available until its complete user-visible path is
implemented. Unsupported or unavailable provider state remains explicit; Harness
does not populate operational surfaces with representative data.

Deferred visual refinements are tracked separately in
[the final workspace UX pass](ux-backlog.md) so they are not lost while the
functional surfaces continue to change.
