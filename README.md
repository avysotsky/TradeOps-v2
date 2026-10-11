# JobRadar Local Relay — isolated private transport branch

This branch is reserved for opt-in local-machine task transport, not for any
TradeOps source or release. Do not merge it into main.

- `tasks/*.json`: immutable requests with a narrow allowlist and no secrets.
- `results/*.json`: bounded sanitized results (never raw logs, passwords or
  real job-search records).
- Task execution happens locally in an explicitly configured worktree on D:.
- No GitHub Actions, background ChatGPT tasks, application submissions, or
  production database access.
- All task/response artifacts remain in this PRIVATE repository.
- The user may stop the local relay process or revoke GitHub credentials.
- Git history still retains deleted items: never put personal information here.

Transport contract: `version=1`; `id` a UUID; `action` one of
`status`, `privacy_tests`, `dotnet_tests`, `dotnet_testdb`,
`list_source`, `read_source`, `write_source`.
