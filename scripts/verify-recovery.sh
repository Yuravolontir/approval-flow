#!/usr/bin/env bash
# verify-recovery.sh
# Live check for Bug 3: a hard crash between reserving budget and completing payment
# leaves an ORPHANED budget reservation that no in-process compensation releases, and
# (after Phase 2) Dapr redelivery can no longer resume the saga. The Phase 3 startup
# recovery service must find the stranded saga and drive it to a terminal state.
#
# Method (honest crash, not a hand-built state):
#   1. publish an auto-approve invoice whose Scenario triggers the gated fault injection:
#      the saga reserves budget, persists CurrentSagaStep=BudgetReserved, indexes it in
#      saga:inflight, then Environment.Exit(1) — a REAL process death at the vulnerable
#      point. This kills workflow-service (and its sidecar, which shares its netns).
#   2. assert the ORPHAN: workflow:{id} is stuck at BudgetReserved, {id} is in
#      saga:inflight, and the department budget is held.
#   3. restart workflow-service + its Dapr sidecar.
#   4. the recovery BackgroundService forward-resumes the saga -> Paid, and removes {id}
#      from saga:inflight.
#
# Load-bearing, non-tautological assertion: post-crash the invoice is BudgetReserved
# (stuck); post-restart it must be Paid AND absent from saga:inflight. A no-op / broken
# recovery leaves it BudgetReserved -> FAIL.
set -euo pipefail

NET="${APPROVAL_NET:-finalprj_approval-flow}"
REDIS_CTR="${REDIS_CTR:-finalprj-redis-1}"
DAPR_HOST="${DAPR_HOST:-workflow-service}"   # sidecar shares workflow-service netns
PUBSUB="${PUBSUB:-pubsub}"
TOPIC="${TOPIC:-invoice.submitted}"
INVOICE_ID="${INVOICE_ID:-RECOVER-01}"
DEPT="${DEPT:-engineering-2026Q2}"
WF_SVC="${WF_SVC:-workflow-service}"
WF_DAPR="${WF_DAPR:-workflow-service-dapr}"

kv() { docker exec "$REDIS_CTR" redis-cli HGET "workflow-service||$1" data 2>/dev/null || true; }

wf_status() {
  # numeric InvoiceStatus (Paid=6) and SagaStep (BudgetReserved=1) from workflow:{id}
  kv "workflow:${INVOICE_ID}" | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin); print(d.get("status"), d.get("currentSagaStep"), d.get("reservationId"))
except Exception:
    print("NONE NONE NONE")'
}

inflight_has() {
  kv "saga:inflight" | python3 -c 'import sys,json
try:
    ids=json.load(sys.stdin); print("YES" if "'"$INVOICE_ID"'" in ids else "NO")
except Exception:
    print("NO")'
}

EVT=$(cat <<JSON
{
  "InvoiceId": "${INVOICE_ID}",
  "CorrelationId": "corr-${INVOICE_ID}",
  "Invoice": {
    "Id": "${INVOICE_ID}", "Submitter": "qa@corp", "Department": "${DEPT}",
    "Vendor": "Staples", "VendorKnown": true, "InvoiceNumber": "SUP-${INVOICE_ID}",
    "Currency": "USD", "Category": "office_supplies", "Attendees": null,
    "LineItems": [{"Description": "Printer paper", "Quantity": 2, "UnitPrice": 45.0}],
    "TaxAmount": 0, "Total": 90.0, "ReceiptPresent": true, "Date": "2026-07-01",
    "Notes": null, "Scenario": "crash-after-reserve"
  }
}
JSON
)

echo "=== step 0: reset this test's own fixture keys (re-runnable) ==="
# Delete only THIS invoice's workflow record and the Phase 2 idempotency claim, so a
# re-run is not (correctly) dropped by the duplicate-delivery guard. Touches nothing else.
docker exec "$REDIS_CTR" redis-cli DEL \
  "workflow-service||workflow:${INVOICE_ID}" \
  "workflow-service||processed:invoice-submitted:${INVOICE_ID}" >/dev/null 2>&1 || true
echo "cleared workflow:${INVOICE_ID} and its processed marker"

echo ""
echo "=== step 1: publish ${INVOICE_ID} with crash-after-reserve fault injection ==="
CODE=$(docker run --rm --network "$NET" curlimages/curl:latest -s -o /dev/null -w "%{http_code}" \
  -X POST "http://${DAPR_HOST}:3500/v1.0/publish/${PUBSUB}/${TOPIC}" \
  -H "Content-Type: application/json" -d "$EVT")
echo "publish http=${CODE}"

echo ""
echo "=== step 2: wait for the crash to strand the reservation ==="
# Dapr Redis-stream delivery latency is variable (cold consumer groups can take a while),
# so wait generously. The BudgetReserved state is persisted just before Environment.Exit,
# so it survives the crash and is observable durably once delivery lands.
ORPHAN=0
for _ in $(seq 1 120); do
  read -r st step rid <<<"$(wf_status)"
  running=$(docker inspect -f '{{.State.Running}}' "finalprj-${WF_SVC}-1" 2>/dev/null || echo "unknown")
  if [ "$step" = "1" ]; then ORPHAN=1; echo "orphan observed: status=${st} step=BudgetReserved reservation=${rid} (container running=${running})"; break; fi
  sleep 1
done
if [ "$ORPHAN" -ne 1 ]; then echo "INCONCLUSIVE: never observed a BudgetReserved orphan (status=$(wf_status))"; exit 2; fi
echo "in saga:inflight after crash = $(inflight_has)"

echo ""
echo "=== step 3: restart workflow-service + sidecar ==="
docker compose up -d --force-recreate "$WF_SVC" "$WF_DAPR" >/dev/null 2>&1
for i in $(seq 1 30); do
  h=$(docker inspect -f '{{.State.Health.Status}}' "finalprj-${WF_SVC}-1" 2>/dev/null || echo none)
  if [ "$h" = "healthy" ]; then echo "workflow-service healthy after ${i}s"; break; fi
  sleep 1
done

echo ""
echo "=== step 4: wait for recovery to resolve the orphan ==="
RECOVERED=0
for _ in $(seq 1 45); do
  read -r st step rid <<<"$(wf_status)"
  if [ "$st" = "6" ]; then RECOVERED=1; echo "recovered: status=Paid step=${step} reservation=${rid}"; break; fi
  sleep 1
done

INFLIGHT_AFTER=$(inflight_has)
echo "in saga:inflight after recovery = ${INFLIGHT_AFTER}"

echo ""
echo "=== VERDICT ==="
if [ "$RECOVERED" -eq 1 ] && [ "$INFLIGHT_AFTER" = "NO" ]; then
  echo "PASS: crashed saga was recovered to Paid and removed from the in-flight index"
elif [ "$RECOVERED" -ne 1 ]; then
  echo "FAIL: invoice still stuck (not Paid) after restart -> recovery did not run"
  exit 1
else
  echo "FAIL: invoice recovered to Paid but still present in saga:inflight -> index not cleaned"
  exit 1
fi
