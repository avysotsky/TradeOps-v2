# Bounded Deribit private-session probe

Explicit diagnostic tool for [B3 read-only capture](DERIBIT_READONLY_CAPTURE_HOST.md).
The [isolated inbox variant](DERIBIT_LIVE_INBOX_QUALIFICATION.md) is selected only with
`-WithIsolatedInbox`; the following default-mode contract remains DB-free.
It reuses the existing connector lifecycle; it is not an additional API/Worker
registration or a deployment. No database or sender implementation is referenced.

## Protected local input

The Windows launcher `scripts/deribit/Deribit-PrivateSessionReadOnlyLocal.ps1`
requires an exact reviewed HEAD and a clean tracked source tree. The owner's
untracked launchSettings is preserved. Build completes before credential decryption.

The existing CurrentUser-DPAPI vault is reused; keys are not requested again.
A recent, separately acquired HTTP account-summary observation supplies the native
account ID/type. The launcher verifies that its protected credential-ID fingerprint
still matches the vault. The native ID is the account pin; the fingerprint is only
a key-rotation check, never an account identity. Changed pins/credentials fail closed.

A separate encrypted qualification profile is retained locally under ignored
`.tradeops/b3-private-session/`. It is explicitly unverified and does not activate
a production binding. Plaintext credentials/IDs are neither written to disk nor
passed in command-line arguments. The short-lived reviewed child receives bounded
JSON over redirected stdin; environment exports, raw replies and private stderr
are not persisted. Existing workspace ACLs are retained; stored payload protection
is Windows CurrentUser DPAPI, with its usual same-user trust boundary.

```powershell
./scripts/deribit/Deribit-PrivateSessionReadOnlyLocal.ps1 -ExpectedHead <reviewed-40-character-SHA> -ObserveSeconds 45
```

The probe accepts main or subaccount identity. Its in-memory diagnostic AccountKey
names the authenticated native entity; for a subaccount the optional marker repeats
the known native ID. This is **not** an inferred parent account. The default protocol-only
mode never persists it, promotes it to a production hierarchy or uses it to send orders.
Production parent/tenant mapping still needs the separately governed server binding.

## Public preflight and private session

Public catalog queries cover explicit BTC future and option groups. The probe
requires inverse BTC-settled metadata for both groups and validates native units;
mixed/unknown settlement is refused rather than silently filtered. This intentionally
limited probe does not qualify linear, other-currency, combo or account-wide coverage.

The existing read-only lifecycle checks authentication, the pinned account ID/type,
the exact subscription ACK and correlated heartbeat challenges/replies. Aggregate
diagnostics count these milestones, epochs, normalized events, capture returns and
typed problems; they contain no scope identifiers, labels, raw frames or tokens.

The observation window is 30–60 seconds after subscription. A successful result
requires one healthy epoch, confirmed auth/account/subscriptions, at least one
acknowledged heartbeat challenge, no typed failure and completed socket shutdown.
Reconnect or an unanswered challenge cannot become a successful quiet-session result.

The capture port deliberately refuses private events. If an actual order/execution
event is normalized, the probe stops with `PRIVATE_EVENT_OBSERVED_NOT_STORED`.
It never fabricates an Inserted/Duplicate result. `LIVE_SESSION_PASS_NO_DURABLE_CAPTURE`
therefore proves the quiet live protocol session only, with zero durable writes.
`DatabaseUsed`, `IndependentlyVerifiedAccount`, `AccountCoverageQualified` and
`TradingReady` remain false. No real event is published as a fixture or public log.

## Granted-scope normalization

The request always explicitly asks for `connection trade:read account:read` and
`wallet:none block_trade:none block_rfq:none`. Unrequested permissions may inherit
key rights, so request-side denial must not be removed. See [public/auth scope
semantics](https://docs.deribit.com/api-reference/authentication/public-auth) and
[access scopes](https://docs.deribit.com/articles/access-scope).

Live response-shape inspection showed denied categories omitted from granted scope
and an opaque `name:` item. A narrower `trade:none` auth experiment also removed
the trade grant. No values of the opaque item, account ID or tokens were logged.
The validator now requires exactly one connection, trade:read and account:read;
explicit :none or omission is accepted only for the three deliberately denied
categories. Any elevated/additional permission, missing required read grant,
duplicate token, unknown category or ambiguous name/marker is rejected. A bounded
single name item is ignored as metadata and never used to determine account identity.

This observed response-format compatibility is not a general allowlist of future
server scopes. Unknown formats continue to fail closed. All successful epochs
remain RECOVERING; this diagnostic neither implements B4 recovery nor B5 fencing.
