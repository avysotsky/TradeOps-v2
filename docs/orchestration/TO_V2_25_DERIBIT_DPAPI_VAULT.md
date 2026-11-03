# TO-V2-25 — Windows DPAPI credential vault for Deribit read-only smoke

**Stacked on:** owner Gate A+B accepted TO-V2-24 exact
\`d6e43490a25adcd3109dceeb444e797494fe4fbc\`. **Status:** Draft.
No changes to frozen TO-V2-24 code or accepted proof.

## Problem

The Windows owner had to copy and paste both Deribit API key ID and secret
for every read-only mainnet smoke run. Avoid repeatedly manipulating the
secret and leaving it in the Windows clipboard.

## Implementation

\`scripts/deribit/Deribit-ReadOnlyLocal.ps1\` runs ONLY on Windows and accepts:
- \`-Action Save\` — first time, masked \`Read-Host -AsSecureString\`
  input. Encrypt **each** string with PowerShell \`ConvertFrom-SecureString\`
  **without** a user-specified key: Windows DPAPI CurrentUser. Never store
  raw key material.
- \`-Action Status\` — reports only whether local encrypted vault exists.
- \`-Action Run\` — retrieves local ciphertext via
  \`ConvertTo-SecureString\`, exports process-local variables temporarily
  for the **existing** isolated Deribit read-only smoke executable.
  Refuses source if exact fetched GitHub branch HEAD mismatches
  \`-ExpectedHead\`. Builds **before decrypting**. Cleans environment
  variables and the detached temporary worktree in a finally block.
- \`-Action Remove\` — deletes local vault ciphertext; does not revoke
  the live API key on Deribit.

**Vault location (never inside repo):**
\`%LOCALAPPDATA%\TradeOps\Credentials\Deribit.Mainnet.ReadOnly.dpapi.json\`
The file is not portable plaintext JSON: credential values are distinct
DPAPI ciphertext strings. The directory and file use restricted ACL for
current Windows user and Local SYSTEM, with inherited ACL removed.
Reject filesystem reparse points.

This protects at-rest credentials from casual access and repo leaks; it
**does not** protect against malware/processes with same-user access,
local administrator compromise, debugging or credential theft from an
untrusted workstation. DPAPI may be unavailable after reinstalling
Windows or changing user profile; simply re-enroll using the Deribit UI.
Running on WSL/Linux or uploading this vault to GitHub is unsupported.

**Least privilege:** a separate Deribit production API key with no
trade:read_write or wallet rights. The exact future API permissions
cannot be inferred from ability to read: owner must independently verify
them in Deribit UI. No new execution capability is introduced.

## Qualification

Gate A WSL2 .NET restore/build/xUnit independent exact SHA, cleanup 6/6;
the Linux runner **cannot perform native Windows DPAPI execution**, so
that result is NOT PowerShell runtime proof. Owner Gate B is limited
to local \`Save\`, \`Status\` and \`Run\` with sanitized PASS output.
Never share the vault JSON, IDs, secrets, process environment or log
containing sensitive material. No key material included in this PR.
All local read-only API traffic remains three hardcoded Deribit mainnet
GET requests; no trade/cancel/transfer or changes in API/Worker.

Reference: https://learn.microsoft.com/powershell/module/microsoft.powershell.security/convertfrom-securestring
