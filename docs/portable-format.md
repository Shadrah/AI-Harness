# Portable format

The portable directory format is designed for loss-aware migration between
Harness and other agents.

```text
project.harness/
|-- manifest.json
|-- project.json
|-- instructions/
|-- conversations/*.jsonl
|-- context/
|-- memories/
|-- skills/
|-- tools/
|-- attachments/
`-- raw/<source>/
```

`manifest.json` records the format version, creation time, source harnesses,
content hashes, and the features present in the archive. Each imported item has
provenance containing its source harness, source ID, source path when safe to
retain, import time, and content hash.

Unknown source records may be stored under `raw/`. Secrets must never appear in
portable archives. Connections that require authentication are represented as
redacted configuration plus an `authentication_required` status.

An applied import is self-contained. References to files owned by the source
harness are copied into content-addressed Harness storage or reported as missing
during preview; they are never left as required live dependencies. The manifest
records an import boundary and a loss report for source state that was not
exportable, including hidden prompts, server-side memory, unavailable tool output,
and missing attachments.

## Source-specific history import

Harness detects Codex and Claude Code JSONL histories without requiring either
application to remain installed. The user previews one conversation before
import. Visible user and assistant messages are normalized, while the complete
selected source file is copied into Harness-owned storage with its source kind
and content hash.

Codex `subagent`/guardian rollouts and sessions whose originator is Harness are
internal execution records, not user conversations, and are excluded. Large root
rollouts are streamed rather than rejected by a small file-size cutoff. Project
previews identify the latest root continuation explicitly.

Workspace hints and source conversation identifiers are provenance only.
Authentication material is never imported. Tool calls, hidden reasoning,
proprietary checkpoints, and server-side state that cannot yet be reconstructed
are disclosed in the preview instead of being represented as complete.

Imported conversation history drives a clean Harness chat in the background.
The visible transcript begins with the user's next prompt, while a compact
continuation brief preserves selected early instructions and recent results for
the provider. The raw export stays in local Harness storage and is not sent as a
model attachment. Oversized histories are shortened explicitly; Harness never
describes omitted records as recovered.
