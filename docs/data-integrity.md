# Production data integrity

Harness has a strict production rule: **displayed operational data must be
source-backed and accurate.** A production build must never substitute mock,
sample, estimated, or invented values when provider data is missing.

## Release invariants

- Models, capabilities, modalities, reasoning levels, defaults, context limits,
  service tiers, prices, and availability come from the connected runtime or a
  versioned authoritative provider catalog.
- Subscription usage, credits, token counts, rolling windows, weekly limits, and
  reset times come from the authenticated provider response. Harness preserves
  the provider's units and semantics.
- Every metric carries source, connection identity, retrieval time, and freshness.
- Missing values display `Unavailable` or `Not reported`; authentication failures
  display `Sign in to view`. Zero is never used to represent unknown.
- Stale values remain visibly stale and show their last successful refresh time.
- Provider errors remain errors. They do not fall back to fixture data.
- Development fixtures may exist only behind an explicit development-data source.
  They must be labeled `Preview` and excluded from production packaging.
- Importers retain provenance and raw source records so normalized values can be
  audited against their origin.

## Release gate

A production release is blocked while any ordinary application path constructs
representative model or usage data. Automated release checks must start the app
with no provider connection and verify that it shows an honest empty/unavailable
state rather than populated metrics.
