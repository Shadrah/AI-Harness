# Product completeness contract

Harness is complete when it can be a user's primary development harness without
requiring another harness to remain installed. This document defines that product
surface before release engineering and distribution polish begin.

A feature is complete only when its entire path works: discovery, configuration,
execution, live feedback, cancellation, persistence, restart recovery, errors,
and removal. A button or provider call by itself is not a completed feature.

## 1. Workspaces and sessions

- Open, create, remove, and switch multiple workspace folders without reordering
  the workspace rail or leaking state between projects.
- Create, rename, resume, archive, delete, search, and export sessions.
- Restore the active workspace, session, provider thread, attachments, model
  settings, working tree, and unfinished-operation state after restart.
- Show context capacity, compaction state, and an auditable continuity boundary.

## 2. Provider connections

- Connect subscription-backed OpenAI Codex and Anthropic Claude runtimes without
  depending on either vendor's desktop harness.
- Connect OpenAI and Anthropic APIs using credentials held by the operating-system
  credential vault.
- Connect local OpenAI-compatible servers and discover Ollama and llama.cpp.
- Install, select, update, repair, disable, and remove provider adapters.
- Report connection identity, runtime version, source, health, freshness, and
  provider errors without synthetic fallback data.

## 3. Model capability fidelity

- Discover models from the authenticated provider connection.
- Surface each model's actual modalities, context window, reasoning controls,
  service or speed tiers, tool support, caching, structured output, and limits.
- Validate a turn before sending it and never silently drop unsupported content.
- Preserve provider-native settings and events alongside normalized projections.
- Run every adapter through the same capability conformance suite.

## 4. Agent execution loop

- Stream assistant text without overwriting earlier conversation history.
- Stream compact reasoning summaries, plans, tool lifecycle, commands, files,
  progress, errors, token usage, and a final detailed turn report.
- Pause for approval, accept or decline safely, cancel a turn, recover a crashed
  runtime, and resume or reconstruct a provider thread.
- Keep high-volume command output in Activity rather than flooding the chat.
- Support background work and explicit notification without hiding failures.

## 5. Tools, terminal, and sandbox

- Provide an integrated terminal scoped to the active workspace.
- Expose provider tools and Harness tools through a typed permission model.
- Support workspace-write and read-only sandboxes, command approvals, network
  approvals, timeouts, cancellation, and clear process ownership.
- Retain a bounded, searchable activity log with redaction before export.
- Prevent one workspace, provider, plugin, or skill from inheriting another's
  private process state or credentials.

## 6. Agent orchestration

- Run one or more independent tasks per workspace, with explicit ownership of
  their provider thread, process tree, approvals, context, and output artifacts.
- Delegate bounded subtasks and merge their reports without silently sharing
  credentials, mutable chat state, or unreviewed instructions.
- Offer isolated Git worktrees for concurrent code changes, make their branch and
  base revision visible, and provide reviewable merge or discard paths.
- Queue, pause, resume, retry, and cancel background tasks; preserve useful state
  across restart and clearly distinguish waiting, blocked, failed, and complete.
- Never let a background task mutate the active workspace merely because the user
  switched projects while it was running.

## 7. Context, files, and artifacts

- Attach text, source, images, PDFs, audio, directories, and provider-supported
  artifacts with hashes, MIME types, size limits, and visible delivery state.
- Maintain workspace instructions, session context, user memory, and provider
  compaction as distinct layers.
- Preview exactly what will be sent to a provider and how much context it uses.
- Store generated or changed artifacts outside chat text and make them inspectable,
  diffable, exportable, and removable.

## 8. Git and GitHub

- Initialize repositories, choose and rename branches locally and remotely,
  attach origins, create repositories, and manage visibility.
- Stage, unstage, diff, recover, commit, fetch, pull, push, publish releases, and
  surface conflicts without requiring Settings.
- Create and switch branches or worktrees, open and inspect pull requests, show
  checks and review state, and make every remote mutation explicit.
- Resolve the active repository at action time so workspace switching cannot
  target a stale project.
- Preserve large binaries locally and direct distributable artifacts to Git LFS
  or Releases rather than silently attempting an invalid Git push.
- Keep GitHub account connection persistent in Settings; repository actions stay
  in the workspace surface.

## 9. Migration, import, and export

- Import projects from Codex, Claude Code, OpenCode, Aider, Continue, Cline/Roo,
  Cursor, and portable transcript or project exports when source data exists.
- Group detected history by source harness and project, identify the latest root
  continuation, preview loss, and retain source provenance.
- Copy required context and attachments into Harness-owned storage.
- Export and restore a provider-neutral Harness archive containing projects,
  sessions, settings, events, context, attachments, memories, and installed skills.
- Never import credentials or claim unavailable hidden state was recovered.

## 10. Skills Library

- Discover public skills through searchable catalogs and GitHub sources without
  requiring users to know a repository or skill name in advance.
- Populate and refresh the local catalog unobtrusively in the background after
  startup; browsing never waits for GitHub, while direct searches may fetch and
  stream additional remote results on demand.
- Search and filter by task category, language, framework, provider compatibility,
  capability, license, trust state, popularity, and update recency.
- Preview source, instructions, scripts, assets, dependencies, permissions,
  supported providers, version history, and install scope before installation.
- Keep discovery and update scans metadata-only. Download, provider setup,
  dependency installation, activation, and updates require an explicit user
  action for a selected skill and visible approval of consequential operations.
- Lock provider-specific skills to compatible targets; for portable skills, let
  the user choose among compatible connected providers/models and installation
  scope, while tracking setup state separately for every selected target.
- Install immutable, hashed copies into user or workspace scope; update, pin,
  disable, fork, export, and remove them without requiring the source to remain.
- Adapt portable skills to each provider while clearly labeling provider-specific
  packages. Skills never bypass normal tool, network, filesystem, or approval
  policies. See [Skills Library](skills-library.md).

## 11. Personalization and extensibility

- Apply global, provider, workspace, and session instructions with visible
  precedence and token cost.
- Support MCP servers, tools, skills, provider adapters, and UI extensions through
  versioned manifests and permission declarations.
- Provide enable, disable, update, inspect, and uninstall paths for every extension.

## 12. Developer navigation and control

- Provide workspace file search, symbol/text search, clickable file and line
  references, an editor handoff, command palette, and configurable shortcuts.
- Keep terminal, Activity, diff, approvals, artifacts, and repository details in
  focused modules that can be opened when needed rather than permanent clutter.
- Preserve keyboard focus, selection, scroll position, and per-workspace layout;
  long lists are virtualized and all high-volume surfaces remain responsive.
- Expose accessible names, logical tab order, screen-reader status, scalable text,
  high-contrast behavior, and non-color-only success or failure indicators.

## 13. Operations and recovery

- Back up and migrate the database transactionally; detect corruption and provide
  an export or repair path.
- Provide local diagnostic logs, health checks, dependency checks, offline states,
  and redacted issue bundles.
- Update Harness and managed provider runtimes independently, with rollback.
- Work across supported Windows, macOS, and Linux builds with keyboard, screen
  reader, high-DPI, and reduced-motion verification.

## Release levels

- **Preview:** one provider and one operating system may be supported, but every
  limitation is explicit and user data remains recoverable.
- **Beta:** at least OpenAI, Anthropic, and local models pass capability and
  restart-recovery conformance; import/export and Skills Library install paths work.
- **1.0 replacement:** every section above has an end-to-end supported path and
  Harness can replace the named daily-driver harnesses without hidden dependencies.
