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
| Ollama | Chat Completions | native `/api/tags` + `/api/show` |
| llama.cpp | Chat Completions | native router `/models` |
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
   For Ollama or llama.cpp, **Detect local** checks their default loopback ports
   without an API key, generation request, model download, or model load. A custom
   loopback port can also be entered on either local connection type.
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
- Explicit Ollama and llama.cpp connection types. Ollama discovery enriches its
  installed tags from native per-model capability/context metadata and excludes
  embedding-only entries. llama.cpp uses the router's native metadata catalog.
  Neither path guesses reasoning levels that the runtime did not report.
- A shared model/adapter conformance report separates **ready**, **reported but
  not implemented**, **model unsupported**, and **unknown** states. The primary
  workspace enables only ready features; Settings → Providers explains adapter
  gaps without erasing the provider's metadata.
- Preflight rejects a model from the wrong connection, unadvertised reasoning or
  service-tier values, unsupported tools, and unsupported or not-yet-implemented
  attachment modalities before an HTTP request is made.
- Native text streaming, Unicode, output completion/error checks, and cancellation.
- Text/code contents and native inline image attachments when image input is
  enabled. PDF input uses native Responses `input_file`, Anthropic `document`,
  or Gemini `inlineData` blocks. Gemini also supports inline audio and video.
  Turn attachments are limited to 20 MiB total and 1 MiB per text file;
  unsupported model/adapter combinations remain disabled and fail preflight
  instead of becoming invisible path references or being decoded as text.
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
- Per-model, opt-in Anthropic automatic prompt caching using the native five-minute
  cache control. Cache-read and cache-write token counts come from provider usage
  events and are recorded in Activity; Harness does not estimate cache hits.
- Per-model, opt-in OpenAI and Anthropic hosted artifact generation. Harness
  requests each provider's native code-execution output contract, accepts only
  provider file references, downloads at most 20 files and 100 MiB per file
  through the authenticated provider endpoint, uses atomic local writes, retains
  hashes and provider IDs in the native event stream, and adds clickable local
  links. Harness does not render or synthesize PDF, DOCX, or HTML itself.
- Current Git working-tree diffs for changed dirty files (up to 100 files).
  These may include pre-existing edits, and are labeled accordingly.
- No automatic billable retries or replay of interrupted tool calls. An interrupted
  native history is discarded in favor of transcript continuity on the next turn.
  Commands that were interrupted may have made partial changes.

## Still required before feature-complete / production certification

- Live account validation for each provider, including models without tool support,
  limited API keys, stream interruptions, reasoning variants, and quota failures.
- Native audio/video delivery for non-Gemini protocols, image generation, hosted
  search/computer tools, generated-file downloads for other protocols, citations,
  provider-native compaction, and additional
  sampling/budget controls. These are not advertised as working capabilities in
  the new adapter.
- Prompt-cache controls for non-Anthropic protocols, manual token-budget thinking
  (older Claude/Gemini contracts), and all provider-
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
calls. It covers four wire formats, native PDF/audio/video payloads,
native Ollama/llama.cpp discovery,
native-state replay, model metadata/pagination,
model/adapter conformance and request preflight,
tool approval boundaries, credential routing failures, Unicode, usage separation,
catalog coexistence, and a headless Providers settings preview.

Contract references: [OpenAI Responses](https://developers.openai.com/api/reference/cli/resources/responses/methods/create),
[Anthropic models](https://platform.claude.com/docs/en/api/http/models),
[Gemini models](https://ai.google.dev/api/models),
[xAI models](https://docs.x.ai/developers/rest-api-reference/inference/models),
[Mistral models](https://docs.mistral.ai/api/endpoint/models),
[OpenRouter reasoning preservation](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens),
[Ollama model details](https://docs.ollama.com/api-reference/show-model-details), and
[llama.cpp server routes](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).
