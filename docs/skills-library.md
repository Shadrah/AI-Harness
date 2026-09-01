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

GitHub-wide search is a discovery feed, not a trust signal. Results are cached,
deduplicated by repository and path, and refreshed conditionally. Search-rate
limits and reset times remain visible; Harness backs off rather than scraping.

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

## Trust and installation

Before installation Harness shows the exact commit, file tree, source diff,
license, dependencies, executable content, requested permissions, and warnings.
Installation then:

1. downloads a repository archive or release asset pinned to a commit/tag;
2. verifies the expected hash and optional signature;
3. rejects path traversal, symlinks escaping the package, hidden credentials,
   oversized packages, and undeclared executable content;
4. copies an immutable package into Harness-owned storage;
5. records provenance and grants no runtime permission automatically;
6. activates it only for the selected user or workspace scope.

Removing a source repository never breaks an installed version. Updates are a
new immutable version with a reviewable diff; local modifications require an
explicit fork or conflict decision.

## Provider delivery

Each provider adapter declares the skill mechanisms it supports:

- native provider skill reference or uploaded skill bundle;
- filesystem skill package exposed to a subscription runtime;
- inline instructions and approved supporting files;
- unsupported, with an explicit reason.

OpenAI's API exposes project skills as versioned bundles that can be created,
listed, downloaded, updated by default-version pointer, and referenced by version.
Harness may use that native path for an authenticated API connection while still
retaining its own local pinned copy and provenance. Subscription runtimes and
other providers use their supported delivery mechanisms rather than an assumed
common protocol.

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
2. GitHub connection-backed catalog search, cache, facets, and detail preview.
3. Pinned archive download, validation, install/remove, and user/workspace scope.
4. Codex and Claude filesystem delivery adapters plus provider conformance tests.
5. Updates, diffs, signing, community catalogs, ratings, and reporting.
