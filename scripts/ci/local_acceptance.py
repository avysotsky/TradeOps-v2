#!/usr/bin/env python3
"""TradeOps CI-01: local WSL2/Docker acceptance, never GitHub Actions.

No branch switches, port publishing, reusable services, credentials, or global Docker cleanup.
All Docker resources get unique names; source worktrees live on WSL's Linux filesystem.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import uuid

REPO = "avysotsky/TradeOps-v2"
SPECS = {
    62: ("TradeOps/b1-01-oms-domain-identity-contracts", "backend"),
    63: ("TradeOps/f1-01-deribit-react-van-parity", "deribit"),
    64: ("TradeOps/f2-01-coincall-react-workstation", "coincall"),
}
# The .NET image is manifest-digest pinned. Other official images use version tags;
# their observed immutable IDs and registry digests are recorded in evidence.
IMAGES = {
    "dotnet": "mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317",
    "node": "node:24.12.0-bookworm-slim",
    "postgres": "postgres:17.6-bookworm",
    "curl": "curlimages/curl:8.12.1",
}
LOG_REDACT = re.compile(r"(?i)(authorization:\s*bearer\s+)\S+|(password|api[_-]?key|secret|token)=\S+")


def utc() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")


def safe_name(value: str) -> str:
    return re.sub(r"[^a-zA-Z0-9_.-]", "_", value)


def git_origin_is_github(url: str) -> bool:
    # Never print URLs (they can contain an embedded token).
    return bool(re.fullmatch(
        r"(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)"
        r"avysotsky/TradeOps-v2(?:\.git)?/?", url))


def parse_ls_remote(output: str, wanted: set[str]) -> dict[str, str]:
    refs = {}
    for line in output.splitlines():
        fields = line.split()
        if len(fields) == 2 and fields[1] in wanted and re.fullmatch(r"[a-f0-9]{40}", fields[0]):
            refs[fields[1]] = fields[0]
    if set(refs) != wanted:
        raise RuntimeError("Required remote PR head or source-branch ref is unavailable")
    if len(set(refs.values())) != 1:
        raise StaleShaError("Remote PR HEAD and source branch disagree")
    return refs


class StaleShaError(Exception):
    pass


class EnvironmentErrorCI(Exception):
    pass


def sanitized(raw: str) -> str:
    return LOG_REDACT.sub(lambda match: (match.group(1) or match.group(2) + "=") + "[REDACTED]", raw)


def parse_counts(folder: Path, gate: str) -> dict:
    if gate == "backend":
        total = passed = failed = skipped = 0
        found = 0
        for path in (folder / "trx").rglob("*.trx") if (folder / "trx").exists() else []:
            try:
                root = ET.parse(path).getroot()
                counters = next((e for e in root.iter() if e.tag.rsplit("}", 1)[-1] == "Counters"), None)
                if counters is None:
                    continue
                total += int(counters.attrib.get("total", "0"))
                passed += int(counters.attrib.get("passed", "0"))
                failed += int(counters.attrib.get("failed", "0"))
                skipped += int(counters.attrib.get("notExecuted", "0"))
                found += 1
            except (OSError, ValueError, ET.ParseError):
                continue
        return {"total": total, "passed": passed, "failed": failed, "skipped": skipped,
                "suites": found, "source": "TRX", "available": found > 0}
    suites = ["npm-test.log", "van-layout.log"] if gate == "deribit" else ["npm-test.log"]
    total = passed = failed = skipped = 0
    found = 0
    for name in suites:
        path = folder / name
        if not path.exists():
            continue
        content = path.read_text(errors="replace")
        # Node.js TAP emitted as '# tests 4', '# pass 4', '# fail 0'.
        def last(key: str):
            matches = re.findall(r"(?m)^#\s+" + key + r"\s+(\d+)\s*$", content)
            return int(matches[-1]) if matches else None
        n, p, f, s = (last(k) for k in ("tests", "pass", "fail", "skipped"))
        if n is None or p is None or f is None:
            continue
        total += n
        passed += p
        failed += f
        skipped += s or 0
        found += 1
    return {"total": total, "passed": passed, "failed": failed, "skipped": skipped,
            "suites": found, "source": "Node TAP", "available": found > 0}


class Runner:
    def __init__(self, repo: Path, result_root: Path):
        self.repo = repo
        self.result_root = result_root
        self.run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + secrets.token_hex(3)
        self.images = {k: os.environ.get("CI01_" + k.upper() + "_IMAGE", v)
                       for k, v in IMAGES.items()}
        # Never import host exchange keys, auth tokens, .env files or Docker env files.
        self.image_info: dict[str, dict] = {}

    def exec(self, args: list[str], log: Path | None = None, timeout: int = 600,
             cwd: Path | None = None, quiet: bool = False) -> subprocess.CompletedProcess:
        if log is not None:
            log.parent.mkdir(parents=True, exist_ok=True)
        try:
            cp = subprocess.run(args, cwd=cwd, capture_output=True, text=True,
                                timeout=timeout, check=False, errors="replace")
            output = sanitized(cp.stdout + ("\n" if cp.stdout and cp.stderr else "") + cp.stderr)
            if log is not None:
                log.write_text(output, encoding="utf-8")
            if not quiet:
                print(f"    {safe_name(args[0])}: exit {cp.returncode}" +
                      (f"; log {log.name}" if log else ""), flush=True)
            return cp
        except subprocess.TimeoutExpired:
            if log is not None:
                log.write_text(f"TIMEOUT after {timeout}s\n", encoding="utf-8")
            return subprocess.CompletedProcess(args, 124, "", "timeout")
        except OSError as exc:
            if log is not None:
                log.write_text(f"COMMAND UNAVAILABLE: {type(exc).__name__}\n", encoding="utf-8")
            return subprocess.CompletedProcess(args, 127, "", type(exc).__name__)

