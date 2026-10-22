#!/usr/bin/env python3
"""Real HTTP acceptance against a loopback TradeOps API and a disposable PG DB.

The API must run with Exchange.Provider=Mock. No network broker calls are made.
Contains no passwords or credentials; secrets are supplied through local env.
"""
import json
import os
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path


class AcceptanceFailure(Exception):
    pass


BASE = os.environ["TRADEOPS_ACCEPTANCE_API_URL"].rstrip("/")
if not BASE.startswith("http://127.0.0.1:"):
    raise SystemExit("FAIL: real API acceptance requires a loopback HTTP listener")
SIGNAL_KEY = os.environ["TRADEOPS_ACCEPTANCE_SIGNAL_KEY"]
OPERATOR_KEY = os.environ["TRADEOPS_ACCEPTANCE_OPERATOR_KEY"]
STATE = Path(os.environ["TRADEOPS_ACCEPTANCE_STATE"])
PHASE = os.environ.get("TRADEOPS_ACCEPTANCE_PHASE", "")


def request(method, path, payload=None, authorization=None):
    headers = {"Accept": "application/json"}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if authorization == "signal":
        headers["X-TradeOps-Api-Key"] = SIGNAL_KEY
    if authorization == "operator":
        headers["X-TradeOps-Operator-Key"] = OPERATOR_KEY

    req = urllib.request.Request(
        BASE + path,
        data=None if payload is None else json.dumps(payload).encode("utf-8"),
        headers=headers,
        method=method,
    )
    try:
        with urllib.request.urlopen(req, timeout=18) as response:
            status = response.status
            raw = response.read(512_000)
    except urllib.error.HTTPError as exc:
        status = exc.code
        raw = exc.read(512_000)
    except (OSError, TimeoutError) as exc:
        raise AcceptanceFailure(f"HTTP transport failure on {path}: {type(exc).__name__}") from None

    try:
        parsed = json.loads(raw) if raw else None
    except (ValueError, UnicodeError):
        parsed = None
    return status, parsed


def check(condition, label):
    if not condition:
        raise AcceptanceFailure(label)


def expect(method, path, wanted, payload=None, authorization=None):
    status, body = request(method, path, payload, authorization)
    check(status == wanted, f"{method} {path}: expected HTTP {wanted}, received {status}")
    return body


def verify_health():
    live = expect("GET", "/health/live", 200)
    check(live.get("status") == "live", "live health payload mismatch")
    ready = expect("GET", "/health/ready", 200)
    check(ready.get("status") == "ready", "readiness not ready")
    check(ready.get("dependencies", {}).get("postgres") == "ready",
          "API readiness did not verify real PostgreSQL")


def initial():
    verify_health()
    expect("GET", "/api/orders/local?limit=10", 200)
    check(expect("POST", "/api/signals", 401,
                 {"symbol": "BTCUSDT", "side": "Buy", "quantity": 0.001}) is not None,
          "missing auth rejection body")

    signal_id = str(uuid.uuid4())
    payload = {
        "signalId": signal_id,
        "symbol": "BTCUSDT",
        "side": "Buy",
        "quantity": 0.001,
        "riskPercent": 1.0,
        "source": "real-pg-acceptance-mock-only",
    }
    first = expect("POST", "/api/signals", 200, payload, "signal")
    check(isinstance(first, dict) and first.get("accepted") is True
          and isinstance(first.get("order"), dict), "mock submission not accepted")
    coid = first["order"].get("clientOrderId")
    check(isinstance(coid, str) and bool(coid), "missing mock client order identifier")

    retry = expect("POST", "/api/signals", 200, payload, "signal")
    check(retry.get("order", {}).get("clientOrderId") == coid,
          "same signal id generated a different mock order")

    conflict = dict(payload)
    conflict["quantity"] = 0.002
    expect("POST", "/api/signals", 409, conflict, "signal")

    order_list = expect("GET", "/api/orders/local?limit=10", 200)
    check(isinstance(order_list, list)
          and sum(item.get("clientOrderId") == coid for item in order_list) == 1,
          "PostgreSQL local order audit missing or duplicated")

    expect("GET", f"/api/orders/local/{coid}/history", 401)
    historical = expect("GET", f"/api/orders/local/{coid}/history", 200,
                        authorization="operator")
    check(isinstance(historical, list) and len(historical) >= 1,
          "order lifecycle audit not stored in PostgreSQL")
    expect("GET", f"/api/signals/{signal_id}", 200, authorization="operator")
    expect("POST", "/api/system/reconcile", 200, authorization="operator")
    after = expect("GET", f"/api/orders/local/{coid}/history", 200,
                   authorization="operator")
    check(isinstance(after, list) and after[-1].get("status") == "Filled",
          "Mock reconciliation did not persist terminal Filled state")

    STATE.write_text(json.dumps({"signalId": signal_id, "coid": coid,
                                 "payload": payload}), encoding="utf-8")
    print("TRADEOPS REAL API INITIAL PASS mock_order=1 idempotent=true "
          "signal_conflict=409 postgres_ready=true reconciliation=Filled")


def restarted():
    saved = json.loads(STATE.read_text(encoding="utf-8"))
    signal_id, coid = saved["signalId"], saved["coid"]
    verify_health()

    signal = expect("GET", f"/api/signals/{signal_id}", 200,
                    authorization="operator")
    check(signal.get("id") == signal_id, "signal not restored after API restart")

    history = expect("GET", f"/api/orders/local/{coid}/history", 200,
                     authorization="operator")
    check(isinstance(history, list) and history[-1].get("status") == "Filled",
          "terminal lifecycle audit lost after API restart")

    replay = expect("POST", "/api/signals", 200, saved["payload"], "signal")
    check(replay.get("accepted") is True
          and replay.get("order", {}).get("clientOrderId") == coid,
          "API restart broke persisted idempotency")

    orders = expect("GET", "/api/orders/local?limit=10", 200)
    check(sum(item.get("clientOrderId") == coid for item in orders) == 1,
          "API restart caused duplicate database order")

    print("TRADEOPS REAL API RESTART PASS durable_signal=true "
          "durable_history=true idempotent=true duplicate_orders=0")


try:
    if PHASE == "initial":
        initial()
    elif PHASE == "restart":
        restarted()
    else:
        raise AcceptanceFailure("unsupported acceptance phase")
except (AcceptanceFailure, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
    print(f"TRADEOPS REAL API {PHASE.upper()} FAIL: {str(exc)[:180]}", file=sys.stderr)
    sys.exit(1)
