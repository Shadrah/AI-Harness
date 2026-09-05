# Subscription account handoff

Harness can retain multiple named OpenAI Codex subscription profiles. This is an
account switch, not quota pooling: every profile keeps its own provider-owned
credentials, available models, usage windows, and native provider thread IDs.

## Account profiles

The first profile uses the existing Codex home so current Harness installations
keep their sign-in. Additional profiles live under Harness's local application
data in separate directories. Harness stores only credential-free profile
metadata in `subscription-identities.json`; portable backups do not include the
provider profile directories or authentication material.

Use **Settings → Providers** to add, select, activate, sign in, sign out, or
remove profiles. Adding a profile makes it active so its initial sign-in can be
completed. Removing an inactive profile removes its Harness registration but
does not silently delete provider-owned profile files.

## Low-usage prompt

The prompt is enabled by default at 5% remaining in the provider-reported
five-hour window. The threshold is configurable from 1–25%. Harness never
interrupts a running turn and never switches silently. It waits for the turn to
settle, shows the source account and remaining usage, and requires the user to
choose and confirm a destination profile.

The destination list is ordered by each account's last reported five-hour
remaining percentage. This value is explicitly last-known data; Harness does not
combine account meters or invent availability for profiles it has not checked.

## Continuity boundary

On confirmation, Harness:

1. stops the source profile's local provider runtime;
2. records an account-boundary event against the current Harness session;
3. clears only the native provider thread ID;
4. starts the destination profile's isolated runtime; and
5. adds a visible handoff marker to the durable local transcript.

No model generation is required to create the handoff. The next user message
uses Harness's existing local continuity builder to reconstruct a compact brief
from durable user directions, assistant results, turn reports, context files,
and workspace state. Consequently, a source account at 0% can still hand the
task to another profile.

Returning to a saved task restores the identity recorded for that task before
attempting to resume its provider thread. Account/profile work runs outside the
UI thread; only bounded state updates are dispatched back to the interface.
