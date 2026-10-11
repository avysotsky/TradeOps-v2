# Ubuntu JobRadar Relay v0.3 deployment record

**Transport branch only** — isolated, private, never merge.

The Ubuntu v0.3 agent is delivered as the ChatGPT conversation artifact
`UbuntuJobRadarRelay-v0.3.zip`; it has NOT been installed on any user
machine by this commit and is not checked into this repository.

SHA-256 of the release ZIP:
`119b3ebe545b4b118ef12f946cb87630a84f900c60b57f337a88157c84bbc1bf`

Local deployment target: `/workspace/relay-install`.

Read `README_RU.md` contained in the ZIP. Install as a non-root user.
Configure private outbound HTTPS to GitHub, and never paste credentials
into GitHub, logs, or ChatGPT. Required GitHub fine-grained PAT: isolated
access to this private repo, Contents read/write. Note: PAT is not
branch-scoped, so its permitted writes are broader than the relay branch.

Supported actions are strictly allowlisted. The executor has no
free-form shell action, no incoming listener and no external SSH address.
Its isolated output is sanitized before being sent to `results/`.

A synthetic `status` task is already pending in `tasks/`.
This is not an end-to-end claim: Ubuntu execution has not been verified.
