# Production readiness

Harness's production feature scope is closed. Work after this point is limited to
correctness, continuity, interface refinement, packaging, and release operations.
New product ideas should be recorded for a later release rather than added to the
initial installer.

## Product boundary

The initial production release is a lightweight desktop AI harness with durable
workspaces and tasks, subscription and API providers, model-native capabilities,
tools and approvals, context and attachments, computer use, terminal and diffs,
Git/GitHub workflows, imports, account handoff, and the Skills Library.

The initial release is not an IDE, GitHub Desktop replacement, MCP host or
marketplace, multi-agent task farm, or full project-management client. It does not
require symbol indexing, an embedded editor, pull requests, issues, releases,
managed MCP servers, or automatic importers for every third-party harness.

## Gate 1 — Stability and continuity

- OpenAI subscription and Claude subscription connections retain authentication,
  account identity, model selection, reasoning, service tier, usage, and task
  continuation across restart.
- Direct API and local-model connections expose only the capabilities their live
  catalog and Harness adapter both support. Unsupported state stays disabled and
  never receives invented fallback values.
- Stop immediately blocks late provider, tool, approval, browser, and terminal
  output from mutating the completed UI state.
- Context occupancy comes from provider-native usage or count endpoints. Providers
  without an exact contract remain visibly unknown rather than estimated.
- Personal instructions, imported context, retained attachments, native
  compaction, and account handoff survive a close/reopen cycle.
- The GitHub module lists the authenticated account's repositories and completes
  initialize, branch, origin, create/publish, clone/open, commit, pull, and push on
  a disposable certification repository.
- Database migration, portable backup/restore, moved-workspace relinking, and
  unclean-shutdown recovery preserve user-owned data.
- Startup, workspace switching, catalog loading, and long conversations remain
  responsive with slow or unavailable provider, GitHub, filesystem, and database
  dependencies.

Gate 1 passes when focused offline checks are green and one deliberate live pass
has exercised each configured subscription provider and the disposable GitHub
repository. Paid model calls are not repeated as general smoke tests.

## Gate 2 — Final interface pass

- Remove the unreachable legacy repository setup dialog now superseded by the
  GitHub module.
- Make the composer model selector responsive enough for provider-prefixed names.
- Optically balance the minimize, maximize, and close glyphs without shrinking
  their hit targets.
- Verify maximized-window safe areas, dropdown legibility, keyboard focus, tab
  order, chat auto-follow, long-list scrolling, narrow layouts, and high-DPI text.
- Review loading, empty, disabled, success, failure, and cancellation states in
  every focused module. No control should imply work succeeded before it did.
- Remove redundant labels and controls while retaining tooltips and accessible
  names for icon-only actions.

Gate 2 passes when the production shell and every modeless window have been
reviewed at 100%, 150%, and 200% scale with representative large collections.

## Gate 3 — Signed Windows installer

- Publish a self-contained Windows x64 build from a clean checkout with no preview
  data, development secrets, local databases, logs, or test artifacts.
- Resolve the current Git and GitHub CLI dependencies explicitly: bundle and
  service approved versions, or detect them and provide a truthful guided setup.
- Install application files separately from user data. Upgrade and uninstall must
  preserve `%LOCALAPPDATA%\Harness` unless the user explicitly chooses removal.
- Create Start menu and optional desktop shortcuts, registered uninstall metadata,
  versioned upgrades, and repair behavior.
- Sign the application executable and installer through Azure Artifact Signing;
  verify signatures and timestamps on the produced artifacts.
- Produce SHA-256 checksums and test install, upgrade, repair, and uninstall on a
  clean Windows virtual machine and a standard non-administrator account.

Gate 3 passes only when the exact downloadable installer has completed the clean
machine test. A successful local `dotnet publish` is not an installer pass.

## Gate 4 — Open-source release

- Finalize the license, third-party notices, privacy/security statement, supported
  provider matrix, installation guide, troubleshooting, and data-location guide.
- Document what is provider-reported, what Harness verifies, which external
  runtimes are managed or required, and what consumes paid API or subscription
  usage.
- Publish release notes, signed installer, checksums, known limitations, and a
  reproducible source tag matching the shipped binaries.

## Post-release backlog

- Pull requests, issues, release publishing, and deeper GitHub account surfaces.
- MCP connection management or marketplace functionality.
- Concurrent subagents and managed Git worktrees.
- Additional source-specific harness importers.
- Skills community reviews, forking, and marketplace features.
- IDE-style file, symbol, or editor navigation.
