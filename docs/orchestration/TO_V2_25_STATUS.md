# TO-V2-25 Status

- Scope: Windows CurrentUser DPAPI encrypted local credentials for Deribit
  **read-only** smoke; no network or trading code changes.
- Parent: qualified TO-V2-24 SHA
  \`d6e43490a25adcd3109dceeb444e797494fe4fbc\`.
- Script: \`scripts/deribit/Deribit-ReadOnlyLocal.ps1\`.
- Current development state: **Gate A PENDING; Windows owner Gate B NOT RUN**.
- Linux runner cannot execute Windows DPAPI; Windows owner acceptance
  must be reported distinctly with sanitized marker only.
- Encryption: Windows DPAPI CurrentUser, restricted file ACL outside repo,
  no portable encryption key stored.
- No changes to API/Worker, exchange capabilities, Deribit client, or
  PostgreSQL. No real account mutating calls or credentials provided.
- Next: independent exact SHA Gate A and owner one-time Save/Run smoke.
