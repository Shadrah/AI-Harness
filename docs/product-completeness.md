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

## 6. Subscription continuity

- Keep every workspace task bound to its own durable provider thread, context,
  approvals, model settings, and output history.
- Hand a task between eligible accounts under the same subscription provider at
  safe turn boundaries, preserving continuity without merging account quotas.
- Support manual, suggested, and opt-in automatic handoff, and stop honestly when
  no eligible account remains.
- Concurrent subagents, background task farms, and managed Git worktrees are not
  requirements for the initial production release.

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
- Browse the connected account's repositories in a dedicated in-app GitHub
  module; clone or open one as a Harness workspace without using a web page.
- Stage, unstage, diff, recover, commit, fetch, pull, and push without requiring
  Settings. Initialize Git, choose or rename a branch, attach an origin, or publish
  the current workspace from the GitHub module.
- Resolve the active repository at action time so workspace switching cannot
  target a stale project.
- Preserve large binaries locally and direct oversized history to Git LFS rather
  than silently attempting an invalid Git push.
- Keep GitHub account connection persistent in Settings; repository actions stay
  in the workspace surface.

## 9. Migration, import, and export

- Import projects and detected history from Codex and Claude Code, with portable
  transcript or project import as the universal fallback for other harnesses.
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
- Search cached and remote metadata by text, topic, repository source, status,
  and connected model/provider compatibility.
- Preview the selected source, provenance, compatibility, package tree, scripts,
  assets, revision, and install scope before installation.
- Keep discovery and update scans metadata-only. Download, provider setup,
  dependency installation, activation, and updates require an explicit user
  action for a selected skill and visible approval of consequential operations.
- Lock provider-specific skills to compatible targets; for portable skills, let
  the user choose among compatible connected providers/models and installation
  scope, while tracking setup state separately for every selected target.
- Install immutable, hashed copies into user or workspace scope; update, disable,
  re-enable, and recoverably remove them without requiring the source to remain.
- Adapt portable skills to each provider while clearly labeling provider-specific
  packages. Skills never bypass normal tool, network, filesystem, or approval
  policies. See [Skills Library](skills-library.md).

## 11. Personalization

- Apply saved personal instructions through each provider's supported native
  instruction path without inserting ghost transcript messages.
- Keep skills and built-in tools behind visible capability and permission controls.
- MCP hosting, an MCP marketplace, and general UI/plugin extensibility are
  post-release possibilities, not initial production requirements.

## 12. Workspace control

- Keep terminal, Activity, browser, task history, diff, approvals, and GitHub in
  focused modules that can be opened when needed rather than permanent clutter.
- Preserve keyboard focus, selection, and scroll position; long lists are
  virtualized and all high-volume surfaces remain responsive.
- Harness is not an IDE. Symbol indexing, an embedded code editor, and IDE-style
  navigation are outside the production scope.
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
