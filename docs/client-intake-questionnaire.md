# TradeOps Client Intake Questionnaire

Use this questionnaire before accepting a paid TradeOps pilot.

Its purpose is to determine whether the client fits the current product boundary and to collect the minimum configuration information needed for the existing pilot flow.

Do **not** ask the client to send API secrets, private keys, passwords or production exchange credentials in this questionnaire.

## 1. Client and contact

**Client / company name:**

**Primary technical contact:**

**Primary operator contact:**

**Preferred communication channel:**

**Target pilot start window:**

## 2. Existing trading logic

Which best describes the client's current source of trading decisions?

- [ ] TradingView indicator
- [ ] TradingView strategy
- [ ] Existing alert logic
- [ ] Existing bot/service that can emit webhooks
- [ ] Other

Brief description:

**Does the client already know when to buy/sell?**

- [ ] Yes
- [ ] No

If **No**, stop qualification: strategy design / alpha research is outside the current TradeOps pilot.

## 3. Signal source and transport

**Primary source of signals:**

**Can the source send an HTTPS webhook?**

- [ ] Yes
- [ ] No
- [ ] Unknown

**If TradingView is used, is the client able to create/edit alert messages?**

- [ ] Yes
- [ ] No
- [ ] Unknown

**Typical alert frequency:**

**Peak expected alert frequency:**

**Expected operating hours / timezone:**

## 4. Symbols and action semantics

List the symbols required for the pilot:

```text
Example:
BTCUSDT
ETHUSDT
```

For each action, define the intended meaning:

**buy =**

**sell =**

**Are short positions required?**

- [ ] Yes
- [ ] No

**Are position close/reduce-only semantics required in the first pilot?**

- [ ] Yes
- [ ] No
- [ ] Not sure

If the required action semantics cannot be represented by the current agreed payload contract, mark the case for separate technical scoping before committing to the pilot.

## 5. Quantity semantics

What does the quantity field represent?

- [ ] Base asset quantity
- [ ] Contracts
- [ ] Fixed quote-currency amount
- [ ] Percentage / dynamic sizing
- [ ] Other

Required decimal precision:

Minimum intended test quantity:

Maximum intended test quantity during the pilot:

If sizing requires new strategy/risk logic rather than mapping an existing client decision into the current TradeOps contract, scope it separately.

## 6. Event identity and duplicate behavior

Does the client already have a stable unique identifier for one intended execution?

- [ ] Yes
- [ ] No
- [ ] Unknown

Proposed event identifier / source field:

Describe when two webhook deliveries should be considered the **same logical execution**:

Describe when two deliveries should be considered **different executions**:

Acceptance requirement:

```text
Same logical execution -> same eventId
Different intended execution -> different eventId
```

If a stable event identity cannot be defined, do not start the paid pilot until this is resolved.

## 7. Risk fields

Which values are already decided by the client's existing logic?

- [ ] Quantity
- [ ] Risk percentage
- [ ] Stop loss
- [ ] Take profit
- [ ] None of the optional risk fields

Who owns the trading/risk decision?

- [ ] Client strategy / client
- [ ] Existing external system
- [ ] Other

TradeOps may enforce configured technical/risk controls, but the current pilot does not design a profitable risk model for the client.

## 8. Pilot stage

Mandatory first stage:

- [x] Mock

Does the client want an optional Bybit Testnet stage after Mock acceptance?

- [ ] Yes
- [ ] No

If **Yes**:

**Does the client already have a Bybit Testnet account?**

- [ ] Yes
- [ ] No

**Can the client create dedicated testnet API credentials when implementation starts?**

- [ ] Yes
- [ ] No

Do not collect those credentials in this questionnaire.

Mainnet or real-money execution is not part of the current standard pilot.

## 9. Deployment boundary

For a public TradingView pilot, who controls the DNS name?

**DNS owner:**

Who controls the TLS certificate / certificate provisioning?

**TLS owner:**

Where is the pilot expected to run?

- [ ] Client server / VM
- [ ] Implementer-managed pilot host
- [ ] Existing Docker host
- [ ] Other / undecided

Can the host expose HTTPS port 443 to TradingView while keeping the TradeOps API itself restricted from untrusted public access?

- [ ] Yes
- [ ] No
- [ ] Unknown

## 10. Operations and alerts

Who is authorized to trigger operator actions such as reconciliation, emergency stop or cancellation?

**Authorized operator:**

Should Telegram operational alerts be enabled?

- [ ] Yes
- [ ] No

If **Yes**, who is responsible for the receiving channel/account?

**Alert owner:**

What delivery inactivity period would be meaningful enough to investigate during the client's normal operating hours?

**No-success threshold:**

## 11. Acceptance expectations

The standard pilot acceptance proves execution/integration engineering, including:

- authenticated webhook delivery;
- deterministic event identity;
- idempotent redelivery;
- conflict rejection;
- persisted signal/order correlation;
- lifecycle persistence;
- reconciliation;
- delivery audit, metrics and health;
- operator-authenticated evidence export;
- client sign-off.

Does the client expect anything beyond this list?

- [ ] No
- [ ] Yes

If **Yes**, describe:

Any additional requirement must be explicitly classified as either:
1. configuration of an existing capability;
2. a small pilot adaptation;
3. separate product development.

## 12. Explicit expectation check

Confirm the client understands that the standard TradeOps pilot does **not** include:

- [ ] strategy design;
- [ ] alpha research;
- [ ] profitable signal generation;
- [ ] return/profit guarantees;
- [ ] Bybit mainnet;
- [ ] other real-money exchange deployment;
- [ ] second-exchange integration;
- [ ] HFT / ultra-low-latency guarantees.

Any unchecked item requires clarification before accepting the pilot.

## Qualification result

Complete this section after reviewing the answers.

**Result:**

- [ ] FIT — current TradeOps paid pilot can be delivered mainly by configuration/onboarding.
- [ ] FIT WITH SMALL ADAPTATION — a bounded change is required before/during pilot.
- [ ] SEPARATE SCOPE REQUIRED — client requires capability outside the current pilot.
- [ ] NOT A CURRENT FIT — request depends on strategy/alpha/mainnet/unsupported execution semantics.

**Required adaptation, if any:**

**Open questions:**

**Approved pilot stage:**

- [ ] Mock only
- [ ] Mock + optional Bybit Testnet after Mock acceptance

**Next action:**

- [ ] Prepare `docs/paid-pilot-scope-template.md`
- [ ] Send commercial terms
- [ ] Run technical discovery
- [ ] Reject / redirect as outside current scope
