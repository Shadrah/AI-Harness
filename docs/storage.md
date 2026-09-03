# Local storage

Harness stores durable application state in a versioned SQLite database at
`%LOCALAPPDATA%\Harness\data\harness.db` on Windows and the equivalent local
application-data directory on other platforms.

The first schema persists:

- projects, normalized workspace paths, and last-opened timestamps;
- multiple named sessions per project;
- ordered user, assistant, and turn-report conversation messages; completed
  provider items and structural events remain durable, while high-frequency
  streaming delta fragments are coalesced in memory and not written one row at
  a time;
- provider identity, thread ID, selected model, reasoning effort, and service tier;
- raw provider notifications for audit and future re-projection;
- context-file attachment metadata and content-addressed Harness-owned copies.
- cached Skills Library manifest metadata; repository source totals, index state,
  complete path count, hydrated-description count, revision, and refresh time;
  and installed-skill target, scope, source revision,
  content hash, package path, provider path, and provenance.

SQLite runs with foreign keys, write-ahead logging, and normal synchronous mode.
Message writes are upserts keyed by a stable local message ID. Final assistant
text replaces its in-memory streaming projection without producing duplicate
transcript entries or a database write for every token.
The application drains pending writes during normal shutdown, but correctness
does not depend on shutdown because mutations are persisted as they arrive.

Attached context files are SHA-256 hashed and copied beneath the data directory.
The session therefore remains usable if the original file is moved or removed.
Duplicate content within one session reuses its existing attachment record, and
an unreferenced Harness-owned blob is deleted when its final attachment is
removed. Original user files are never deleted. Files are currently limited to
25 MB each.

Provider delivery is tracked by content hash and provider thread. Text snapshots
are inserted into model input the first time they are needed by a thread instead
of relying on mention metadata alone; images are sent through native image input.
Later turns reuse the provider thread's context, while newly attached content is
delivered incrementally. Text snapshots longer than 512 Ki characters include
their first 512 Ki characters plus the retained snapshot path for tool-assisted
inspection of the remainder.

Provider credentials, browser cookies, access tokens, and device-login secrets
do not belong in this database. Connection credentials remain in the provider's
supported credential boundary or the operating-system credential vault.

When a saved session has a provider thread ID and that adapter supports resume,
Harness resumes that provider thread after restoring the local transcript. A
resume failure is shown explicitly and does not delete the local record.

Every applied conversation import stores its source path, retained Harness-owned
copy, SHA-256 hash, and an `harness/importBoundary` provider event containing the
scanner warnings and message count. Imported messages remain durable with an
`IMPORTED` status but are excluded from the visible new-session transcript. The
first successful model turn records `harness/importContextBriefV2Applied`; if a normal
provider thread must be rebuilt, Harness instead records
`harness/sessionContextReconstructed`.

Model controls are session state, not application defaults. Harness persists a
provider/model/reasoning/service-tier selection when any selector changes and
again when a provider thread is created. Restoring a session reapplies only
options currently advertised by that provider, then opens the transcript at its
latest message after the conversation surface has completed layout.
