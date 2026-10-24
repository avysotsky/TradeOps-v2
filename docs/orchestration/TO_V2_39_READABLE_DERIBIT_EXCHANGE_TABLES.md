# TO-V2-39 — Readable Deribit exchange tables

The owner has confirmed that the React mainnet read-only terminal opens and
shows real Deribit position rows. Its previous table CSS was unsuitable for
operator use: column headings were 10px, numeric monospace cells 11px, and
other cells 12px.

This targeted UX change increases **both position and open-order** tables:
- headings: 14px at the default setting; higher contrast and less tracking;
- ordinary and monospaced values: **16px default**, 1.5 line-height;
- controls: **A− / A+** beside the position counter adjust both tables
  from 14px through 20px in 2px steps, without restart;
- status pills and cancel buttons: 14px with larger hit areas;
- consistent tabular digits and preserved red/green PnL/sign contrast;
- 840px min-width, horizontal scroll on narrow screens so the browser
  never crushes values into unreadable columns.

The zoom preference lives only in React component state (no secrets or
browser storage). It resets to the new **16px default** on reload.

The owner qualified PR #41 SHA
`919317718ed494d030102d4b4ff3a42f09b9bb68`
for read-only live account position rendering; this PR is intentionally
stacked on that frozen SHA. The owner's position amounts/instrument details
are intentionally NOT copied into GitHub.

The isolated one-command launcher is unchanged except for its pin-checked
Git branch name, so it runs the new PR's code. It still builds React and
.NET, auto-authorizes read-only GETs without prompting, uses the owner's
existing Deribit DPAPI read-only vault, and forces both trading execution
flags to false. No new trading backend, credential handling, database,
migration, route, broker call, or external data export is added.

Owner run + tests of this new PR SHA: NOT YET VERIFIED. Keep Draft.
