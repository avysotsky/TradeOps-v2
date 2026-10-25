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

    def remote_sha(self, number: int) -> str:
        branch = SPECS[number][0]
        wanted = {f"refs/pull/{number}/head", f"refs/heads/{branch}"}
        cmd = ["git", "-C", str(self.repo), "ls-remote", "--exit-code", "origin", *sorted(wanted)]
        cp = self.exec(cmd, quiet=True, timeout=120)
        if cp.returncode:
            raise EnvironmentErrorCI(f"Cannot read PR #{number} refs from GitHub (git ls-remote exit {cp.returncode})")
        refs = parse_ls_remote(cp.stdout, wanted)
        return refs[f"refs/pull/{number}/head"]

    def docker(self, args: list[str], log: Path | None = None, timeout: int = 120,
               quiet: bool = False) -> subprocess.CompletedProcess:
        return self.exec(["docker", *args], log, timeout, quiet=quiet)

    def require_images(self, names: list[str], out: Path):
        for key in names:
            if key in self.image_info:
                continue
            image = self.images[key]
            if not ("://" not in image and " " not in image and "\n" not in image):
                raise EnvironmentErrorCI(f"Unsafe Docker image reference for {key}")
            result = self.docker(["image", "inspect", image], quiet=True)
            if result.returncode:
                pulled = self.docker(["pull", image], out / f"image-{key}.log", timeout=1200)
                if pulled.returncode:
                    raise EnvironmentErrorCI(f"Cannot pull {key} image ({image}); see image-{key}.log")
            cp = self.docker(["image", "inspect", "--format", "{{json .Id}}|{{json .RepoDigests}}", image], quiet=True)
            if cp.returncode:
                raise EnvironmentErrorCI(f"Unable to inspect {key} image")
            try:
                image_id, digests = cp.stdout.strip().split("|", 1)
                self.image_info[key] = {"reference": image, "id": json.loads(image_id),
                                        "repo_digests": json.loads(digests)}
            except (ValueError, json.JSONDecodeError):
                raise EnvironmentErrorCI(f"Invalid image inspect result for {key}")

    def container_cmd(self, image_key: str, source: Path, temp: Path, cmd: list[str],
                      network: str = "none", env: dict[str, str] | None = None,
                      detach: bool = False, name: str | None = None) -> list[str]:
        args = ["run", "-d" if detach else "--rm"]
        if detach:
            args += ["--rm"]
        if name:
            args += ["--name", name]
        args += ["--network", network, "--security-opt", "no-new-privileges", "--cap-drop", "ALL",
                 "--mount", f"type=bind,src={source},dst=/workspace",
                 "--mount", f"type=bind,src={temp},dst=/scratch", "--workdir", "/workspace",
                 "-e", "HOME=/scratch/home", "-e", "NUGET_PACKAGES=/scratch/nuget",
                 "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
                 "-e", "NPM_CONFIG_CACHE=/scratch/npm", "-e", "CI=true",
                 "-e", "Exchange__Provider=Mock", "-e", "Telegram__Enabled=false",
                 "-e", "DeribitTerminal__ExecutionEnabled=false",
                 "-e", "DeribitTerminal__MainnetExecutionEnabled=false"]
        for key, value in (env or {}).items():
            args.extend(["-e", f"{key}={value}"])
        return ["docker", *args, self.images[image_key], *cmd]

    def step(self, summary: dict, label: str, args: list[str], out: Path,
             log_name: str, timeout: int = 900, cwd: Path | None = None) -> bool:
        begin = utc()
        cp = self.exec(args, out / log_name, timeout, cwd=cwd)
        summary["steps"].append({"name": label, "command": label, "exit_code": cp.returncode,
                                 "status": "PASS" if cp.returncode == 0 else "FAIL",
                                 "started_utc": begin, "completed_utc": utc(), "log": log_name})
        return cp.returncode == 0

    def backend(self, summary: dict, source: Path, temp: Path, out: Path):
        self.require_images(["dotnet", "postgres", "curl"], out)
        sdk_file = source / "global.json"
        expected = json.loads(sdk_file.read_text())["sdk"]["version"]
        summary["sdk_expected"] = expected
        base = lambda cmd, net="none", env=None, name=None, detach=False: self.container_cmd(
            "dotnet", source, temp, cmd, net, env, detach, name)
        if not self.step(summary, "dotnet version", base(["dotnet", "--version"]), out, "dotnet-version.log"):
            return
        observed = (out / "dotnet-version.log").read_text().strip()
        summary["tool_versions"]["dotnet"] = observed
        if observed != expected:
            raise EnvironmentErrorCI(f"Expected global.json .NET {expected}, Docker reported {observed}")
        if not self.step(summary, "dotnet info", base(["dotnet", "--info"]), out, "dotnet-info.log"):
            return
        if not self.step(summary, "dotnet restore", base(["dotnet", "restore", "TradeOps.sln"], "bridge"), out, "restore.log", 1800):
            return
        if not self.step(summary, "dotnet build", base(["dotnet", "build", "TradeOps.sln", "-c", "Release", "--no-restore"]), out, "build.log", 1800):
            return
        tests = base(["dotnet", "test", "TradeOps.sln", "-c", "Release", "--no-build", "--no-restore",
                      "--logger", "trx;LogFileName=tests.trx", "--results-directory", "/scratch/trx"])
        tests_passed = self.step(summary, "all backend xUnit (including OMS)", tests, out, "dotnet-test.log", 1800)
        # Copy TRX even for a failing test invocation so failure totals are preserved.
        (out / "trx").mkdir(exist_ok=True)
        for file in (temp / "trx").rglob("*.trx"):
            destination = out / "trx" / file.relative_to(temp / "trx")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(file, destination)
        if not tests_passed:
            return
        summary["postgres_mock_smoke"] = "NOT_RUN"
        self.smoke(summary, source, temp, out)

