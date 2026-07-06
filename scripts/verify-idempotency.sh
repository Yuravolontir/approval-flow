#!/usr/bin/env bash
# verify-idempotency.sh
# Live check for Bug 2: the invoice.submitted subscriber must be idempotent under
# Dapr's at-least-once delivery. A redelivery of the same event (same InvoiceId)
# must NOT reprocess the invoice — otherwise the payment runs twice and the
# dashboard counter double-counts one invoice.
#
# Method: publish REDELIVER-01 via the Dapr pub/sub API, wait until the workflow
# reaches a terminal state, then publish the SAME event again (the redelivery).
# The dashboard TotalProcessed must advance by exactly 1 across both deliveries.
#
# Requires the stack up. Publishes through the workflow sidecar's Dapr API and
# reads dashboard stats from Redis, both via a throwaway curl container on the
# compose network.
#
# Pass  (fixed app): TotalProcessed delta == 1.
# Fail  (buggy app): delta >= 2 -> redelivery reprocessed -> double count.
set -euo pipefail

NET="${APPROVAL_NET:-finalprj_approval-flow}"
REDIS_CTR="${REDIS_CTR:-finalprj-redis-1}"
# The Dapr sidecar runs with network_mode: service:workflow-service, so it shares
# the workflow-service network identity; its HTTP port 3500 is reachable there.
DAPR_HOST="${DAPR_HOST:-workflow-service}"
PUBSUB="${PUBSUB:-pubsub}"
TOPIC="${TOPIC:-invoice.submitted}"
INVOICE_ID="${INVOICE_ID:-REDELIVER-01}"
EVT_FILE="${EVT_FILE:-$(dirname "$0")/../.redeliver-evt.json}"

# Event payload (self-contained so the script needs no external file).
read -r -d '' EVT <<JSON || true
{
  "InvoiceId": "${INVOICE_ID}",
  "CorrelationId": "corr-${INVOICE_ID}",
  "Invoice": {
    "Id": "${INVOICE_ID}", "Submitter": "qa@corp", "Department": "engineering-2026Q2",
    "Vendor": "Staples", "VendorKnown": true, "InvoiceNumber": "SUP-${INVOICE_ID}",
    "Currency": "USD", "Category": "office_supplies", "Attendees": null,
    "LineItems": [{"Description": "Printer paper", "Quantity": 2, "UnitPrice": 45.0}],
    "TaxAmount": 0, "Total": 90.0, "ReceiptPresent": true, "Date": "2026-07-01", "Notes": null
  }
}
JSON

read_processed() {
  local raw
  raw=$(docker exec "$REDIS_CTR" redis-cli HGET "workflow-service||dashboard:stats" data 2>/dev/null || true)
  if [ -z "$raw" ] || [ "$raw" = "" ]; then echo 0; return; fi
  echo "$raw" | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin); print(int(d.get("totalProcessed",0)))
except Exception:
    print(0)'
}

publish_once() {
  docker run --rm --network "$NET" curlimages/curl:latest \
    -s -o /dev/null -w "%{http_code}" \
    -X POST "http://${DAPR_HOST}:3500/v1.0/publish/${PUBSUB}/${TOPIC}" \
    -H "Content-Type: application/json" -d "$EVT"
}

wait_terminal() {
  # Poll workflow state until status is a terminal value (Paid/PaymentFailed/etc.)
  for _ in $(seq 1 40); do
    local st
    st=$(docker exec "$REDIS_CTR" redis-cli HGET "workflow-service||workflow:${INVOICE_ID}" data 2>/dev/null || true)
    if echo "$st" | grep -qi -e '"status":"Paid"' -e '"status":"PaymentFailed"' -e '"status":[0-9]'; then return 0; fi
    sleep 1
  done
  return 0
}

BEFORE=$(read_processed)
echo "=== dashboard TotalProcessed BEFORE = ${BEFORE} ==="

echo ""
echo "=== delivery #1 of ${INVOICE_ID} ==="
CODE1=$(publish_once); echo "publish http=${CODE1}"
wait_terminal
MID=$(read_processed)
echo "TotalProcessed after delivery #1 = ${MID}"

echo ""
echo "=== delivery #2 (redelivery of the SAME event) ==="
CODE2=$(publish_once); echo "publish http=${CODE2}"
sleep 3
AFTER=$(read_processed)
echo "TotalProcessed after delivery #2 = ${AFTER}"

DELTA=$((AFTER - BEFORE))
echo ""
echo "=== VERDICT ==="
echo "TotalProcessed delta across two deliveries = ${DELTA} (correct app: 1)"
if [ "$DELTA" -ge 2 ]; then
  echo "FAIL: redelivery was reprocessed -> double count (no idempotency guard)"
  exit 1
elif [ "$DELTA" -le 0 ]; then
  echo "INCONCLUSIVE: invoice was not processed at all (delta=${DELTA}); check routing/state"
  exit 2
else
  echo "PASS: redelivery acked without reprocessing -> counted exactly once"
fi
