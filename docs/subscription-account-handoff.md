# Subscription account orchestration

Harness retains multiple named OpenAI Codex and Claude Code subscription profiles
with the same switching and continuity workflow. This is identity-aware routing,
not quota merging: every profile keeps its own
provider-owned credentials, available models, usage windows, billing mode, and
native provider thread IDs.

## Account profiles

The first profile uses the provider's existing home so current Harness installations
keep their sign-in. Additional OpenAI and Claude profiles live under Harness's
local application data in separate directories. Harness stores only credential-free profile
metadata in `subscription-identities.json`; portable backups do not include the
provider profile directories or authentication material.

Use **Settings → Providers** to add, select, activate, sign in, sign out, or
remove profiles. Adding a profile makes it active so its initial sign-in can be
completed. Removing an inactive profile removes its Harness registration but
does not silently delete provider-owned profile files.

## Handoff modes

Settings offers **Manual**, **Suggest**, and **Automatic** behavior. Suggest is
the default and presents a confirmation at 5% remaining in the provider-reported
five-hour window. The threshold is configurable from 1–25%. Harness keeps an
account fixed while a turn is running and evaluates normal low-usage routing only
after that turn settles.

The destination list is ordered by each account's last reported five-hour
remaining percentage. This value is explicitly last-known data; Harness does not
combine account meters or invent availability for profiles it has not checked.

## Routing policy boundary

The routing engine keeps each identity and provider quota separate. Manual
selection and confirmed handoff remain available for compatible profiles.
Automatic routing is explicitly user-selected and considers only participating
subscription profiles belonging to the current provider. Direct API/PAYG
connections are excluded. An account handoff is recorded as a visible durable
event, and individual meters remain inspectable at all times.

## Target automatic handoff behavior

Automatic handoff is an explicit opt-in configuration for subscription profiles.
It considers every enabled, authenticated identity under the active provider and
never crosses provider families. A Claude task may route only among eligible
Claude subscription accounts; an OpenAI task may route only among eligible
OpenAI subscription accounts. Direct API and pay-as-you-go connections are not
members of these subscription schedulers.

The selected identity stays fixed for the duration of a turn. At a safe turn
boundary, or after a limit response ends a turn, Harness chooses the best compatible
identity using live model entitlement, authentication health, five-hour and
weekly availability, reset times, billing mode, and user participation settings.
If no identity is eligible, Harness pauses the task and reports the earliest
known reset instead of silently selecting another provider or a billable API.

An automatic handoff retains the same Harness workspace and task. It creates a
new account-owned provider session because native session state cannot cross the
identity boundary, then supplies a durable continuation capsule derived from the
Harness transcript, accepted tool outcomes, current workspace state, context
files, instructions, model settings, and unfinished objective. The destination
must inspect current state before continuing so a partially executed command or
edit is not duplicated. After a provider limit interrupts a turn, Automatic mode
submits a visible Harness continuation message on the destination account; it will
not overwrite text the user has already entered in the composer.

## Account usage overview

Settings → Providers must show a compact usage overview for every connected
subscription identity without requiring the user to activate each account or
return to the main workspace. Each account card shows its display name, provider,
authenticated identity, plan, participation state, and the provider-reported
five-hour and weekly usage windows with remaining percentage and reset time.

Usage snapshots remain account-specific; Harness does not present them as a
combined quota. Every value includes its last-updated time and a visible stale,
refreshing, signed-out, rate-limited, or unavailable state. Harness must say "not
reported" when a provider omits a window; it must not estimate, fabricate, or
silently reuse another identity's usage. Refreshes run independently off the UI
thread and update the account cards in bounded batches.

## Continuity boundary

On a confirmed or automatic handoff, Harness:

1. stops the source profile's local provider runtime;
2. records an account-boundary event against the current Harness session;
3. clears only the native provider thread ID;
4. starts the destination profile's isolated runtime; and
5. adds a visible handoff marker to the durable local transcript.

No model generation is required to create the handoff. The next user message—or
the visible automatic continuation after a limit response—uses Harness's existing
local continuity builder to reconstruct a compact brief
from durable user directions, assistant results, turn reports, context files,
and workspace state. Consequently, a source account at 0% can still hand the
task to another profile.

Returning to a saved task restores the identity recorded for that task before
attempting to resume its provider thread. Account/profile work runs outside the
UI thread; only bounded state updates are dispatched back to the interface.
