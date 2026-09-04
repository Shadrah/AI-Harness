# Harness engineering requirements

## Responsiveness is a product requirement

Harness should be known for lightning-fast interaction. Preserve responsiveness
as features, histories, workspaces, and provider catalogs grow.

- The UI thread is for UI work only: input, rendering/layout, and small, bounded
  updates to UI-owned state. Do not run database operations, filesystem scans,
  credential-store access, network/process startup, provider stream parsing,
  large serialization, image decoding, or other heavy computation on it.
- An `async` method is not proof of nonblocking execution. Check synchronous
  work before its first await and continuation context after each await.
  Microsoft.Data.Sqlite's async calls still execute SQLite work synchronously.
- Keep service work off the UI context. Use asynchronous I/O where genuinely
  supported and worker execution for blocking APIs/CPU work. Dispatch only the
  small resulting UI update; never move UI-owned controls to a worker thread.
- Never block the UI using `.Wait()`, `.Result`, synchronous process waits, or
  sleeps. Propagate cancellation, observe background task failures, and handle
  shutdown without accessing disposed state.
- Startup must render a usable shell without waiting for unrelated provider,
  Git/GitHub, or catalog work. Run independent services independently and expose
  honest loading/failure states.
- Batch large collection updates, coalesce streamed deltas, bound activity
  output, and virtualize large lists where needed. Avoid per-item layout churn,
  redundant refreshes, and rebuilding unchanged UI state.
- Background results must not overwrite a newer workspace, session, selection,
  or active turn. Validate their target when applying them, not just when starting.
- For changes touching startup or heavy data paths, use focused responsiveness
  checks with slow/locked dependencies and large histories/catalogs. Measure
  caller/UI responsiveness separately from total background completion time.
  Never claim a performance improvement solely because the build passes.
- Keep verification proportional to the change. Avoid repeated broad smoke tests
  or paid model calls unless needed and authorized. Documentation-only edits do
  not require an application rebuild.

Existing focused check:
`dotnet run --project tools/Harness.ApiCheck -c Release -- --startup-check`

Do not trade away these requirements to add a feature faster. A responsiveness
regression is a product bug, not an acceptable cost of more functionality.
