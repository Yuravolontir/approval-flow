#!/usr/bin/env bash
# verify-concurrency.sh
# Live check for Bug 1: budget reserve must be optimistic-concurrent (ETag CAS),
# so two concurrent reserves that together exceed the remaining budget can never
# both succeed. Before the fix this reproduced a lost update / overspend.
#
# Requires the stack up (docker compose up) with payment-service on the compose
# network. Reads/writes go through a throwaway curl container because
# payment-service is not published to the host; budget state is read from Redis.
#
# The load-bearing invariant is NOT "available == 400" — a naive read-modify-write
# ALSO leaves available at 400 here (last write wins keeps one reservation), so
# that number does not distinguish the bug from the fix. The honest signal is:
#   spent == success_count * AMOUNT     (every approval maps to a real debit)
# On the buggy code two reserves succeed but only one debit lands (spent=AMOUNT),
# so this equality breaks. On the fixed code exactly one wins and it holds.
set -euo pipefail

NET="${APPROVAL_NET:-finalprj_approval-flow}"
REDIS_CTR="${REDIS_CTR:-finalprj-redis-1}"
DEPT="${DEPT:-marketing-2026Q2}"
AMOUNT="${AMOUNT:-600}"
KEY="payment-service||budget:${DEPT}"

read_available() {
  docker exec "$REDIS_CTR" redis-cli HGET "$KEY" data \
    | python3 -c 'import sys,json; print(json.load(sys.stdin)["available"])'
}

BEFORE=$(read_available)
echo "=== BUDGET BEFORE ==="
echo "available = ${BEFORE}"

# The race is only meaningful when the budget covers ONE reserve but not TWO.
if ! python3 -c "import sys; a=float('${BEFORE}'); amt=float('${AMOUNT}'); sys.exit(0 if amt <= a < 2*amt else 1)"; then
  echo "SKIP: available=${BEFORE} is not in [${AMOUNT}, $((AMOUNT*2))); reset the stack (seed=1000) before running." >&2
  exit 2
fi

echo ""
echo "=== RACE: two concurrent \$${AMOUNT} reserves on ${DEPT} ==="
OUT=$(docker run --rm --network "$NET" curlimages/curl:latest sh -c '
  A='"'"'{"Department":"'"$DEPT"'","Amount":'"$AMOUNT"',"InvoiceId":"RACE-A","IdempotencyKey":"race-A-'"$RANDOM"'"}'"'"'
  B='"'"'{"Department":"'"$DEPT"'","Amount":'"$AMOUNT"',"InvoiceId":"RACE-B","IdempotencyKey":"race-B-'"$RANDOM"'"}'"'"'
  curl -s -X POST http://payment-service:8080/budget/reserve -H "Content-Type: application/json" -d "$A" > /tmp/a &
  curl -s -X POST http://payment-service:8080/budget/reserve -H "Content-Type: application/json" -d "$B" > /tmp/b &
  wait
  echo "A:$(cat /tmp/a)"
  echo "B:$(cat /tmp/b)"
')
echo "$OUT"

SUCCESS_COUNT=$(echo "$OUT" | grep -o '"success":true' | wc -l | tr -d ' ')

AFTER=$(read_available)
echo ""
echo "=== BUDGET AFTER ==="
echo "available = ${AFTER}"

SPENT=$(python3 -c "print(round(float('${BEFORE}') - float('${AFTER}'), 2))")

echo ""
echo "=== VERDICT ==="
echo "concurrent successes = ${SUCCESS_COUNT} (correct app: <=1)"
echo "spent = ${SPENT}, expected = success_count * AMOUNT = $(python3 -c "print(round(${SUCCESS_COUNT}*float('${AMOUNT}'),2))")"
echo "available after = ${AFTER} (must be >= 0)"

FAIL=0
[ "$SUCCESS_COUNT" -gt 1 ] && { echo "FAIL: more than one reserve won -> overspend (no ETag)"; FAIL=1; }
python3 -c "import sys; sys.exit(0 if float('${AFTER}') >= 0 else 1)" || { echo "FAIL: available went negative"; FAIL=1; }
python3 -c "import sys; sys.exit(0 if abs(float('${SPENT}') - ${SUCCESS_COUNT}*float('${AMOUNT}')) < 0.01 else 1)" \
  || { echo "FAIL: spent (${SPENT}) != approvals * amount -> lost update"; FAIL=1; }

if [ "$FAIL" -eq 0 ]; then
  echo "PASS: at most one reserve won, every approval maps to a real debit, no overspend"
else
  exit 1
fi
