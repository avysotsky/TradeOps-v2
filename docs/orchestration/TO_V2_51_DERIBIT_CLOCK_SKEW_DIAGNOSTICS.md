# TO-v2-51 — isolate Deribit-to-local skew from RAW L2 observed age

## Owner observation

The owner observed `Δ exchange → local` change from approximately 250 ms to 465 ms while the RAW book remains visually correct. The `DeribitRawBook.View` UTC timestamp and `DeribitRawFeedHub` subtraction formula are unchanged from the earlier RAW version, so source inspection alone **does not** establish an added 215 ms network delay.

The original metric is:

`observedDeltaMs = localBookSnapshotUnixMs − exchangeBookMessageTimestampUnixMs`

This mixes clock offset, transport, queueing, JSON parsing and snapshot construction.

## New independent public-only measurement

`DeribitClockProbe` makes one authenticated-to-*local-operator* (not exchange-authenticated) HTTPS GET to an allowlisted public endpoint:

- MAINNET: `https://www.deribit.com/api/v2/public/get_time`
- Testnet: `https://test.deribit.com/api/v2/public/get_time`

It never touches `DeribitRawFeedHub`'s receive loop, Deribit WS auth, exchange read-only credentials, private order overlays, or order execution. Same named HttpClient has proxy/redirect/cookie disabled; public GET includes **no API key**.

One singleton cache allows one request per ~30 seconds for all terminals, with lock to avoid concurrent requests, 5-second deadline, and short negative-result cooldown. The app supplies this sample only through the same loopback/ephemeral-operator-key-protected GET boundary as other terminal requests. No response payloads or exception messages cross the diagnostics boundary.

### Signed estimate and limits

Record local `t0` before HTTPS send, read the exchange millisecond timestamp `tx`, and record monotonic RTT `r` with Stopwatch after the response:

`estimatedWindowsMinusDeribitMs = (t0 + r/2) − tx`

`estimatedCorrectedDeltaMs = observedDeltaMs − estimatedWindowsMinusDeribitMs`

Interpretation:

- Positive offset: Windows ahead of Deribit.
- Negative offset: Windows behind Deribit.
- Corrected delta is **not** a measured one-way network latency. The midpoint assumes approximately symmetric outbound/inbound delays; server response generation, separate HTTPS route, scheduling, transit, and RAW queueing limit accuracy.
- Exchange and local timestamps can change meaning between grouped and RAW modes; compare RAW to RAW.
- Display sample RTT and timestamp, and expire client estimate after 90s. Never silently substitute zero for unavailable clock measurements or clamp a negative corrected value to zero.

Example, **illustrative only**: raw 465ms, signed clock offset +215ms, corrected value ~250ms; this is not an observation until confirmed by the operator's live panel.

## Runtime acceptance

1. Exact SHA launcher builds terminal API, filtered RAW book / clock math tests, Node contracts, TypeScript and Vite **before DPAPI** decryption.
2. Terminal starts on **RAW** (the owner's accepted default); 1/10/20 channels remain valid alternatives.
3. Keep first toolbar row unchanged: original Δ exchange → local continues to show original observed metric.
4. Expand `Диагностика рыночного потока`: see Windows−Deribit signed offset, HTTPS RTT, UTC probe time and corrected Δ every ~30 seconds.
5. Confirm no change in RAW `change_id` continuity, no extra Deribit WS reconnects and no public private payloads.
6. If RTT exceeds 1000ms or fluctuates materially, do **not** infer stable clock offset; inspect multiple samples.
7. Compare the actual signed offset with the old ~250ms and current ~465ms RAW reading. If the offset explains little of ~215ms change, investigate transport/serialization/queue work rather than Windows time.
8. Keep PR Draft until owner Windows execution accepts these conditions. No local dotnet runtime or live credentials available to assistant.

Source: Deribit public/get_time is a public endpoint returning current exchange Unix epoch milliseconds: https://docs.deribit.com/#public-get_time.
