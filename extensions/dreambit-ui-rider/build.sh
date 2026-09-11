#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

python3 tools/check_catalog.py

if [[ -x ./gradlew ]]; then
  exec ./gradlew buildPlugin
fi

if command -v gradle >/dev/null 2>&1; then
  exec gradle buildPlugin
fi

echo "Gradle 9+ is required. Install Gradle, or run build.ps1 on Windows to let the package download a local Gradle distribution." >&2
exit 2
