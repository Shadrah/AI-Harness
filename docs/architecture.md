# Architecture

Harness uses a small provider-neutral core with native desktop and runtime
adapters around it. The desktop application can expose provider-native features
without forcing every provider into a lowest-common-denominator API.

```text
Avalonia desktop application
    |-- workspace / tasks / activity trace
    |-- conversation + multimodal composer
    |-- context, diff, terminal, approvals
    |-- provider-native model controls
    |
Harness.Core
    |-- projects, sessions, turns, content parts
    |-- per-model capabilities and constraints
    |-- normalized event stream + raw extensions
    |-- import plans and provenance
    |
Runtime adapters                         Storage and imports
    |-- Codex app-server / CLI              |-- SQLite event store
    |-- Claude Code                          |-- portable Harness archive
    |-- OpenAI / Anthropic APIs              |-- Codex and Claude history
    `-- local OpenAI-compatible servers      `-- popular harness projects
```

## Process boundary

Harness is a replacement harness, not a shell around another harness. No Codex,
Claude, Cursor, Continue, Cline, or similar desktop application may be required
after migration. Their presence can make discovery and import convenient, but
normal operation must continue after the source application is uninstalled.

Subscription-backed integrations run through a provider-supported runtime when
that is the provider's supported subscription boundary. Harness can provision
and update that runtime in Harness-owned application data, or use an explicit
user-selected executable. It must not silently bind to a different desktop
harness's private runtime. Harness does not extract browser cookies or impersonate
a consumer web client. Direct APIs use user-supplied API credentials stored
through the operating system's credential vault. Local models remain local and
can be reached through an embedded adapter or an OpenAI-compatible endpoint.

Runtime processes are isolated behind JSON-RPC or structured standard I/O when
available. This keeps provider SDKs and rapidly changing protocols out of the
desktop/core dependency graph.

The Harness-managed Codex adapter installs the official platform package rather
than only the top-level CLI executable. Package completeness requires both the
CLI and its sibling code-mode host; command-runner, sandbox, and search resources
remain in the official package layout. Child processes discard inherited private
`CODEX_*` pipes, thread IDs, and sandbox flags from a parent harness while
preserving an explicitly configured `CODEX_HOME`. This prevents a development
launch from silently depending on Codex Desktop internals.

The Codex adapter reads `model/list` for each model's input modalities, default
reasoning effort, complete `supportedReasoningEfforts` list, and advertised
service tiers. The UI builds its reasoning and speed-tier controls from that
specific model descriptor and replaces them when the selected model changes.
The shell owns no cross-provider option list. Provider defaults remain explicit;
for Codex, a null service tier is the provider's standard/default path while an
advertised `priority` tier is presented using the provider's own display name.
The selected values are sent on `turn/start`, and the effective values are read
back from `thread/settings/updated` rather than inferred from assistant prose.
It reads
`account/rateLimits/read` for authenticated subscription windows and maps
primary/secondary windows by duration instead of assuming that every account has
one five-hour and one weekly bucket. Refreshes use the account rate-limit update
stream or a low-frequency status poll; neither operation starts a model turn.
The selected runtime version and source are visible and auditable. If an old
runtime reports an old catalog, Harness reports that limitation and offers an
explicit runtime update; it never supplements the response with guessed models.

## Capability preservation

Capabilities and controls belong to a model descriptor, not merely to a provider. The core
currently names text, vision, tool use, reasoning, image generation, audio input,
audio output, prompt caching, and computer use. Each capability will grow typed
constraints such as accepted MIME types, maximum image count, tool schema limits,
reasoning controls, and streaming event support.

Before a run, the application validates every content part and requested feature
against the selected model. Switching models may surface an incompatibility or an
explicit conversion; Harness never silently drops an image, tool, structured
output request, or provider-specific option.

Images are canonical content parts. They preserve their path or URI, MIME type,
alternative text, requested detail, dimensions when known, and content hash.
Adapters translate the canonical part into the provider's native protocol.

## Event model

Every session is an append-only event stream. Stable event kinds cover model
output, tool calls, approvals, file changes, context changes, errors, and usage.
An extension payload retains the provider-native record. The activity rail is a
compact projection of this stream, not a separate UI-only timeline.

Streaming follows bounded channels with cancellation. Large binary attachments
are content-addressed outside the event rows. SQLite is the intended default
store; archives use JSON Lines plus an attachment directory for portability.

The initial SQLite implementation checkpoints normalized messages during
streaming, retains raw provider notifications, and stores provider thread IDs
alongside model settings. Project/session selection restores the local timeline;
adapters with a supported resume operation also restore the provider-side thread
before another turn is sent. Session deletion cascades only through Harness-owned
records and requires a user confirmation in the interface.

Workspace Git operations run through argument-list process invocation rather
than shell-composed commands. Status uses NUL-delimited porcelain output so
spaces and unusual characters in file names remain unambiguous. Stage and
unstage affect only the selected path. A working-tree revert requires explicit
confirmation and first writes a timestamped recovery record beneath Harness
application data; tracked files are copied and untracked files are moved there,
never deleted. Git commands reject absolute paths and paths escaping the
repository root.

## Import pipeline

Imports follow `detect -> scan -> preview -> apply`. A detector identifies the
source harness and versions. A scanner produces an immutable plan with counts,
warnings, conflicts, and excluded secret material. Only an approved plan writes
to Harness storage.

Initial targets are Codex, Claude Code, OpenCode, Aider, Continue, Cline/Roo,
and straightforward project folders with instruction/context files. Importers
preserve source IDs, timestamps, model metadata, raw records, and attachment
links whenever the source provides them.

Import is a one-way ownership transfer into Harness storage. Once applied,
projects, normalized conversations, context files, attachments, memories, tool
events, file-change records, and retained raw records are addressable without
the source harness. Source credentials are neither copied nor required; the user
connects the equivalent provider inside Harness.

"Continue with the same context" means every exportable input is reconstructed:
ordered messages, instructions, selected files, summaries, attachments, tool
results, working directory, model settings, and provider-native records. Hidden
server state or proprietary context that a source does not expose cannot be
recreated. Import preview must identify those losses before applying the import,
and the resumed conversation must retain an auditable import-boundary event.

The first shipping importer accepts exported Markdown/text transcripts and
role/content JSON or JSONL records. It scans before writing, previews message
counts and the loss boundary, normalizes only recognized user/assistant roles,
and copies the original export into content-addressed Harness storage. Imported
messages become a new durable session and never depend on the source harness.
They do not become hundreds of visible messages in the new chat. On its first
model turn, Harness constructs a compact private continuation brief from at most
eight short excerpts spanning the opening contract and recent stopping point,
then sends the user's new prompt after that brief. The brief is capped at 12,000
characters. The retained raw export remains local and is not attached to the
model turn, because hidden model input still consumes context tokens.
An `harness/importContextBriefV2Applied` event makes that handoff auditable and prevents
the import from being injected again into an already-hydrated provider thread.
Imported sessions created by the earlier transcript-envelope implementation lack
this versioned marker, so Harness starts one fresh provider thread with the brief
instead of resuming the already saturated provider context.

The same reconstruction path protects ordinary Harness sessions when a saved
provider thread can no longer be resumed: durable local conversation becomes
private background continuity for a new provider thread instead of forcing the
model to infer state from the working tree.

Context capacity is read from provider usage events. Harness displays actual
tokens against the provider-reported model context window; it does not maintain
a guessed per-model limit table. At the configured safety threshold, the adapter
requests the provider's native thread compaction so its continuation summary and
tool state remain protocol-compatible.

Detected history is grouped by source harness and normalized project path before
the user chooses anything. A project import opens or creates that Harness
workspace, restores each detected conversation as its own task, and copies known
root instruction files such as `AGENTS.md`, `CLAUDE.md`, and repository guidance
into each imported task's Harness-owned context storage. Reimporting the same
source path into the same workspace is skipped.

Codex rollout discovery distinguishes user-owned root conversations from internal
runtime files. Any session whose metadata source is a `subagent` (including
guardian checks), or whose originator is Harness itself, is excluded from the
candidate list. Existing imports of those records are reclassified as internal
and hidden without deleting their retained data. Root JSONL is streamed up to
1 GiB because real long-running project histories routinely exceed 200 MB; the
former 50 MB cap selected small internal logs while rejecting the actual chat.
Active history files are opened with read/write sharing, and import takes a fixed-
length content-hashed snapshot so the retained source and provenance hash agree.

Each source project marks exactly one `LATEST CONTINUATION`. With internal records
removed, this is the newest user-owned root conversation and becomes the default
imported task. Compact continuity selection reserves space for recent unresolved
checkpoint language—awaiting, pending approval, blockers, in-progress validation,
and explicit resume points—rather than relying only on the final few messages.

The workspace rail is backed by every persisted project, ordered by most recent
use. Selecting a workspace swaps its task list, active provider thread, context,
working tree, and repository state without requiring a new application window.

## Settings and repository boundary

Low-frequency persistent configuration lives in the Settings module: workspace
visibility, imports, personalization, startup behavior, provider management,
GitHub account connection, and advanced controls. Repository initialization,
branch naming, origin attachment, GitHub repository creation, commit, pull, push,
and the working tree are workspace actions and remain in the primary UI.

Repository support uses argument-list `git` and `gh` processes. Harness can
initialize a repository, attach or update `origin`, create a GitHub repository,
configure a repository-local commit identity, choose the initial branch, commit,
fetch, fast-forward pull, and push the current branch. Repository creation is a
single publish transaction: preflight, stage, initial commit, remote creation,
and upstream push. Files above GitHub's 100 MiB regular-Git limit are preserved
locally and added to `.git/info/exclude`; a sole unpublished initial commit can
be amended automatically to recover from a rejected first push.
Every remote mutation requires a direct user action. GitHub credentials remain
in the system credential store managed by GitHub CLI and are never copied into
the Harness database.

Workspace changes clear cached repository state immediately. Each primary Git
action then resolves the selected workspace and current branch again before it
runs, so an in-flight background refresh cannot send an action to the previously
selected repository.

## Skills Library boundary

The planned Skills Library is a first-class Harness subsystem rather than an
installer tied to another agent. It will discover public skill packages from
configured registries and GitHub sources, support category and capability search,
show source/version/license/permissions before installation, and install a pinned
copy into the user's Harness environment.

Installed skills remain usable if the catalog or original harness disappears.
Updates are explicit and diffable. A skill package is untrusted code and
instructions: manifests are validated, content is hashed, requested tools and
permissions are declared, secrets are never bundled, and execution remains under
the same approval and sandbox policy as any other tool.

## Dependency policy

`Harness.Core` has no UI or provider SDK dependencies. The desktop ships only
the Avalonia packages required for native rendering. Optional adapters should be
separate assemblies or external processes and load only when configured. This
keeps startup, memory use, and idle background work measurable and controllable.
