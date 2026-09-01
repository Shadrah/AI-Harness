# Final workspace UX pass

These issues are intentionally deferred until the remaining daily-driver
surfaces stop changing size and placement:

- Widen or make the composer model selector responsive. Provider-prefixed names
  such as `OpenAI Codex · …` are currently truncated too aggressively.
- Keep the existing minimize, maximize, and close hit targets, but increase and
  optically balance the glyphs inside them. The current symbols look undersized
  relative to their buttons.

The final pass should also verify maximized-window safe-area spacing, dropdown
legibility, keyboard focus, narrow-window behavior, and high-DPI scaling.
