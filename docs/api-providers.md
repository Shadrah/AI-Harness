# Direct API providers — first integration slice

Settings → Providers connects additional accounts without requiring another harness.
The existing Harness-managed Codex subscription runtime remains independent.

| Connection | Native wire protocol | Catalog source |
| --- | --- | --- |
| OpenAI API | Responses | `/v1/models` |
| Anthropic API | Messages | `/v1/models`, paginated |
| Gemini API | streaming GenerateContent | `/v1beta/models`, paginated |
| xAI / Grok | Responses | `/v1/language-models` |
| Mistral | Chat Completions | `/v1/models` |
| DeepSeek | Chat Completions | `/models` |
| OpenRouter | Chat Completions | `/api/v1/models` |
| Local / compatible | Chat Completions | configured base URL + `models` |

This is a conversational API integration, **not full feature parity** with every
provider's product. The adapters have offline protocol coverage but have not yet
been verified against paid accounts. Model-list access does not prove that a key
can make billable requests to every listed model. OpenRouter lists routing
candidates; an OpenAI-compatible endpoint is not a guarantee of full OpenAI API
compatibility. No account was charged during implementation.

## Setup

1. Choose a provider, enter its API key, and select **Connect / Refresh**.
   Multiple named connections are supported. Keys live in Windows Credential
   Manager; public connection metadata and explicit model overrides live in
   `%LOCALAPPDATA%/Harness/api-connections.json`.
2. Select a discovered model to inspect capabilities. A catalog that only lists
   IDs cannot tell Harness its modalities, context limit, or valid reasoning
   values. Unknown options are not inferred from model names. Enable tools/images
   or supply reasoning/tier values only after verifying that model's API contract.
   Overrides are opt-in, per connection and model, visibly labeled, and removable.
3. Select the connected model beneath the composer. API billing is separate from
   a consumer subscription. Your project's transcript remains in Harness.

Claude subscription credentials are not accepted: Anthropic's
[third-party authentication policy](https://code.claude.com/docs/en/legal-and-compliance)
requires an approved API route rather than Claude.ai subscription login. Other
consumer subscriptions are not silently treated as API credits either.

## Implemented

- Per-account catalog refresh at startup; no generation requests for discovery.
- Native text streaming, Unicode, output completion/error checks, and cancellation.
- Text/code contents and inline image attachments when image input is enabled.
  Inline turn attachments are limited to 20 MiB total and 1 MiB per text file;
  unsupported input fails visibly instead of becoming an invisible path reference.
- Provider-native conversation state retained in Harness's local SQLite event
  store, including signed/encrypted reasoning and tool-call IDs. Raw reasoning is
  not dumped into visible chat. Existing transcript imports and provider switches
  start from a bounded continuity brief, not another harness's runtime.
- Current personalization and root project `AGENTS.md` on every request.
- Project-relative list/read/write tools and explicit command execution.
  Tool output is bounded and goes to Activity, not a wall of visible chat text.
- Ask approves each write/command. Approve for me currently falls back to Ask for
  API execution. Full access explicitly bypasses approvals. **Commands are not
  OS-sandboxed.** File tools reject path traversal, Git internals, and junctions.
- Provider-reported input/output tokens, with latest input separated from
  cumulative processed tokens. Unknown account quotas and context limits remain
  unknown; no simulated five-hour/weekly meters for API connections.
- Current Git working-tree diffs for changed dirty files (up to 100 files).
  These may include pre-existing edits, and are labeled accordingly.
- No automatic billable retries or replay of interrupted tool calls. An interrupted
  native history is discarded in favor of transcript continuity on the next turn.
  Commands that were interrupted may have made partial changes.

## Still required before feature-complete / production certification

- Live account validation for each provider, including models without tool support,
  limited API keys, stream interruptions, reasoning variants, and quota failures.
- Native image generation, audio, video, hosted search/computer tools, citations,
  provider-native compaction, and additional sampling/budget controls. These are
  not advertised as working capabilities in the new adapter.
- Manual token-budget thinking (older Claude/Gemini contracts) and all provider-
  specific controls. The current reasoning selector forwards effort/level strings;
  it does not translate a token budget into a fabricated reasoning level.
- Shared sandbox/automatic-risk-review parity with Codex. The API runner is a
  bounded client-tool loop (40 requests per turn, 2-minute command timeout,
  24 MiB serialized request safety limit), not Codex's execution environment.
- API Skills Library activation/discovery, non-Git file-change snapshots, exact
  turn-only diffs across shell operations, context preflight and compaction, and
  cross-platform OS credential vaults. Skill installation currently targets Codex.
- Stream-error details with safe structured redaction. HTTP failures currently
  report status and remediation without dumping potentially sensitive response
  bodies. Submitted reasoning/tier values are not called provider-confirmed.

## Checks

`dotnet run --project tools/Harness.ApiCheck -c Release -- --startup-check`

Uses an isolated SQLite fixture to verify that lock contention does not block the
calling thread, event restoration uses its index, and a large model catalog is
published in one collection notification. `--startup-profile` separately performs
read-only timing queries against the local store, printing only aggregate counts,
query plans and timings, never message contents or credentials.

`dotnet run --project tools/Harness.ApiCheck -c Release`

This uses synthetic in-memory HTTP responses, never real credentials or model
calls. It covers four wire formats, native-state replay, model metadata/pagination,
tool approval boundaries, credential routing failures, Unicode, usage separation,
catalog coexistence, and a headless Providers settings preview.

Contract references: [OpenAI Responses](https://developers.openai.com/api/reference/cli/resources/responses/methods/create),
[Anthropic models](https://platform.claude.com/docs/en/api/http/models),
[Gemini models](https://ai.google.dev/api/models),
[xAI models](https://docs.x.ai/developers/rest-api-reference/inference/models),
[Mistral models](https://docs.mistral.ai/api/endpoint/models),
[OpenRouter reasoning preservation](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens).
