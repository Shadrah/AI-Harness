# Interface direction

The interface is a **precision instrument**: compact like a multiplexer, but able
to reveal images, diffs, approvals, model controls, and long-form reasoning when
needed. It deliberately avoids a card-heavy web dashboard aesthetic.

## Visual system

| Token | Hex | Role |
|---|---:|---|
| Graphite | `#11151B` | primary canvas |
| Deck | `#181E26` | raised working surfaces |
| Seam | `#29313C` | dividers and structure |
| Paper | `#D8DEE8` | primary text |
| Signal | `#65C7D0` | active model, focus, success |
| Approval | `#E2A84A` | attention and file changes |

Inter is the interface face. Cascadia Mono, JetBrains Mono, or Consolas renders
paths, code, counters, shortcuts, and telemetry. Corners remain tight, animation
is unnecessary for core navigation, and focus indicators must stay visible.

## Desktop anatomy

```text
┌ command strip: workspace / task / model / run ──────────────────────┐
│ navigator │ activity trace │ conversation + composer │ inspector   │
│ projects  │ model           │ messages               │ capability  │
│ tasks     │ tool            │ images / artifacts     │ context     │
│           │ approval        │ prompt                  │ working tree│
├──────────────────────────── optional terminal / diff drawer ───────┤
└ status: runtime / tools / tokens / branch ─────────────────────────┘
```

The activity trace is the signature surface. A thin continuous line records
model turns, tool work, approvals, file changes, and context mutations alongside
the conversation. It provides the temporal clarity of terminal output while the
main canvas remains readable.

The command strip is also the window title surface. It owns window dragging,
double-click maximize/restore, and quiet minimize/maximize/close actions at the
far edge. This removes unrelated operating-system chrome without disguising or
removing expected desktop behavior.

The inspector and navigator must be collapsible. At narrow widths the trace
reduces to icons, then becomes a temporary overlay; the conversation is always
the last surface sacrificed.

Working-tree files and diff content live in a dedicated modeless module. The
main inspector shows only the current branch, change count, and module launcher;
it never expands into an always-visible file list. Turn-level diff state follows
the same rule: the inspector exposes only its count and open action.

The diff module renders structured unified diffs rather than undifferentiated
code text. Every content row carries old/new line numbers, an explicit add or
remove marker, and a semantic background color; hunk headers and Git metadata
remain visually distinct. The header reports total added and removed lines.

## Interaction rules

- The composer behaves like a chat input: `Enter` sends; `Ctrl+Enter` and
  `Shift+Enter` insert a line break. Sending is disabled only when the prompt is
  empty, no model is available, or a turn is already running.
- Model changes immediately recompute enabled composer inputs and controls.
- Model and reasoning selection live in the composer footer. Reasoning choices
  come from the selected runtime's model catalog and are never a global list.
- The inspector shows every quota window reported by the active connection,
  including remaining percentage and reset time. Preview data must say preview;
  stale and authentication-required states remain explicit.
- The Session Context inspector shows current tokens against the model context
  window only after the provider reports both values. Imported history remains
  a small private continuation brief rather than filling the visible conversation;
  the raw export is never ghost-attached. At 85%, Harness requests provider-native
  compaction, reports a missing acknowledgement after 15 seconds, shows request
  acceptance, then waits for the provider completion event or a confirmed token
  drop. A 45-second confirmation timeout prevents an indefinite `COMPACTING` state.
- The workspace rail lists every persisted project. Selecting one swaps directly
  to its task history. Installed-harness migration is project-first: choices are
  labeled by source software and project, with their chats nested in the preview,
  rather than presented as one timestamp-heavy global list.
- Unsupported attachments remain visible and explain why a run cannot start.
- Images support paste, drag-and-drop, file selection, thumbnails, and a full
  inspection view with detail-level controls when the provider offers them.
- Destructive tools and provider permission prompts enter the activity trace and
  receive an inline approval surface near the relevant turn.
- The command palette is the universal escape hatch for keyboard operation.
- Reduced-motion preferences disable nonessential transitions.
- Healthy provider connections do not show recurring setup controls. Runtime
  update checks, catalog refreshes, and usage refreshes happen in the background;
  sign-in or installation actions appear only for first-time setup or a broken
  connection.
- The conversation renders only user prompts, model responses, and one bounded
  evidence-based turn report. Commands, reasoning, tool calls, and raw output
  live in the separate Activity module.
- Activity retains at most 200 events per turn and 48 KiB of visible detail per
  event. Streaming deltas are coalesced on a 100 ms UI cadence so verbose builds
  cannot monopolize the render thread.
- Turn Diff and Working Tree share the structured unified-diff presentation:
  old/new line numbers, hunk headers, red removals, green additions, and exact
  added/removed totals.
