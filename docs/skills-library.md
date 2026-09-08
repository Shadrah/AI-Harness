# Skills Library

The Skills Library is Harness's searchable, provider-aware package manager for
reusable agent instructions, scripts, references, assets, and tool integrations.
It is not a list of links and it is not tied to one model vendor.

## Discovery model

Harness combines three source types:

1. Curated registries containing reviewed manifests and immutable release hashes.
2. User-configured GitHub organizations, repositories, topics, and catalog files.
3. GitHub search candidates that contain a recognized skill entry point such as
   `SKILL.md` or a Harness skill manifest.

GitHub-wide search is a discovery feed, not a trust signal. Harness groups those
matches by repository, then runs a repository-scoped `filename:SKILL.md` query.
That second query records the source's complete GitHub-reported count even when
the repository contains far more entries than GitHub will return in one page.
Descriptions are cached progressively from the current browse or search result,
deduplicated by repository and path. The UI always distinguishes `REPORTED` from
`DESCRIPTIONS CACHED`; a partial index is never presented as a complete catalog.

Sync also discovers repositories through the `agent-skills`, `claude-skills`, and
`codex-skills` GitHub topics. For each verified source, Harness creates a partial,
no-checkout Git clone with blob filtering and enumerates the commit tree. This
bypasses code search's finite result window and records every `SKILL.md` path in
that repository without downloading skill package blobs. Names and paths become
locally searchable immediately; manifest descriptions hydrate progressively from
direct searches.

## Catalog lifecycle

The Skills Library always opens from Harness's local catalog database. It never
blocks the window, workspace restoration, or library navigation while waiting on
GitHub. Catalog maintenance follows two distinct paths:

- After Harness startup, a low-priority background job conditionally refreshes
  configured catalogs, known repositories, installed-skill update metadata, and
  previously discovered entries. It respects ETags, last-modified values, rate
  limits, offline state, metered-network preferences, and a configurable refresh
  interval. Only one refresh runs at a time, and it yields to model turns and
  other interactive work.
- A direct user search returns local matches immediately and then searches the
  loaded GitHub sources for matching `SKILL.md` files. Harness reads only those
  manifests to obtain names, descriptions, inferred topics, and compatibility,
  then caches them for future browsing. An explicit source refresh uses the same
  interactive path.

Startup refresh failures are non-blocking and retain the last successful catalog.
The library shows catalog age, refresh state, source-specific errors, and the next
eligible refresh time without repeatedly surfacing background notifications.
Installing a result always revalidates its source commit and manifest, even when
the search result came from the cache.

Catalog refresh is metadata-only. It caches the exact `SKILL.md` path inventory
from a blob-filtered, no-checkout Git tree and hydrates manifest descriptions
progressively. Large inventories are written in short background batches that
release the catalog store between commits, keeping workspace navigation and
search responsive. Refresh does not check out package files, download repository
archives, install dependencies, run scripts, create provider resources, or
activate a skill. Harness never silently downloads or applies a skill package.

## Canonical manifest

Harness uses a provider-neutral `harness-skill.json` manifest when available and
can derive a provisional manifest from recognized provider formats. It records:

- stable ID, name, summary, categories, keywords, license, author, homepage;
- source repository, path, commit SHA, release/tag, content hash, and signature;
- compatible operating systems, providers, models, tools, and Harness versions;
- entry instructions, scripts, references, assets, and optional templates;
- filesystem, process, network, credential, MCP, and external-application needs;
- installation scope, conflicts, dependencies, and update channel.

Provider-native files remain intact. The normalized manifest describes them; it
does not rewrite a Claude-specific skill into a supposedly portable one.

## Search and browsing

The library supports free text plus facets for:

- engineering, game development, design, research, data, documents, media,
  productivity, DevOps, testing, security, and other maintained categories;
- language, framework, engine, file type, and operating system;
- OpenAI/Codex, Anthropic/Claude, provider-neutral, and local-model compatibility;
- required capabilities such as vision, terminal, browser, MCP, image generation,
  audio, or network access;
- verified source, reviewed source, signed release, license, installed state,
  popularity, recency, and update availability.

Every result shows why it matched and whether its compatibility is declared,
verified by Harness, inferred, or unknown.

`SKILL.md` is not treated as proof of universal model compatibility. Harness
classifies a manifest from evidence:

- `Portable Agent Skill` uses the open frontmatter surface and can be offered to
  connected runtimes whose adapters implement that standard.
- `Codex extension` and `Claude Code extension` require their matching provider
  adapter. Claude-only invocation fields and dynamic command injection are not
  silently offered to Codex models.
- mixed or unknown extensions remain unverified and do not appear under a
  specific-model compatibility filter.

The UI lists concrete models reported by connected providers, but compatibility
is resolved by the model's provider runtime and the manifest format. Harness does
not maintain a hardcoded list claiming that one GPT or Claude version understands
a skill differently when the provider exposes skills at the runtime level.

## Trust and installation

Installation begins only from an explicit user action on a selected GitHub or
catalog result. Harness first resolves the package's declared compatibility:

- A provider- or model-specific skill has its target fixed and clearly labeled.
  Harness blocks installation when that target is not connected or compatible.
- A portable skill presents the compatible connected providers and models. The
  user chooses a target and whether installation applies globally or only to the
  current workspace. `ADD TARGET` repeats the reviewed install for another
  connection, model, or scope without conflating their state.
- A skill with missing or unverified compatibility can be installed only through
  an advanced flow that explains what is unknown; Harness does not guess that it
  works with every model.

Harness then shows the exact commit, file tree, source diff, license, dependencies,
executable content, requested permissions, provider setup operations, and warnings.
No package payload is downloaded until the user confirms this setup. Installation
then:

1. downloads a repository archive or release asset pinned to a commit/tag;
2. verifies the expected hash and optional signature;
3. rejects path traversal, symlinks escaping the package, hidden credentials,
   oversized packages, and undeclared executable content;
4. copies an immutable package into Harness-owned storage;
5. records provenance and grants no runtime permission automatically;
6. invokes each selected provider adapter's standard skill setup path;
7. reports per-target success or failure and activates only successful targets in
   the selected user or workspace scope.

Provider installation uses a stable namespaced identity derived from the original
name, repository, and catalog ID. Two upstream skills called `review` therefore
remain separate and never overwrite one another. Harness keeps the original
package immutable, adapts only the provider-facing copy's identity, and rebuilds
a local provider index. Codex reads names and descriptions from `.agents/skills`
and loads the full instructions only when selected; installed skills therefore
become discoverable without adding every skill body to every chat turn.

For a compatible direct-API connection, Harness keeps a separate provider-facing
copy keyed by connection, optional model, and user/workspace scope. A tool-capable
model receives two read-only tools: `list_skills` searches the active installed
catalog, and `read_skill_resource` loads `SKILL.md` or another bounded UTF-8 file
only when needed. The active set is rebuilt off the UI thread for each turn, so
installing a skill makes it available on the next turn without restarting or
rewriting another skill. Traversal, junctions/symlinks, binary content, Harness
metadata, and resources over 512 KiB are rejected. Package scripts are never run
by this adapter and skill text grants no tools, permissions, or higher authority.

Provider setup may copy files, upload a native bundle, or register instructions,
but it may not install external dependencies, execute package scripts, or request
new privileges without a separate visible approval. A failed multi-target setup
does not pretend the skill is universally installed; Harness records each target's
state independently and offers retry or rollback.

Removing a source repository never breaks an installed version. For an update,
Harness compares the cached old package with the new revision's Git metadata and
shows added, changed, and removed paths before downloading. Installations created
with an integrity baseline are re-hashed before update or removal; a modified copy
gets an explicit destructive warning, while older installations with no baseline
are labeled unknown rather than assumed clean. The active folder swap retains a
rollback copy until its database record commits.

Pinned package-cache directories are namespaced by catalog identity and revision.
Harness rechecks their provenance, file count, byte count, and aggregate content
hash before reuse; unsafe revision paths, altered cache content, and reparse points
are rejected instead of silently becoming an installation or update.

Disabling moves the provider-facing copy outside its active discovery directory
without deleting the pinned package or provenance; enabling moves it back and
rebuilds the provider index. Removal moves the copy to a local recovery directory
before deleting its installed-state record. A storage failure restores the copy
and index so the filesystem and database do not silently diverge.

## Provider delivery

Each provider adapter declares the skill mechanisms it supports:

- native provider skill reference or uploaded skill bundle;
- filesystem skill package exposed to a subscription runtime;
- inline instructions and approved supporting files;
- unsupported, with an explicit reason.

Harness currently implements Codex filesystem delivery and provider-neutral
direct-API delivery for models whose verified adapter surface includes tool use.
The latter does not claim a provider-native skill API: Harness supplies its own
bounded read tools while retaining the pinned local package and provenance.
Future native provider bundles can be added behind the same adapter contract when
a provider documents and exposes them.

## Catalog integrity

- A catalog never executes package code while indexing.
- Popularity and stars do not imply safety or compatibility.
- Search descriptions are untrusted content and never become instructions.
- Credentials stay in connection vaults and are referenced only through explicit
  runtime grants.
- Reports, blocks, publisher allowlists, and local trust decisions are separate
  from upstream deletion or popularity.

## Initial implementation slices

1. Local installed-skill inventory and canonical manifest/parser.
2. Local catalog browsing, startup background refresh, GitHub-backed direct
   search, cache, facets, refresh status, and detail preview.
3. Explicit install confirmation, target provider/model and scope selection,
   pinned archive download, validation, setup, rollback, and removal.
4. Direct-API on-demand delivery, followed by additional provider-native or
   filesystem adapters as their contracts are implemented and verified.
5. Updates, diffs, signing, community catalogs, ratings, and reporting.
