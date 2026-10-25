#!/usr/bin/env bash
# Run on WSL2 Ubuntu only. Never changes other branches or hosted workflows.
set -Eeuo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$script_dir/local_acceptance.py" "$@"
