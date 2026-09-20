#!/usr/bin/env bash
# The API-break guard compares against PackageValidationBaselineVersion. If that lags the last published
# release, everything added since is unguarded - so CI fails until the baseline is moved forward. Run after a
# release and it tells you the one line to change.
#
# nuget.org being unreachable is a WARNING, not a failure: an outage must not block an unrelated PR.
set -euo pipefail

csproj="${1:-src/Jev.Net/Jev.Net.csproj}"
baseline=$(grep -o '<PackageValidationBaselineVersion>[^<]*' "$csproj" | sed 's/.*>//')
[ -n "$baseline" ] || { echo "FAIL: no PackageValidationBaselineVersion in $csproj" >&2; exit 1; }

index=$(curl -fsS --max-time 20 https://api.nuget.org/v3-flatcontainer/jev.net/index.json) || {
  echo "::warning::could not reach nuget.org; baseline ($baseline) not verified"; exit 0; }

# Latest STABLE version: prereleases (anything with a '-') never become the baseline.
latest=$(printf '%s' "$index" | tr -d ' \n' | grep -o '"[0-9][^"]*"' | tr -d '"' | grep -v -- '-' | sort -t. -k1,1n -k2,2n -k3,3n | tail -1)
[ -n "$latest" ] || { echo "::warning::no published stable version found; baseline ($baseline) not verified"; exit 0; }

if [ "$baseline" = "$latest" ]; then
  echo "ok: API baseline $baseline is the latest published version"
else
  echo "FAIL: API baseline is $baseline but the latest published version is $latest." >&2
  echo "      Set <PackageValidationBaselineVersion>$latest</PackageValidationBaselineVersion> in $csproj." >&2
  exit 1
fi
