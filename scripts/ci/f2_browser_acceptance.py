#!/usr/bin/env python3
"""Owner WSL2 only: exact F2 browser capture, reuse CI-01 transport/safety.

No unit-test rerun. CI-01 removes dist, so rebuild only for browser capture.
Never qualifies live connectivity, execution or human visual parity.
"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import zipfile

from local_acceptance import Runner, StaleShaError, utc, git_origin_is_github

F2 = "d262c94b384b2df0a4ca5ed9d8054c431c3f0462"
F1 = "cd7f06c31206b20d135d595c21791a7f9ff26c85"
VAN = "702a6f51006be4e9c35c65d21ec7f85c40c5a2d4"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("repo", type=Path)
    parser.add_argument("--export-dir", type=Path, required=True)
    args = parser.parse_args()
    repo = args.repo.resolve()
    root = Path.home() / "TradeOps-acceptance" / "F2-01" / F2
    runner = Runner(repo, root)
    out = root / ("browser-" + runner.run_id)
    out.mkdir(parents=True, mode=0o700)
    summary = {"source_sha": F2, "van_sha": VAN, "status": "NOT_RUN",
               "started_utc": utc(), "steps": [], "notes": [],
               "unit_tests": "NOT_RERUN", "visual_parity": "NOT_RUN",
               "live_exchange": "NOT_RUN", "published_ports": False}
    active = set()

    def require(args, log, timeout=300):
        cp = runner.exec(args, out / log, timeout)
        if cp.returncode:
            raise RuntimeError(f"exit {cp.returncode}; see {log}")
        return cp

    def fetch(source, sha, log):
        # Use the already-authenticated Windows transport. No ref/FETCH_HEAD writes.
        gitargs = ["fetch", "--no-tags", "--no-write-fetch-head", source, sha]
        command = runner.windows_git(gitargs) or ["git", "-C", str(repo), *gitargs]
        require(command, log)

    def container(key, src, tmp, command, log, network="none"):
        name = "ci01-f2-" + runner.run_id + "-" + log.split(".")[0]
        active.add(name)
        cmd = runner.container_cmd(key, src, tmp, command, network=network, name=name)
        # Store output outside the disposable worktree, no host port published.
        cmd[2:2] = ["--mount", f"type=bind,src={out},dst=/results", "--shm-size=1g"]
        if not runner.step(summary, log, cmd, out, log, timeout=1800):
            raise RuntimeError("container phase failed; see " + log)
        active.discard(name)

    try:
        origin = runner.exec(["git", "-C", str(repo), "remote", "get-url", "origin"], quiet=True)
        if origin.returncode or not git_origin_is_github(origin.stdout.strip()):
            raise RuntimeError("Canonical TradeOps origin required")
        if runner.remote_sha(64) != F2:
            raise StaleShaError("F2 PR/branch HEAD moved; review new SHA first")
        summary["remote_sha_before"] = F2
        # Require exact-SHA owner CI evidence before skipping unit tests.
        ci_root = Path.home() / "tradeops-acceptance-results" / "pr-64" / F2
        qualified = []
        for path in sorted(ci_root.rglob("summary.json")):
            data = json.loads(path.read_text())
            counts = data.get("test_counts") or {}
            if (data.get("source_sha") == F2 and data.get("status") == "PASS"
                    and counts.get("passed", 0) >= 9 and counts.get("failed") == 0
                    and counts.get("skipped") == 0
                    and any(s.get("name") == "React build" and s.get("status") == "PASS"
                            for s in data.get("steps", []))):
                qualified.append(path)
        if not qualified:
            raise RuntimeError("Exact F2 CI PASS evidence missing; no browser qualification")
        shutil.copyfile(qualified[-1], out / "prior-ci-summary.json")
        summary["prior_ci_evidence"] = str(qualified[-1])
        runner.images["playwright"] = "mcr.microsoft.com/playwright:v1.56.1-noble"
        runner.require_images(["node", "playwright"], out)
        fetch("origin", F2, "f2-fetch.log")
        fetch("https://github.com/avysotsky/alexvysotsky.com.git", VAN, "van-fetch.log")
        with tempfile.TemporaryDirectory(prefix="ci01-f2-browser-") as directory:
            tmp = Path(directory)
            for name in ("home", "npm", "nuget", "browser"):
                (tmp / name).mkdir()
            source, van = tmp / "source", tmp / "van"
            worktrees = []
            try:
                for path, sha in ((source, F2), (van, VAN)):
                    require(["git", "-C", str(repo), "worktree", "add", "--detach", str(path), sha],
                            path.name + "-worktree.log")
                    worktrees.append(path)
                    actual = require(["git", "-C", str(path), "rev-parse", "HEAD"],
                                     path.name + "-identity.log").stdout.strip()
                    if actual != sha:
                        raise StaleShaError("Detached worktree SHA mismatch")
                app = "apps/coincall-terminal"
                container("node", source, tmp, ["bash", "-euc",
                    f"cd {app}; node --version; npm install --no-package-lock --no-audit --no-fund; "
                    "npm ls --depth=0; npm run build; cp -a dist /results/dist"],
                    "build.log", "bridge")
                for name in ("browser-check.mjs", "serve-static.mjs", "van-browser-parity.mjs"):
                    shutil.copyfile(source / app / "qa" / name, tmp / "browser" / name)
                container("playwright", source, tmp, ["bash", "-euc",
                    "cd /scratch/browser; npm install --no-save --no-package-lock "
                    "--no-audit --no-fund playwright@1.56.1"], "browser-install.log", "bridge")
                # Both browser phases run with Docker network=none, in addition
                # to the QA scripts' outbound request blocks. Loopback still works.
                browser_cmd = """cd /scratch/browser
node serve-static.mjs /results/dist 5181 > /results/serve.log 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT
node -e 'const t=setInterval(async()=>{try{const r=await fetch("http://127.0.0.1:5181/");if(r.ok){clearInterval(t);process.exit(0)}}catch{}},100);setTimeout(()=>process.exit(1),15000)'
set +e
node browser-check.mjs /results/ui "$1" > /results/browser.log 2>&1
smoke=$?
node van-browser-parity.mjs /results/dist /scratch/van/frontend/dist /results/van-ui "$1" "$2" > /results/van-parity.log 2>&1
parity=$?
cat /results/browser.log /results/van-parity.log
test "$smoke" -eq 0 && test "$parity" -eq 0
"""
                container("playwright", source, tmp,
                          ["bash", "-euc", browser_cmd, "browser", F2, VAN], "chromium.log")
                smoke = json.loads((out / "ui/browser-check.json").read_text())
                parity = json.loads((out / "van-ui/parity-manifest.json").read_text())
                if not (smoke.get("sha") == F2 and smoke.get("status") == "PASS"
                        and smoke.get("passed") == 20 and smoke.get("failed") == 0
                        and parity.get("reactSha") == F2 and parity.get("vanSha") == VAN
                        and parity.get("status") == "PASS"
                        and parity.get("summary", {}).get("passed") == 14
                        and parity.get("summary", {}).get("failed") == 0):
                    raise RuntimeError("Browser evidence missing, stale or incomplete")
                pngs = list(out.glob("ui/*.png")) + list(out.glob("van-ui/*.png"))
                for path in pngs:
                    with path.open("rb") as stream:
                        if stream.read(8) != b"\x89PNG\r\n\x1a\n":
                            raise RuntimeError("Invalid PNG: " + path.name)
                if len(pngs) < 28:
                    raise RuntimeError("Incomplete screenshot set")
                summary.update(status="PASS", capture="CAPTURED", png_count=len(pngs))
            finally:
                for name in list(active):
                    runner.docker(["rm", "-f", name], quiet=True)
                    active.discard(name)
                for path in reversed(worktrees):
                    require(["git", "-C", str(repo), "worktree", "remove", "--force", str(path)],
                            path.name + "-cleanup.log")
    except StaleShaError as exc:
        summary.update(status="STALE_SHA")
        summary["notes"].append(str(exc))
    except Exception as exc:
        summary.update(status="FAIL")
        summary["notes"].append(str(exc))
    finally:
        try:
            summary["remote_sha_after"] = runner.remote_sha(64)
            if summary["remote_sha_after"] != F2:
                summary["status"] = "STALE_SHA"
        except Exception as exc:
            if summary["status"] == "PASS":
                summary["status"] = "BLOCKED_ENV"
            summary["notes"].append("Final HEAD check: " + str(exc))
        summary.update(completed_utc=utc(), images=runner.image_info)
        (out / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
        (out / "status.txt").write_text(summary["status"] + ": browser capture only; visual review pending\n")
        args.export_dir.mkdir(parents=True, exist_ok=True)
        archive = args.export_dir / ("TradeOps-visual-" + runner.run_id + ".zip")
        f1 = Path.home() / "tradeops-acceptance-results" / "f1-68" / F1
        # Explicit evidence only; never archive repository/config/.env/credentials.
        with zipfile.ZipFile(archive, "x", zipfile.ZIP_DEFLATED) as z:
            for label, folder in (("F2", out), ("F1", f1)):
                for pattern in ("summary.json", "status.txt", "prior-ci-summary.json",
                                "browser.log", "van-parity.log", "build.log", "chromium.log",
                                "ui/*.png", "ui/*.json", "van-ui/*.png", "van-ui/*.json",
                                "screenshots/*.png", "screenshots/*.json", "screenshots/*.md"):
                    for path in sorted(folder.glob(pattern)):
                        if path.is_file() and not path.is_symlink():
                            z.write(path, label + "/" + str(path.relative_to(folder)))
        print(json.dumps(summary, indent=2))
        print("EVIDENCE_ARCHIVE=" + str(archive), flush=True)
    return 0 if summary["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
