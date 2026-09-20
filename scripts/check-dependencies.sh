#!/usr/bin/env bash
# "One dependency" is a promise in the README, so it is checked against the thing people actually install:
# the packed .nupkg's nuspec, not the csproj. Fails if any target framework declares anything other than
# exactly Microsoft.Extensions.Logging.Abstractions.
#
#   scripts/check-dependencies.sh artifacts/Jev.Net.0.1.0.nupkg
set -euo pipefail

pkg="${1:?usage: check-dependencies.sh <path-to.nupkg>}"
allowed='Microsoft.Extensions.Logging.Abstractions'

nuspec=$(unzip -p "$pkg" '*.nuspec')
groups=$(printf '%s' "$nuspec" | grep -c '<group targetFramework=' || true)
deps=$(printf '%s' "$nuspec" | grep -o '<dependency id="[^"]*"' | sed 's/.*id="//; s/"$//' | sort | uniq -c | sed 's/^ *//')

echo "target frameworks: $groups"
echo "dependencies (count id):"
printf '%s\n' "$deps" | sed 's/^/  /'

[ "$groups" -ge 1 ] || { echo "FAIL: no dependency groups found in the nuspec - the check read nothing" >&2; exit 1; }

unexpected=$(printf '%s\n' "$deps" | awk '{print $2}' | grep -vx "$allowed" || true)
if [ -n "$unexpected" ]; then
  echo "FAIL: unexpected dependency: $unexpected" >&2
  echo "      Jev.Net promises exactly one ($allowed). If this is deliberate, it is a README change first." >&2
  exit 1
fi

count=$(printf '%s\n' "$deps" | awk -v a="$allowed" '$2==a {print $1}')
[ "${count:-0}" = "$groups" ] || { echo "FAIL: expected $allowed once per target framework ($groups), saw ${count:-0}" >&2; exit 1; }

echo "ok: exactly one dependency, in every target framework"
