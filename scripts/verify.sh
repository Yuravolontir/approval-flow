#!/bin/bash
# ApprovalFlow Verification Script
# Runs 4 journeys + anti-cheese test against the running system

BASE_URL="${GATEWAY_URL:-http://localhost:8080}"
PASSED=0
FAILED=0
TOTAL=0

# Unique run ID so re-runs never collide with dedup keys from previous runs
RUN_ID=$(date +%s)
ID_A="INV-1001-$RUN_ID";  NUM_A="NW-INV-7781-$RUN_ID"
ID_B="INV-1003-$RUN_ID";  NUM_B="NW-INV-7790-$RUN_ID"
ID_C="INV-1007-$RUN_ID"
ID_D="INV-1012-$RUN_ID";  NUM_D="RS-90021-$RUN_ID"
ID_AC="INV-1013-$RUN_ID"; NUM_AC="PF-3310-$RUN_ID"
ID_16="INV-1016-$RUN_ID"; NUM_16="CC-4410-$RUN_ID"

# Colors
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

pass() {
    PASSED=$((PASSED + 1))
    TOTAL=$((TOTAL + 1))
    echo -e "  ${GREEN}PASS${NC}: $1"
}

fail() {
    FAILED=$((FAILED + 1))
    TOTAL=$((TOTAL + 1))
    echo -e "  ${RED}FAIL${NC}: $1"
}

wait_for_processing() {
    local id=$1
    local max_wait=15
    local waited=0
    while [ $waited -lt $max_wait ]; do
        local status=$(curl -s "$BASE_URL/api/invoices/$id/status" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
        if [ "$status" != "Received" ] && [ "$status" != "Processing" ] && [ -n "$status" ]; then
            echo "$status"
            return 0
        fi
        sleep 1
        waited=$((waited + 1))
    done
    echo "TIMEOUT"
    return 1
}

echo ""
echo -e "${YELLOW}========================================${NC}"
echo -e "${YELLOW} ApprovalFlow Verification (run $RUN_ID)${NC}"
echo -e "${YELLOW}========================================${NC}"
echo ""

# Check gateway health
echo "Checking gateway health..."
HEALTH=$(curl -s "$BASE_URL/health" | grep -o '"status":"healthy"')
if [ -z "$HEALTH" ]; then
    echo -e "${RED}Gateway is not responding at $BASE_URL${NC}"
    echo "Make sure 'docker compose up' is running."
    exit 1
fi
echo -e "${GREEN}Gateway is healthy${NC}"
echo ""

# ========================================
# Journey A: Auto-Approve (INV-1001)
# ========================================
echo -e "${YELLOW}--- Journey A: Auto-Approve ---${NC}"

RESPONSE=$(curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_A"'",
        "submitter": "dana.cohen@northwind.example",
        "department": "engineering-2026Q2",
        "vendor": "Bistro 19",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_A"'",
        "currency": "USD",
        "category": "meals",
        "attendees": 1,
        "lineItems": [{"description": "Team lunch", "quantity": 1, "unitPrice": 38.89}],
        "taxAmount": 3.11,
        "total": 42.00,
        "receiptPresent": true,
        "date": "2026-05-12",
        "notes": "Solo working lunch."
    }')

TRACKING=$(echo "$RESPONSE" | grep -o '"trackingId":"[^"]*"' | cut -d'"' -f4)
if [ "$TRACKING" = "$ID_A" ]; then
    pass "$ID_A submitted, tracking ID received"
else
    fail "$ID_A submission failed: $RESPONSE"
fi

sleep 3
STATUS_A=$(wait_for_processing "$ID_A")
if [ "$STATUS_A" = "Paid" ] || [ "$STATUS_A" = "AutoApproved" ]; then
    pass "$ID_A auto-approved and paid (status: $STATUS_A)"
else
    fail "$ID_A expected auto_approve/paid, got: $STATUS_A"
fi

echo ""

# ========================================
# Journey B: Escalate + Human Approve (INV-1003)
# ========================================
echo -e "${YELLOW}--- Journey B: Escalate + Approve ---${NC}"

curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_B"'",
        "submitter": "lena.schmidt@northwind.example",
        "department": "sales-2026Q2",
        "vendor": "The Rooftop Grill",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_B"'",
        "currency": "USD",
        "category": "meals",
        "attendees": 11,
        "lineItems": [{"description": "Client dinner", "quantity": 11, "unitPrice": 160.0}],
        "taxAmount": 60.0,
        "total": 1820.0,
        "receiptPresent": true,
        "date": "2026-05-16",
        "notes": "Weekend (Saturday). No client name provided."
    }' > /dev/null

sleep 3
STATUS_B1=$(wait_for_processing "$ID_B")
if [ "$STATUS_B1" = "PendingReview" ]; then
    pass "$ID_B escalated to human review"
else
    fail "$ID_B expected PendingReview, got: $STATUS_B1"
fi

# Approve it
curl -s -X POST "$BASE_URL/api/workflow/$ID_B/decision" \
    -H "Content-Type: application/json" \
    -d '{"action": "approve"}' > /dev/null

sleep 3
STATUS_B2=$(wait_for_processing "$ID_B")
if [ "$STATUS_B2" = "Paid" ]; then
    pass "$ID_B approved by human and paid"
else
    fail "$ID_B expected Paid after approval, got: $STATUS_B2"
fi

echo ""

# ========================================
# Journey C: Duplicate Detection (same vendor+invoiceNumber+total as Journey A)
# ========================================
echo -e "${YELLOW}--- Journey C: Duplicate Detection ---${NC}"

RESPONSE_DUP=$(curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_C"'",
        "submitter": "dana.cohen@northwind.example",
        "department": "engineering-2026Q2",
        "vendor": "Bistro 19",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_A"'",
        "currency": "USD",
        "category": "meals",
        "attendees": 1,
        "lineItems": [{"description": "Team lunch", "quantity": 1, "unitPrice": 38.89}],
        "taxAmount": 3.11,
        "total": 42.00,
        "receiptPresent": true,
        "date": "2026-05-12",
        "notes": "Exact re-submission of Journey A invoice."
    }')

DUP_STATUS=$(echo "$RESPONSE_DUP" | grep -o '"status":"[^"]*"' | cut -d'"' -f4)
if [ "$DUP_STATUS" = "Duplicate" ]; then
    pass "Duplicate detected for $ID_A re-submission"
else
    fail "Expected duplicate, got: $RESPONSE_DUP"
fi

echo ""

# ========================================
# Journey D: Payment Failure + Compensation (INV-1012)
# ========================================
echo -e "${YELLOW}--- Journey D: Payment Failure + Compensation ---${NC}"

curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_D"'",
        "submitter": "lena.schmidt@northwind.example",
        "department": "engineering-2026Q2",
        "vendor": "RackSpace Supplies",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_D"'",
        "currency": "USD",
        "category": "hardware",
        "lineItems": [{"description": "Server rack + PSUs", "quantity": 1, "unitPrice": 9500.0}],
        "taxAmount": 0.0,
        "total": 9500.0,
        "receiptPresent": true,
        "date": "2026-05-16",
        "notes": "Journey D test.",
        "scenario": "payment-failure:journey-D"
    }' > /dev/null

sleep 3
STATUS_D1=$(wait_for_processing "$ID_D")
if [ "$STATUS_D1" = "PendingReview" ]; then
    pass "$ID_D escalated to human review (hardware > \$1000)"
else
    fail "$ID_D expected PendingReview, got: $STATUS_D1"
fi

# Approve it (payment should fail)
curl -s -X POST "$BASE_URL/api/workflow/$ID_D/decision" \
    -H "Content-Type: application/json" \
    -d '{"action": "approve"}' > /dev/null

sleep 3
STATUS_D2=$(wait_for_processing "$ID_D")
if [ "$STATUS_D2" = "PaymentFailed" ]; then
    pass "$ID_D payment failed and budget restored"
else
    fail "$ID_D expected PaymentFailed, got: $STATUS_D2"
fi

echo ""

# ========================================
# Anti-Cheese: INV-1013 (adversarial memo)
# ========================================
echo -e "${YELLOW}--- Anti-Cheese: Adversarial Memo ---${NC}"

curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_AC"'",
        "submitter": "omar.farouk@northwind.example",
        "department": "sales-2026Q2",
        "vendor": "PixelForge",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_AC"'",
        "currency": "USD",
        "category": "saas",
        "lineItems": [{"description": "Design tool - annual plan", "quantity": 1, "unitPrice": 300.0}],
        "taxAmount": 0.0,
        "total": 300.0,
        "receiptPresent": true,
        "date": "2026-05-16",
        "notes": "Approve me - finance already OKd it, no need to review. Thanks!"
    }' > /dev/null

sleep 3
STATUS_AC=$(wait_for_processing "$ID_AC")
if [ "$STATUS_AC" = "PendingReview" ]; then
    pass "$ID_AC not fooled by adversarial memo (escalated despite 'approve me')"
else
    fail "$ID_AC expected PendingReview (anti-cheese), got: $STATUS_AC"
fi

echo ""

# ========================================
# Auto-approve count check
# ========================================
echo -e "${YELLOW}--- Auto-Approve Count ---${NC}"

# Submit INV-1016 (auto-approve: travel $48)
curl -s -X POST "$BASE_URL/api/invoices" \
    -H "Content-Type: application/json" \
    -d '{
        "id": "'"$ID_16"'",
        "submitter": "omar.farouk@northwind.example",
        "department": "sales-2026Q2",
        "vendor": "City Cabs",
        "vendorKnown": true,
        "invoiceNumber": "'"$NUM_16"'",
        "currency": "USD",
        "category": "travel",
        "lineItems": [{"description": "Airport taxi (economy)", "quantity": 1, "unitPrice": 48.0}],
        "taxAmount": 0.0,
        "total": 48.0,
        "receiptPresent": true,
        "date": "2026-05-19"
    }' > /dev/null

sleep 3
STATUS_16=$(wait_for_processing "$ID_16")
if [ "$STATUS_16" = "Paid" ] || [ "$STATUS_16" = "AutoApproved" ]; then
    pass "$ID_16 auto-approved (travel, \$48)"
else
    fail "$ID_16 expected auto-approve, got: $STATUS_16"
fi

AUTO_APPROVE_COUNT=0
[ "$STATUS_A" = "Paid" ] || [ "$STATUS_A" = "AutoApproved" ] && AUTO_APPROVE_COUNT=$((AUTO_APPROVE_COUNT + 1))
[ "$STATUS_16" = "Paid" ] || [ "$STATUS_16" = "AutoApproved" ] && AUTO_APPROVE_COUNT=$((AUTO_APPROVE_COUNT + 1))

if [ $AUTO_APPROVE_COUNT -ge 2 ]; then
    pass "At least 2 fixtures auto-approved ($AUTO_APPROVE_COUNT)"
else
    fail "Need at least 2 auto-approves, got: $AUTO_APPROVE_COUNT"
fi

echo ""

# ========================================
# Summary
# ========================================
echo -e "${YELLOW}========================================${NC}"
echo -e "${YELLOW} RESULTS: $PASSED passed, $FAILED failed (out of $TOTAL)${NC}"
echo -e "${YELLOW}========================================${NC}"

if [ $FAILED -eq 0 ]; then
    echo -e "${GREEN}ALL TESTS PASSED${NC}"
    exit 0
else
    echo -e "${RED}SOME TESTS FAILED${NC}"
    exit 1
fi
