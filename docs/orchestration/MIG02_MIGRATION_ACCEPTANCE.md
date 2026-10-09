# MIG-02 — Private v2 migration acceptance and public v1 freeze

## Status (2026-10-09)
**CODE MIGRATION + PRIVATE CI + SOURCE-EQUIVALENT HTTP INTEROP VERIFIED. PUBLIC v1 ARCHIVE ACTION STILL PENDING.**

## Private repositories
- TradeOps-v2: `avysotsky/TradeOps-v2`, private, canonical active development.
- DocFlow-v2: `avysotsky/DocFlow-v2`, private, canonical active development.
- Public source commit snapshots: TradeOps `77229d7f4f835b8cf43eb0aeae427d2df509bb11`; DocFlow `602346f90aed9ca9f940ac38ad1847249e3c81df`.
- Fresh private histories were created without original Git commits. Retained TradeOps v1 blobs: 545, DocFlow blobs: 73, all identical to pinned originals. 34 historical TradeOps docs/probe files omitted.
- TradeOps-v2 full build `37910596850`: **SUCCESS, 576/576 tests, 0 warnings and 0 errors**.
- DocFlow-v2 Python CI `37910125274`: **SUCCESS, 120 passed and 13 skipped**.
- DocFlow-v2 .NET CI `37910115571`: **SUCCESS**.
- DocFlow-v2 .NET/Python normalized text parity `37910372073`: **SUCCESS, 13/13**.

## Two-service compatibility gate
- Real DocFlow ASGI FastAPI `POST /api/v1/extractions` served locally (no LLM keys), using pinned public DocFlow source bytes matching the initial DocFlow-v2 imported tree.
- Real TradeOps.Infrastructure `DocFlowHttpExtractionAdapter` (not a fake .NET client) sends typed extraction request and validates returned normalized / structured JSON.
- Validated canonical SHA-256 document ID, segment ID and fingerprint shape, deterministic result, structured validation, fail-closed unsupported schemaVersion (422), no echoed raw text on malformed request.
- `MIG02 Dual-Service HTTP Contract Smoke` workflow run **37912246018 — SUCCESS** on PR #1; full TradeOps build `37912245654 — SUCCESS`.
- PR #1 merged squash commit `87ed1b3b4f2c2418a554f1fb3a2fada5c4f5b001`.
- **Scope limitation:** this tests byte-equivalent frozen DocFlow v1 Python source, not a direct private DocFlow-v2 checkout or production LLM provider. When v2 diverges, replace the pinned public-source checkout with a securely authenticated private GitHub App checkout, or versioned private application package artifact. No promise of future v2-v2 equivalence until then.

## Public portfolio preservation
- Public repositories remain `avysotsky/TradeOps` and `avysotsky/DocFlow`, still Public. Do not rename, privatize or delete them.
- Portfolio summaries added at `ENGINEERING_SNAPSHOT_V1.md` and linked at top of README (documentation-only changes).
- Public source SHAs and existing historical links were verified accessible.
- Public source workflows/logs expire per GitHub retention; dated test results preserved in the public snapshot reports.
- Before owner performs archive, verify `Repo → Settings → General → Danger Zone → Archive this repository`, then inspect both Public and Archived flags and deep links.

## Security and next steps
- No broker order placement, new executable signals, production LLM credentials, or private repo content pushed into public code.
- Both v2 repos must remain private; any source export from v2 back to a public repo requires separate explicit publication approval.
- Evaluate private code scanning/CodeQL entitlement and protected branch policies under repository Security/Settings; do not assume old public setup carries over.
- The GitHub integration's available actions **cannot change repository archived state**; archive toggle must be performed manually by repository owner through GitHub UI.
- Once owner archives the v1 originals, re-read GitHub repository metadata and verify Public+Archived and old URLs. Mark MIG-02 fully completed only after that.
