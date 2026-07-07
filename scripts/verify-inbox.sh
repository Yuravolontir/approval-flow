#!/usr/bin/env bash
# verify-inbox.sh
# Live proof for F1: /invoice-submitted claimed a top-of-handler idempotency key BEFORE
# any durable work. Under at-least-once pub/sub, a crash after the claim but before the
# first SaveState made redelivery hit the claim, get 200, and DROP the message -> the
# invoice was stuck forever (HumanReview/Reject/Duplicate had no saga to self-heal).
#
# Fix: a two-state inbox (InProgress -> Completed) + a startup barrier that repairs any
# InProgress entry left by a crash, per the durable state that survived:
#   - null state             -> remove (reprocessable)
#   - handled (terminal/inflight/queued) -> promote to Completed (no re-drive, no double-pay)
#   - AutoApproved+NotStarted -> re-drive saga to Paid
#   - PendingReview           -> re-add to hitl:queue
#
# Ground truth is Redis, not the app's own logs.
#
# Scenarios:
#   2 (honest crash) AutoApprove, fault AFTER SaveState BEFORE saga: barrier re-drives to
#     Paid with a SINGLE budget debit. This is the headline (real crash + money ground truth).
#   3 (honest crash) HumanReview, fault AFTER SaveState BEFORE hitl:queue: barrier re-adds
#     to the queue. The HumanReview path had NO recovery before this fix.
#   1 (SEEDED, labelled) crash-before-any-SaveState: barrier removes the dead InProgress so
#     it is no longer a permanent poison entry. NOTE: an honest crash here loops forever,
#     because the fault is embedded in the message and Dapr redelivery re-enters the same
#     handler and re-crashes. A real transient crash (OOM/deploy) is not in the message, so
#     redelivery succeeds. The seed reproduces the exact durable footprint of that real crash.
set -uo pipefail

NET="${APPROVAL_NET:-finalprj_approval-flow}"
REDIS_CTR="${REDIS_CTR:-finalprj-redis-1}"
DAPR_HOST="${DAPR_HOST:-workflow-service}"
PUBSUB="${PUBSUB:-pubsub}"
TOPIC="${TOPIC:-invoice.submitted}"
DEPT="${DEPT:-engineering-2026Q2}"
WF_SVC="workflow-service"
WF_DAPR="workflow-service-dapr"

kv() { docker exec "$REDIS_CTR" redis-cli HGET "workflow-service||$1" data 2>/dev/null || true; }
budget_avail() {
  docker exec "$REDIS_CTR" redis-cli HGET "payment-service||budget:${DEPT}" data 2>/dev/null | python3 -c 'import sys,json
try: print(json.load(sys.stdin).get("available"))
except Exception: print("NONE")'
}
wf() {  # $1=id -> "status step" numeric
  kv "workflow:$1" | python3 -c 'import sys,json
try:
    d=json.load(sys.stdin); print(d.get("status"), d.get("currentSagaStep"))
except Exception: print("NONE NONE")'
}
inbox_status() {  # $1=id -> InProgress|Completed|NONE
  kv "inbox:invoice-submitted:$1" | python3 -c 'import sys,json
try:
    v=json.load(sys.stdin); print(v if v else "NONE")
except Exception: print("NONE")'
}
in_list() {  # $1=list-key $2=id -> YES|NO
  kv "$1" | python3 -c 'import sys,json
try: print("YES" if "'"$2"'" in json.load(sys.stdin) else "NO")
except Exception: print("NO")'
}
publish() {  # $1=json
  docker run --rm --network "$NET" curlimages/curl:latest -s -o /dev/null -w "%{http_code}" \
    -X POST "http://${DAPR_HOST}:3500/v1.0/publish/${PUBSUB}/${TOPIC}" \
    -H "Content-Type: application/json" -d "$1"
}
restart_wf() {
  docker compose up -d --force-recreate "$WF_SVC" "$WF_DAPR" >/dev/null 2>&1
  for i in $(seq 1 40); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' finalprj-${WF_SVC}-1 2>/dev/null || echo none)" = "healthy" ] && { echo "  (healthy after ${i}s)"; return; }
    sleep 1
  done
  echo "  (WARN: not healthy in 40s)"
}
reset_ids() {
  for id in "$@"; do
    docker exec "$REDIS_CTR" redis-cli DEL \
      "workflow-service||workflow:${id}" \
      "workflow-service||inbox:invoice-submitted:${id}" >/dev/null 2>&1 || true
  done
  docker exec "$REDIS_CTR" redis-cli DEL "workflow-service||inbox:index" >/dev/null 2>&1 || true
}

# Per-run-unique invoice ids so the script is safely re-runnable. A fixed id makes the
# budget reservation (whose id is derived deterministically from the invoice id) survive
# across runs: re-reserve is then idempotent and scenario 2's debit delta collapses to 0
# -> false FAIL. A fresh id every run yields a fresh inbox key and a fresh reservation.
RUN="$(date +%s)-$$"
I2="INBOX2-${RUN}"; I3="INBOX3-${RUN}"; I1="INBOX1-${RUN}"
FAIL=0

echo "############ reset test fixtures ############"
reset_ids "$I1" "$I2" "$I3"
echo "cleared workflow/inbox keys for $I1 $I2 $I3 and inbox:index"

###########################################################################
echo ""
echo "############ SCENARIO 2 (honest crash): AutoApprove, fault after SaveState before saga ############"
BUD_BEFORE=$(budget_avail); echo "budget ${DEPT} available BEFORE = ${BUD_BEFORE}"
EVT2=$(cat <<JSON
{"InvoiceId":"${I2}","CorrelationId":"corr-${I2}","Invoice":{
"Id":"${I2}","Submitter":"qa@corp","Department":"${DEPT}","Vendor":"Staples","VendorKnown":true,
"InvoiceNumber":"SUP-${I2}","Currency":"USD","Category":"office_supplies","Attendees":null,
"LineItems":[{"Description":"Paper","Quantity":2,"UnitPrice":45.0}],"TaxAmount":0,"Total":90.0,
"ReceiptPresent":true,"Date":"2026-07-01","Notes":null,"Scenario":"crash-after-savestate-before-saga"}}
JSON
)
echo "publish http=$(publish "$EVT2")"
echo "-- wait for crash to strand AutoApproved/NotStarted --"
ORPH=0
for _ in $(seq 1 90); do
  read -r st step <<<"$(wf "$I2")"
  run=$(docker inspect -f '{{.State.Running}}' finalprj-${WF_SVC}-1 2>/dev/null || echo unknown)
  if [ "$st" = "2" ] && [ "$step" = "0" ] && [ "$run" = "false" ]; then ORPH=1; break; fi
  sleep 1
done
if [ "$ORPH" = 1 ]; then
  echo "  orphan: status=AutoApproved(2) step=NotStarted(0) container_running=false"
  echo "  inbox=$(inbox_status "$I2")  in inbox:index=$(in_list inbox:index "$I2")  in saga:inflight=$(in_list saga:inflight "$I2")"
else
  echo "  INCONCLUSIVE: never observed the AutoApproved/NotStarted crash ($(wf "$I2"))"; FAIL=1
fi
echo "-- restart workflow-service + sidecar (barrier runs) --"
restart_wf
echo "-- wait for barrier to re-drive saga to Paid --"
REC=0
for _ in $(seq 1 45); do
  read -r st step <<<"$(wf "$I2")"
  [ "$st" = "6" ] && { REC=1; break; }
  sleep 1
done
read -r st step <<<"$(wf "$I2")"
BUD_AFTER=$(budget_avail)
DELTA=$(python3 -c "print(round(float('${BUD_BEFORE}')-float('${BUD_AFTER}'),2))" 2>/dev/null || echo NaN)
echo "  post-restart: status=${st} step=${step}  inbox=$(inbox_status "$I2")  in inbox:index=$(in_list inbox:index "$I2")  in saga:inflight=$(in_list saga:inflight "$I2")"
echo "  budget available AFTER = ${BUD_AFTER}  (debit delta = ${DELTA}, expect 90.0 = single debit)"
S2_OK=0
if [ "$REC" = 1 ] && [ "$(inbox_status "$I2")" = "Completed" ] && [ "$(in_list inbox:index "$I2")" = "NO" ] \
   && [ "$(in_list saga:inflight "$I2")" = "NO" ] && [ "$DELTA" = "90.0" ]; then
  echo "  SCENARIO 2 PASS: barrier re-drove to Paid, inbox Completed+deindexed, single \$90 debit (no double-pay)"; S2_OK=1
else
  echo "  SCENARIO 2 FAIL"; FAIL=1
fi

###########################################################################
echo ""
echo "############ SCENARIO 3 (honest crash): HumanReview, fault after SaveState before hitl:queue ############"
EVT3=$(cat <<JSON
{"InvoiceId":"${I3}","CorrelationId":"corr-${I3}","Invoice":{
"Id":"${I3}","Submitter":"qa@corp","Department":"${DEPT}","Vendor":"Staples","VendorKnown":true,
"InvoiceNumber":"SUP-${I3}","Currency":"USD","Category":"office_supplies","Attendees":null,
"LineItems":[{"Description":"Chairs","Quantity":1,"UnitPrice":500.0}],"TaxAmount":0,"Total":500.0,
"ReceiptPresent":true,"Date":"2026-07-01","Notes":null,"Scenario":"crash-after-savestate-before-queue"}}
JSON
)
echo "publish http=$(publish "$EVT3")   (Total=500 > ceiling 250 -> HumanReview)"
echo "-- wait for crash to strand PendingReview, not yet queued --"
ORPH=0
for _ in $(seq 1 90); do
  read -r st step <<<"$(wf "$I3")"
  run=$(docker inspect -f '{{.State.Running}}' finalprj-${WF_SVC}-1 2>/dev/null || echo unknown)
  if [ "$st" = "3" ] && [ "$run" = "false" ]; then ORPH=1; break; fi
  sleep 1
done
if [ "$ORPH" = 1 ]; then
  echo "  orphan: status=PendingReview(3) container_running=false"
  echo "  inbox=$(inbox_status "$I3")  in inbox:index=$(in_list inbox:index "$I3")  in hitl:queue=$(in_list hitl:queue "$I3")"
else
  echo "  INCONCLUSIVE: never observed the PendingReview crash ($(wf "$I3"))"; FAIL=1
fi
echo "-- restart (barrier runs) --"
restart_wf
echo "-- wait for barrier to re-add to hitl:queue --"
QOK=0
for _ in $(seq 1 45); do
  [ "$(in_list hitl:queue "$I3")" = "YES" ] && { QOK=1; break; }
  sleep 1
done
echo "  post-restart: in hitl:queue=$(in_list hitl:queue "$I3")  inbox=$(inbox_status "$I3")  in inbox:index=$(in_list inbox:index "$I3")"
S3_OK=0
if [ "$QOK" = 1 ] && [ "$(inbox_status "$I3")" = "Completed" ] && [ "$(in_list inbox:index "$I3")" = "NO" ]; then
  echo "  SCENARIO 3 PASS: barrier re-queued the stranded HumanReview invoice, inbox Completed+deindexed"; S3_OK=1
else
  echo "  SCENARIO 3 FAIL"; FAIL=1
fi

###########################################################################
echo ""
echo "############ SCENARIO 1 (SEEDED): crash-before-any-SaveState -> barrier removes dead InProgress ############"
echo "(seed reproduces a real transient crash's durable footprint; an honest message-embedded fault would loop)"
# Seed: inbox InProgress + in index, NO workflow state -- exactly what a crash at DoWorkAsync top leaves.
docker exec "$REDIS_CTR" redis-cli HSET "workflow-service||inbox:invoice-submitted:${I1}" data '"InProgress"' >/dev/null
docker exec "$REDIS_CTR" redis-cli HSET "workflow-service||inbox:index" data "[\"${I1}\"]" >/dev/null
docker exec "$REDIS_CTR" redis-cli DEL "workflow-service||workflow:${I1}" >/dev/null 2>&1 || true
echo "  seeded: inbox=$(inbox_status "$I1")  in inbox:index=$(in_list inbox:index "$I1")  workflow=$(wf "$I1")"
echo "-- restart (barrier runs) --"
restart_wf
echo "  post-restart: inbox=$(inbox_status "$I1")  in inbox:index=$(in_list inbox:index "$I1")"
S1_OK=0
if [ "$(inbox_status "$I1")" = "NONE" ] && [ "$(in_list inbox:index "$I1")" = "NO" ]; then
  echo "  SCENARIO 1 PASS: barrier removed the dead InProgress entry -> no permanent poison, reprocessable"; S1_OK=1
else
  echo "  SCENARIO 1 FAIL"; FAIL=1
fi

echo ""
echo "############ VERDICT ############"
echo "scenario 1 (seeded no-state remove):     $([ "${S1_OK}" = 1 ] && echo PASS || echo FAIL)"
echo "scenario 2 (honest crash AutoApprove):   $([ "${S2_OK}" = 1 ] && echo PASS || echo FAIL)"
echo "scenario 3 (honest crash HumanReview):   $([ "${S3_OK}" = 1 ] && echo PASS || echo FAIL)"
[ "$FAIL" = 0 ] && echo "ALL PASS" || { echo "SOME FAILED"; exit 1; }
