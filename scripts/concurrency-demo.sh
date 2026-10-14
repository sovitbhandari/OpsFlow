#!/usr/bin/env bash
# Reproducible claim race against a running local API.
# Requires the development seed and two technician accounts in the same organization.
set -euo pipefail
base="${OPSFLOW_BASE_URL:-http://localhost:5080}"
org="${OPSFLOW_DEMO_ORG_ID:?set OPSFLOW_DEMO_ORG_ID}"
password="${Seed__DemoPassword:?set Seed__DemoPassword}"
work="${1:?work order id}"

login() {
  local email="$1" jar="$2"
  csrf="$(curl -s -c "$jar" "$base/api/auth/csrf" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
  curl -s -b "$jar" -c "$jar" -H "X-XSRF-TOKEN: $csrf" -H "Content-Type: application/json" \
    -d "{\"email\":\"$email\",\"password\":\"$password\"}" "$base/api/auth/login" >/dev/null
  echo "$csrf"
}

tmpdir="$(mktemp -d)"
csrf_a="$(login "tech.a.northwind@opsflow.example" "$tmpdir/a")"
csrf_b="$(login "tech.b.northwind@opsflow.example" "$tmpdir/b")"
claim() {
  local jar="$1" csrf="$2" key="$3" out="$4"
  curl -s -o "$out.body" -w "%{http_code}" -b "$jar" -c "$jar" \
    -H "X-XSRF-TOKEN: $csrf" -H "Idempotency-Key: $key" \
    -X POST "$base/api/organizations/$org/work-orders/$work/claim" > "$out.code"
}
claim "$tmpdir/a" "$csrf_a" "demo-claim-a-$work" "$tmpdir/a" &
claim "$tmpdir/b" "$csrf_b" "demo-claim-b-$work" "$tmpdir/b" &
wait
echo "Technician A: $(cat "$tmpdir/a.code")"
echo "Technician B: $(cat "$tmpdir/b.code")"
echo "Exactly one response should be 200 and the other 409."
