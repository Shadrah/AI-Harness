# Portable backup and restore

Harness creates a portable `.harness-backup` archive from **Settings → Workspace**.
The export uses SQLite's online backup API, so the database is a consistent
snapshot rather than a copy of a live database and its write-ahead log. Export
is unavailable while a model turn is active, and all archive, hashing, and file
work runs away from the UI thread.

## Included

- projects and their Harness task/session history;
- messages, turn reports, and provider continuation metadata;
- application settings, personalization, and model-picker preferences;
- retained context attachments and conversation-import snapshots;
- the cached Skills Library catalog, source inventory, and Harness-managed
  installed skill folders; and
- API connection names, provider types, endpoints, and model overrides.

Every payload has a byte length and SHA-256 digest in `manifest.json`. Restore
rejects unknown formats, duplicate or undeclared entries, path traversal,
payload changes, newer database schemas, more than 200,000 files, or more than
20 GiB of expanded content.

## Excluded

The archive never contains API keys, ChatGPT/Codex or GitHub authentication,
browser cookies and profiles, crash diagnostics, managed runtimes, or project
source trees. These remain in their existing
provider, operating-system credential, workspace, or machine-specific boundary.

Global installed skills are restored beneath the current user's agent-skills
directory. Workspace-scoped skills are restored only when that project folder
exists. If it moved, Harness retains the package as disabled managed content and
activates it when the restored workspace is selected or relinked. Existing unrelated skill
folders are never overwritten; a collision receives a restored suffix.

The archive does contain conversations and retained context and is not
encrypted by Harness. Store it with the same care as private source code.

## Restore lifecycle

Selecting **Restore backup…** validates the complete archive and stages a private
copy beneath Harness's local application-data directory. It does not modify the
running database. Close and reopen Harness to apply it.

On the next launch Harness renders its shell, then performs restore work on a
worker before opening storage. Managed attachment/import paths are rebased to
the current machine, the database receives an integrity check, and payloads are
copied before the database is replaced. The prior database is kept beside the
new one as `harness.db.before-restore`; prior API connection metadata and SQLite
sidecars are also moved aside before replacement. A restore notification is
shown after startup.

Workspace source directories are not part of an archive. If a restored path no
longer exists, clicking that workspace opens a folder picker. Choosing its new
local directory updates the existing project record, preserving every restored
task and conversation.
