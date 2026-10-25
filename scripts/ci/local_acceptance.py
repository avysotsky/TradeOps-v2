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
import shlex
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
        self.docker_engine_version: str | None = None

    def exec(self, args: list[str], log: Path | None = None, timeout: int = 600,
             cwd: Path | None = None, quiet: bool = False) -> subprocess.CompletedProcess:
        if log is not None:
            log.parent.mkdir(parents=True, exist_ok=True)
        try:
            environment = os.environ.copy()
            if args and args[0] == "git":
                environment["GIT_TERMINAL_PROMPT"] = "0"
            cp = subprocess.run(args, cwd=cwd, capture_output=True, text=True,
                                timeout=timeout, check=False, errors="replace", env=environment)
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
        summary["steps"].append({"name": label, "command": sanitized(shlex.join(args)), "exit_code": cp.returncode,
                                 "status": "PASS" if cp.returncode == 0 else "FAIL",
                                 "started_utc": begin, "completed_utc": utc(), "log": log_name})
        return cp.returncode == 0

    def backend(self, summary: dict, source: Path, temp: Path, out: Path):
        self.require_images(["dotnet", "postgres", "curl"], out)
        pg_ver = ["docker", "run", "--rm", "--network", "none", self.images["postgres"], "postgres", "--version"]
        if not self.step(summary, "PostgreSQL version", pg_ver, out, "postgres-version.log", 120):
            return
        summary["tool_versions"]["postgres"] = (out / "postgres-version.log").read_text().strip()
        if not summary["tool_versions"]["postgres"].startswith("postgres (PostgreSQL) 17."):
            raise EnvironmentErrorCI("Expected PostgreSQL 17 container, got different version")
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

    def smoke(self, summary: dict, source: Path, temp: Path, out: Path):
        ident = "ci01-" + secrets.token_hex(6)
        net, pg, api = ident + "-net", ident + "-pg", ident + "-api"
        password = secrets.token_urlsafe(24)
        made_net = made_pg = made_api = False
        log = out / "docker-resources.log"
        try:
            cp = self.docker(["network", "create", "--label", "tradeops.ci01=temporary", net], log=log)
            if cp.returncode: raise EnvironmentErrorCI("Cannot create isolated Docker bridge network")
            made_net = True
            pg_cmd = ["run", "-d", "--rm", "--name", pg, "--network", net,
                      "--label", "tradeops.ci01=temporary",
                      "--tmpfs", "/var/lib/postgresql/data:rw,size=536870912",
                      "-e", "POSTGRES_DB=tradeops_ci", "-e", "POSTGRES_USER=postgres",
                      "-e", f"POSTGRES_PASSWORD={password}", self.images["postgres"]]
            if self.docker(pg_cmd, out / "postgres-start.log").returncode:
                raise EnvironmentErrorCI("Cannot start disposable PostgreSQL17; see postgres-start.log")
            made_pg = True
            for _ in range(40):
                ready = self.docker(["exec", pg, "pg_isready", "-U", "postgres", "-d", "tradeops_ci"], quiet=True)
                if ready.returncode == 0: break
                time.sleep(1)
            else: raise EnvironmentErrorCI("PostgreSQL17 was not ready after 40 checks")
            env = {"ConnectionStrings__TradeOpsDb": f"Host={pg};Port=5432;Database=tradeops_ci;Username=postgres;Password={password}",
                   "ASPNETCORE_URLS": "http://0.0.0.0:5080", "ASPNETCORE_ENVIRONMENT": "Development",
                   "Worker__ReconciliationIntervalSeconds": "300"}
            api_cmd = self.container_cmd("dotnet", source, temp,
                       ["dotnet", "run", "--project", "src/TradeOps.Api", "-c", "Release", "--no-build", "--no-restore"],
                       network=net, env=env, detach=True, name=api)
            if self.step(summary, "start Mock-only API", api_cmd, out, "api-start.log", 120) is False:
                raise EnvironmentErrorCI("Could not start Mock API in disposable network")
            made_api = True
            checks = [("api-health-live", f"http://{api}:5080/health/live"),
                      ("api-health-ready", f"http://{api}:5080/health/ready"),
                      ("api-openapi", f"http://{api}:5080/swagger/v1/swagger.json")]
            for label, url in checks:
                cmd = ["docker", "run", "--rm", "--network", net, "--cap-drop", "ALL",
                       "--security-opt", "no-new-privileges", self.images["curl"],
                       "--fail", "--silent", "--show-error", "--max-time", "5", url]
                probe_started = utc()
                for attempt in range(30 if label == "api-health-live" else 1):
                    probe = self.exec(cmd, out / f"{label}.log", timeout=30, quiet=True)
                    ok = probe.returncode == 0
                    if ok: break
                    if label == "api-health-live": time.sleep(2)
                summary["steps"].append({"name": label, "command": sanitized(shlex.join(cmd)),
                                         "exit_code": 0 if ok else probe.returncode,
                                         "status": "PASS" if ok else "FAIL", "attempts": attempt + 1,
                                         "started_utc": probe_started, "completed_utc": utc(),
                                         "log": f"{label}.log"})
                if not ok:
                    summary["postgres_mock_smoke"] = "FAIL"
                    return
                if label == "api-health-ready":
                    try:
                        health = json.loads((out / "api-health-ready.log").read_text())
                        if health.get("status") != "ready" or health.get("dependencies", {}).get("postgres") != "ready":
                            summary["postgres_mock_smoke"] = "FAIL"
                            summary["notes"].append("Mock API /health/ready did not confirm PostgreSQL ready")
                            return
                    except (ValueError, OSError):
                        summary["postgres_mock_smoke"] = "FAIL"
                        return
                if label == "api-openapi":
                    try:
                        openapi = json.loads((out / "api-openapi.log").read_text())
                        paths = openapi["paths"]
                        if not {"/health/live", "/health/ready", "/api/signals"}.issubset(paths):
                            summary["postgres_mock_smoke"] = "FAIL"
                            summary["notes"].append("Mock API OpenAPI paths incomplete")
                            return
                    except (ValueError, KeyError, TypeError, OSError):
                        summary["postgres_mock_smoke"] = "FAIL"
                        return
            # Only a synthetic order mutation on isolated Mock; never an exchange operation.
            payload = json.dumps({"symbol": "BTCUSDT", "side": "Buy", "quantity": 0.001,
                                  "source": "ci01-disposable-mock", "signalId": str(uuid.uuid4())})
            post = ["docker", "run", "--rm", "--network", net, "--cap-drop", "ALL",
                    "--security-opt", "no-new-privileges", self.images["curl"],
                    "--fail", "--silent", "--show-error", "--max-time", "15",
                    "-H", "Content-Type: application/json", "--data-raw", payload,
                    f"http://{api}:5080/api/signals"]
            if not self.step(summary, "isolated Mock signal order", post, out, "mock-order.log", 60):
                summary["postgres_mock_smoke"] = "FAIL"
                return
            try:
                order_response = json.loads((out / "mock-order.log").read_text())
                order = order_response["order"]
                if order.get("status") != "PartiallyFilled" or abs(float(order.get("filledQuantity", 0)) - 0.0006) >= 1e-12:
                    summary["postgres_mock_smoke"] = "FAIL"
                    summary["notes"].append("Synthetic Mock order response failed semantic assertion")
                    return
            except (ValueError, KeyError, TypeError):
                summary["postgres_mock_smoke"] = "FAIL"
                return
            summary["postgres_mock_smoke"] = "PASS"
        finally:
            # Delete only uniquely named resources we created; never prune/stop external stacks.
            if made_api:
                self.docker(["logs", api], out / "api-service.log", quiet=True)
                if self.docker(["rm", "-f", api], out / "api-remove.log", quiet=True).returncode:
                    summary["cleanup_complete"] = False
            if made_pg:
                self.docker(["logs", pg], out / "postgres-service.log", quiet=True)
                if self.docker(["rm", "-f", pg], out / "postgres-remove.log", quiet=True).returncode:
                    summary["cleanup_complete"] = False
            if made_net:
                if self.docker(["network", "rm", net], out / "network-remove.log", quiet=True).returncode:
                    summary["cleanup_complete"] = False
            if not summary["cleanup_complete"]:
                summary["notes"].append("CI-01 resource cleanup failed; inspect only uniquely named ci01 resources")

    def frontend(self, gate: str, summary: dict, source: Path, temp: Path, out: Path):
        self.require_images(["node"], out)
        folder = "apps/trade-terminal" if gate == "deribit" else "apps/coincall-terminal"
        if not (source / folder / "package.json").is_file():
            raise RuntimeError(f"Required frontend missing: {folder}")
        if gate == "deribit" and not (source / folder / "tests/vanLayout.test.mjs").is_file():
            raise RuntimeError("Required Deribit VAN layout regression test missing")
        lock = (source / folder / "package-lock.json").is_file()
        summary["lockfile_present"] = lock
        summary["dependency_reproducibility"] = "PINNED_LOCKFILE" if lock else "LIMITED_NO_LOCKFILE"
        npm = lambda args, network="none": self.container_cmd("node", source, temp,
                         ["npm", "--prefix", folder, *args], network)
        version = self.container_cmd("node", source, temp, ["node", "--version"])
        if not self.step(summary, "node version", version, out, "node-version.log"): return
        node_ver = (out / "node-version.log").read_text().strip()
        summary["tool_versions"]["node"] = node_ver
        if not node_ver.startswith("v24."):
            raise EnvironmentErrorCI(f"Node24 required, found {node_ver}")
        if not self.step(summary, "npm version", self.container_cmd("node", source, temp, ["npm", "--version"]), out, "npm-version.log"): return
        summary["tool_versions"]["npm"] = (out / "npm-version.log").read_text().strip()
        install = ["ci", "--no-audit", "--no-fund"] if lock else ["install", "--no-package-lock", "--no-audit", "--no-fund"]
        if not self.step(summary, "npm " + install[0], npm(install, "bridge"), out, "npm-install.log", 1800): return
        if not self.step(summary, "npm test", npm(["test"]), out, "npm-test.log", 600): return
        if gate == "deribit":
            van = self.container_cmd("node", source, temp, ["node", "--test", "tests/vanLayout.test.mjs"])
            van[van.index("--workdir") + 1] = "/workspace/" + folder
            if not self.step(summary, "explicit VAN layout test", van, out, "van-layout.log", 600): return
        self.step(summary, "React build", npm(["run", "build"]), out, "react-build.log", 1200)

    def run_one(self, number: int) -> dict:
        branch, gate = SPECS[number]
        print(f"\nPR #{number} ({gate}): fetching exact current GitHub HEAD", flush=True)
        started = utc()
        result = {"schema_version": 1, "pr": number, "branch": branch, "gate": gate,
                  "source_sha": None, "remote_sha_before": None, "remote_sha_after": None,
                  "started_utc": started, "completed_utc": None, "status": "NOT_RUN",
                  "steps": [], "tool_versions": {"docker_engine": self.docker_engine_version}, "images": {}, "test_counts": None,
                  "postgres_mock_smoke": "NOT_RUN" if gate == "backend" else "NOT_APPLICABLE",
                  "cleanup_complete": True,
                  "notes": [], "safety": {"exchange_provider": "Mock", "mainnet_execution_enabled": False,
                                           "published_ports": False, "global_docker_cleanup": False}}
        source_sha = "unresolved-" + self.run_id
        try:
            try:
                source_sha = self.remote_sha(number)
                result["source_sha"] = result["remote_sha_before"] = source_sha
            except StaleShaError:
                result["status"] = "STALE_SHA"
                raise
            out = self.result_root / f"pr-{number}" / source_sha
            out.mkdir(parents=True, exist_ok=True)
            if (out / "summary.json").exists():
                out = out / self.run_id
                out.mkdir(parents=True, exist_ok=True)
            result["results_path"] = str(out)
            # Worktrees/caches on WSL's Linux filesystem; no Windows NTFS build writes.
            (self.result_root / ".scratch").mkdir(exist_ok=True)
            with tempfile.TemporaryDirectory(prefix=f"ci01-pr{number}-", dir=self.result_root / ".scratch") as td:
                tmp = Path(td)
                worktree = tmp / "source"
                (tmp / "home").mkdir()
                (tmp / "npm").mkdir()
                (tmp / "nuget").mkdir()
                if self.exec(["git", "-C", str(self.repo), "fetch", "--no-tags", "--no-write-fetch-head", "origin", source_sha],
                             out / "git-fetch.log", 180).returncode:
                    raise EnvironmentErrorCI("Fetch of exact GitHub HEAD failed")
                if self.exec(["git", "-C", str(self.repo), "worktree", "add", "--detach", str(worktree), source_sha],
                             out / "git-worktree.log", 180).returncode:
                    raise EnvironmentErrorCI("Cannot create isolated detached worktree")
                try:
                    checked = self.exec(["git", "-C", str(worktree), "rev-parse", "HEAD"], quiet=True)
                    if checked.returncode or checked.stdout.strip() != source_sha:
                        raise StaleShaError("Local detached checkout has unexpected SHA")
                    result["checked_out_sha"] = checked.stdout.strip()
                    if gate == "backend": self.backend(result, worktree, tmp, out)
                    else: self.frontend(gate, result, worktree, tmp, out)
                finally:
                    # Only our detached worktree may be removed. Keep evidence outside temp.
                    self.exec(["git", "-C", str(self.repo), "worktree", "remove", "--force", str(worktree)],
                              out / "git-worktree-cleanup.log", 120)
            result["test_counts"] = parse_counts(out, gate)
            if not result["steps"] or any(step["status"] == "FAIL" for step in result["steps"]):
                result["status"] = "FAIL"
            elif not result.get("cleanup_complete", True):
                result["status"] = "FAIL"
            elif gate == "backend" and result["postgres_mock_smoke"] != "PASS":
                result["status"] = "FAIL"
            elif gate == "backend" and not result["test_counts"]["available"]:
                result["status"] = "FAIL"
                result["notes"].append("TRX missing; no independently confirmed xUnit test count")
            elif gate != "backend" and not result["test_counts"]["available"]:
                result["status"] = "FAIL"
                result["notes"].append("TAP totals unavailable")
            elif result["test_counts"]["total"] <= 0 or result["test_counts"]["passed"] <= 0 or result["test_counts"]["failed"] != 0:
                result["status"] = "FAIL"
                result["notes"].append("Missing tests or nonzero failures in recorded TRX/TAP totals")
            elif gate == "deribit" and result["test_counts"]["suites"] != 2:
                result["status"] = "FAIL"
                result["notes"].append("Both regular and explicit VAN Node TAP suites must be counted")
            elif any(step["name"] == "React build" and step["status"] == "PASS" for step in result["steps"]) or gate == "backend":
                result["status"] = "PASS"
            else:
                result["status"] = "FAIL"
        except EnvironmentErrorCI as exc:
            result["status"] = "BLOCKED_ENV"
            result["notes"].append(str(exc))
        except StaleShaError as exc:
            result["status"] = "STALE_SHA"
            result["notes"].append(str(exc))
        except (ValueError, OSError, RuntimeError, json.JSONDecodeError, KeyError) as exc:
            result["status"] = "FAIL"
            result["notes"].append(f"{type(exc).__name__}: {exc}")
        finally:
            if result["test_counts"] is None and result.get("results_path"):
                result["test_counts"] = parse_counts(Path(result["results_path"]), gate)
            try:
                current = self.remote_sha(number)
                result["remote_sha_after"] = current
                if result["source_sha"] and current != result["source_sha"]:
                    result["status"] = "STALE_SHA"
                    result["notes"].append("PR HEAD moved during acceptance; results cannot qualify current HEAD")
            except (StaleShaError, EnvironmentErrorCI) as exc:
                if result["status"] == "PASS": result["status"] = "BLOCKED_ENV"
                result["notes"].append(f"Cannot revalidate live PR HEAD: {exc}")
            result["completed_utc"] = utc()
            result["images"] = {key: self.image_info[key] for key in
                                  (["dotnet", "postgres", "curl"] if gate == "backend" else ["node"])
                                  if key in self.image_info}
            out = Path(result.get("results_path", self.result_root / f"pr-{number}" / source_sha))
            out.mkdir(parents=True, exist_ok=True)
            (out / "summary.json").write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n")
            print(f"PR #{number}: {result['status']} at {result['source_sha']} | {out / 'summary.json'}", flush=True)
        return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Local TradeOps exact PR-HEAD WSL2/Docker acceptance")
    parser.add_argument("target", nargs="?", default="all", choices=["all", "62", "63", "64"])
    parser.add_argument("repo", nargs="?", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--results-dir", default=str(Path.home() / "tradeops-acceptance-results"))
    args = parser.parse_args()
    repo = Path(args.repo).expanduser().resolve()
    dest = Path(args.results_dir).expanduser().resolve()
    dest.mkdir(parents=True, exist_ok=True)
    os.chmod(dest, 0o700)
    for binary in ("git", "docker"):
        if not shutil.which(binary):
            print(f"BLOCKED_ENV: {binary} is not on Ubuntu PATH", file=sys.stderr)
            if binary == "git":
                return 2
    origin = subprocess.run(["git", "-C", str(repo), "remote", "get-url", "origin"],
                            capture_output=True, text=True, check=False)
    if origin.returncode or not git_origin_is_github(origin.stdout.strip()):
        print("BLOCKED_ENV: origin must be canonical github.com/avysotsky/TradeOps-v2 (no tokens in URL)", file=sys.stderr)
        return 2
    engine = Runner(repo, dest)
    docker_info = engine.docker(["info", "--format", "{{.ServerVersion}}"], quiet=True)
    if docker_info.returncode:
        print("BLOCKED_ENV: WSL Docker daemon is not reachable", file=sys.stderr)
    else:
        engine.docker_engine_version = docker_info.stdout.strip()
    targets = list(SPECS) if args.target == "all" else [int(args.target)]
    results = [engine.run_one(pr) for pr in targets]
    aggregate = {"schema_version": 1, "run_id": engine.run_id, "created_utc": utc(),
                 "results": [{"pr": r["pr"], "sha": r["source_sha"], "status": r["status"],
                              "summary": str(Path(r.get("results_path", dest / f"pr-{r['pr']}" / (r["source_sha"] or "unresolved"))) / "summary.json")}
                             for r in results]}
    report = dest / f"acceptance-{engine.run_id}.json"
    report.write_text(json.dumps(aggregate, indent=2, ensure_ascii=False) + "\n")
    print("\nFINAL SUMMARY", flush=True)
    for r in results:
        print(f"  PR #{r['pr']}: {r['status']} | {r['source_sha']} | tests={r['test_counts']}", flush=True)
    print("Aggregate report:", report, flush=True)
    return 0 if all(r["status"] == "PASS" for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
