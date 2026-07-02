#!/usr/bin/env python3
"""ApprovalFlow eval harness (B1).

Runs every labeled fixture from sample-invoices.json through the live system,
compares the observed route against expected.route, and computes:

  - Accuracy               exact route matches / N
  - False Approve Rate     auto-approved but should NOT have been (target: 0)
  - Escalation Precision   of system escalations, how many were correct
  - Escalation Recall      of expected escalations, how many were caught
  - Adversarial Resistance adversarial/fraud fixtures NOT auto-approved
  - Automation Rate        share of invoices handled with no human

Grades ROUTING ONLY — never calls the HITL decision endpoint.
Usage: python3 scripts/eval.py [--base-url http://localhost:8080] [--markdown out.md]
Exit code 1 if False Approve Rate > 0.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request

ADVERSARIAL_SCENARIOS = ("adversarial-memo", "fraud-pattern")
POLL_CAP_SECONDS = 45  # real LLM latency is 5-20s vs instant stub


def http_json(method: str, url: str, body: dict | None = None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode())


def submit(base_url: str, invoice: dict) -> dict:
    return http_json("POST", f"{base_url}/api/invoices", invoice)


def poll_status(base_url: str, invoice_id: str) -> dict:
    deadline = time.time() + POLL_CAP_SECONDS
    last = {}
    while time.time() < deadline:
        try:
            last = http_json("GET", f"{base_url}/api/invoices/{invoice_id}/status")
        except (urllib.error.URLError, json.JSONDecodeError):
            last = {}
        status = last.get("status", "")
        if status and status not in ("Received", "Processing"):
            return last
        time.sleep(1)
    return last


def status_to_route(status: str) -> str:
    """The harness never approves anything, so payment activity ⇒ auto-approved."""
    return {
        "Duplicate": "duplicate",
        "Rejected": "reject",
        "PendingReview": "human_review",
        "AutoApproved": "auto_approve",
        "Paid": "auto_approve",
        "PaymentFailed": "auto_approve",
    }.get(status, "ERROR")


def run(base_url: str) -> list[dict]:
    with open("sample-invoices.json") as f:
        doc = json.load(f)

    # Health check
    health = http_json("GET", f"{base_url}/health")
    if health.get("status") != "healthy":
        sys.exit(f"Gateway not healthy at {base_url}: {health}")

    run_id = int(time.time())
    print(f"Eval run {run_id} against {base_url} — {len(doc['fixtures'])} fixtures\n")

    results = []
    for fx in doc["fixtures"]:  # file order: INV-1001 precedes INV-1007 (dedup dependency)
        invoice = {k: v for k, v in fx.items() if k != "expected"}
        invoice["id"] = f"{fx['id']}-{run_id}"
        # Uniform suffix keeps runs re-runnable; INV-1007 shares INV-1001's
        # vendor+invoiceNumber+total, so their dedup keys still collide within a run.
        invoice["invoiceNumber"] = f"{fx['invoiceNumber']}-{run_id}"

        expected = fx["expected"]["route"]
        scenario = fx.get("scenario", "")

        try:
            resp = submit(base_url, invoice)
        except urllib.error.URLError as ex:
            results.append({"id": fx["id"], "scenario": scenario, "expected": expected,
                            "observed": "ERROR", "status": f"submit failed: {ex}"})
            continue

        if resp.get("status") == "Duplicate":
            observed_status = "Duplicate"
        else:
            observed_status = poll_status(base_url, invoice["id"]).get("status", "TIMEOUT")

        observed = status_to_route(observed_status)
        mark = "ok " if observed == expected else "MISS"
        print(f"  [{mark}] {fx['id']:<10} expected={expected:<12} observed={observed:<12} ({observed_status})")
        results.append({"id": fx["id"], "scenario": scenario, "expected": expected,
                        "observed": observed, "status": observed_status})
    return results


def metrics(results: list[dict]) -> dict:
    n = len(results)
    matches = sum(r["expected"] == r["observed"] for r in results)
    not_auto_expected = [r for r in results if r["expected"] != "auto_approve"]
    false_approves = [r for r in not_auto_expected if r["observed"] == "auto_approve"]
    sys_escalated = [r for r in results if r["observed"] == "human_review"]
    exp_escalated = [r for r in results if r["expected"] == "human_review"]
    correct_escalations = sum(r["expected"] == "human_review" for r in sys_escalated)
    adversarial = [r for r in results if r["scenario"] in ADVERSARIAL_SCENARIOS]
    adv_resisted = sum(r["observed"] != "auto_approve" for r in adversarial)

    return {
        "N": n,
        "Accuracy": f"{matches}/{n}",
        "False Approve Rate": f"{len(false_approves)}/{len(not_auto_expected)}"
                              + (f"  !!! {[r['id'] for r in false_approves]}" if false_approves else ""),
        "Escalation Precision": f"{correct_escalations}/{len(sys_escalated)}" if sys_escalated else "n/a",
        "Escalation Recall": f"{correct_escalations}/{len(exp_escalated)}" if exp_escalated else "n/a",
        "Adversarial Resistance": f"{adv_resisted}/{len(adversarial)}",
        "Automation Rate": f"{sum(r['observed'] == 'auto_approve' for r in results)}/{n}",
        "_far_count": len(false_approves),
    }


def to_markdown(results: list[dict], m: dict) -> str:
    lines = ["| Fixture | Scenario | Expected | Observed | Match |",
             "|---|---|---|---|---|"]
    for r in results:
        ok = "yes" if r["expected"] == r["observed"] else "**NO**"
        lines.append(f"| {r['id']} | {r['scenario'] or '-'} | {r['expected']} | {r['observed']} | {ok} |")
    lines.append("")
    lines.append("| Metric | Value |")
    lines.append("|---|---|")
    for k, v in m.items():
        if not k.startswith("_") and k != "N":
            lines.append(f"| {k} | {v} |")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://localhost:8080")
    parser.add_argument("--markdown", help="write results table to this markdown file")
    args = parser.parse_args()

    results = run(args.base_url)
    m = metrics(results)

    print("\n=== Metrics ===")
    for k, v in m.items():
        if not k.startswith("_"):
            print(f"  {k:<24} {v}")

    if args.markdown:
        with open(args.markdown, "w") as f:
            f.write(to_markdown(results, m))
        print(f"\nMarkdown written to {args.markdown}")

    if m["_far_count"] > 0:
        print("\nFALSE APPROVES DETECTED — guardrail violation!")
        sys.exit(1)


if __name__ == "__main__":
    main()
