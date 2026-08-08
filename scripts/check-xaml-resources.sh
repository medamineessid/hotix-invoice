#!/bin/bash
# check-xaml-resources.sh — Verify all {StaticResource X} / {DynamicResource X}
# references in client/*.xaml resolve to a defined x:Key somewhere in the
# client XAML tree.
#
# LIMITATIONS (this is a first-pass filter, not a proof of correctness):
#   - Does NOT verify merge order within a single XAML file (a key defined
#     AFTER it's used in the same file can still fail at runtime).
#   - Does NOT resolve resources added programmatically in code-behind
#     (Application.Resources.Add(...)) or built-in system keys referenced
#     via x:Static.
#   - Does NOT check for resources defined in assemblies outside the client
#     directory (e.g. PresentationFramework).
#
# Usage: bash scripts/check-xaml-resources.sh
# CI integration: run as part of build pipeline; exit code 1 = missing keys.

set -euo pipefail
cd "$(dirname "$0")/.."

defined=$(grep -rhoE 'x:Key="[A-Za-z0-9_.]+"' client --include="*.xaml" \
    | sed -E 's/x:Key="([^"]+)"/\1/' | sort -u)

used=$(grep -rhoE '\{(Static|Dynamic)Resource +[A-Za-z0-9_.]+' client --include="*.xaml" \
    | sed -E 's/\{(Static|Dynamic)Resource +//' | sort -u)

missing=$(comm -23 <(echo "$used" | sort) <(echo "$defined" | sort))

if [ -n "$missing" ]; then
    echo "MISSING RESOURCE KEYS (referenced but never defined via x:Key):"
    echo "$missing"
    exit 1
fi

echo "All StaticResource/DynamicResource references resolve to a defined x:Key."
