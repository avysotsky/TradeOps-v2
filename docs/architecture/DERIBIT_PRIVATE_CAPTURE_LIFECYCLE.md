# Deribit private read-only capture lifecycle

This increment extends the existing `DeribitPrivateWebSocketStream` and
`DeribitWebSocketMessageParser`. `RunCaptureAsync` is an explicit capture entry point;
the legacy testnet worker still selects `IExchangeEventStream.RunAsync`. No new
host registration, credentials, application-role deployment or live connection is
enabled automatically. [Capture storage contract](OMS_PRIVATE_EVENT_CAPTURE.md).

## Binding and read-only protocol

Server composition supplies an `OmsPrivateCaptureCoordinator` over the durable port,
activates a complete AccountKey, and supplies a `DeribitPrivateCapturePlan`. The plan
pins the expected remote account ID/type and a bounded immutable instrument manifest
(native name, future/option kind, trading currency, amount unit/currency). The manifest
is trusted configuration, not a symbol parser or an assumption of account-wide coverage.

Endpoint validation requires exact official WSS host/path/port for the bound
testnet/mainnet environment, without userinfo/query/fragment. The legacy entry point
retains its testnet-only validation. One adapter instance cannot run legacy and
capture modes simultaneously; one session cannot acquire two capture runners.

The capture path can send only:

- public/auth with explicit connection, trade:read, account:read, wallet:none,
  block_trade:none and block_rfq:none scopes;
- private/get_account_summary with extended=true to compare the authenticated
  connection's account ID, main/subaccount type and queried currency with the pin;
- public/set_heartbeat, private/subscribe, and public/test.

Missing, duplicate, elevated or unrecognized granted scopes fail closed. Authentication
and account mismatch stop the transport before releasing buffered events. API errors
are sanitized to typed reasons; auth/account payloads, token strings and remote error
messages are not logged. There is no placement/cancel/replace RPC in this path.

Official references: [authentication/scopes](https://docs.deribit.com/api-reference/authentication/public-auth),
[extended account identity](https://docs.deribit.com/api-reference/account-management/private-get_account_summary),
[heartbeat protocol](https://docs.deribit.com/api-reference/session-management/public-set_heartbeat).
These protocol checks are implemented but live response/permission eligibility is
not qualified by offline fixtures.

## Receive, capture and recovery

One receive path correlates RPC results and processes subscription/heartbeat messages
throughout the handshake. Subscription notifications before its ACK are normalized
and buffered, then released only after account verification and the exact complete
subscription ACK. Native numbers/units come from explicit manifest metadata; no
contracts fallback, cumulative clamping, invented timestamp or leaves calculation.
Unknown instruments/channels and malformed facts produce a recovery outcome.

The socket wrapper bounds the entire fragmented message, rejects binary/invalid text,
and decodes UTF-8 only after reassembly. A bounded normalized-event channel separates
the single capture writer from the socket reader, so slow PostgreSQL commits do not
block heartbeat responses. Overflow tears down the epoch and records a recovery
reason; it does not silently discard events while declaring success.

Defaults: 256 KiB/message, 128 events/message, 256 queued events; RPC/capture deadlines
15 s, receive deadline 45 s, heartbeat interval 20 s; reconnect delay 1–30 s. A pending
test_request reply has its own bounded deadline even if other notifications arrive.
Connection tokens are renewed by a new connection/epoch before expiry (80% of
expires_in, capped at 10 minutes). No refresh token is persisted or reused across sockets.

Every new epoch requires reconciliation. There is deliberately **no READY state** in
this capture coordinator. Reconnect/renewal/overflow can leave uncommitted queued
observations; B4 must recover the outage with snapshots/history/fills. A successful
subscription or a quiet/empty stream is not a coherent snapshot or cancellation proof.
`LastFailure` survives phase changes/reconnects for the current binding; it is process
metadata, not a durable gap ledger. Restart starts from the same blocked recovery gate.

Capture collisions remain durably quarantined; capturing later events continues while
the recovery reason stays visible. A capture-store failure stops the transport with
a sanitized CaptureFailed outcome; host supervision/recovery is required before retry.

## Account/epoch barrier

Coordinator SwitchAsync waits for an in-flight capture transaction to finish, changes
the active binding and cancels the old session. After that barrier old session/epoch
captures and status updates are rejected. Cancellation callbacks run outside the
commit barrier. BeginEpoch likewise prevents delayed previous-epoch work from committing.

On account switch the host must await the old RunCaptureAsync task before starting
the same adapter for the new binding. Stream cancellation aborts its socket and joins
the capture task. These are capture isolation rules, not physical-send fencing.

## Qualification boundary

Futures/inverse, configured linear-future and option normalization have synthetic
coverage. The manifest's configured kinds/currencies generate explicit subscriptions;
this does not prove that every live account instrument is covered. New listings or
missing metadata stop capture until the manifest is corrected. Matching/self-trade,
RFQ/combo and partial-history enrichment still require separate qualification.

Offline scripted-RPC tests, real loopback WebSocket framing/cancellation, and a real
restricted-role PostgreSQL pipeline are separate proofs. They do not qualify TLS to
Deribit, a real account's scopes/identity, live futures/options coverage or coherent
recovery. Before enabling a host, provide verified credential/account/instrument
bindings and exclusive worker selection, then run a separate live read-only acceptance.
Legacy Orders/Fills, risk/kill switch, physical sender and FIX gates remain unchanged.
