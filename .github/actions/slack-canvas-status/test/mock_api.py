#!/usr/bin/env python3
"""Mock GitHub API + Slack workflow webhook for update-canvas.sh flow tests.

Scenario is selected with the MOCK_SCENARIO env var:
  normal       - dev's newest deployment succeeded; prd's newest failed, an older one succeeded.
  degraded     - dev's newest deployment has no statuses yet, the next one failed, none
                 succeeded; prd has no deployments.
  empty        - no deployments in either environment.
  gh_error     - the deployments endpoint returns HTTP 500.
  webhook_fail - deployment data as in normal, but the webhook responds HTTP 500.
Every request is appended to MOCK_LOG as one JSON line: {"path": ..., "body": ...},
except GET /ping (a readiness probe) which is answered 200 and not logged.
"""
import json
import os
import re
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import urlparse, parse_qs

SCENARIO = os.environ.get("MOCK_SCENARIO", "normal")
LOG_PATH = os.environ["MOCK_LOG"]

DEPLOYMENTS = {
    "dev": [
        {
            "id": 2,
            "ref": "refs/heads/main",
            "sha": "aaaabbbbccccddddeeeeffff0000111122223333",
            "payload": {"dockerImage": "quay.io/decentraland/pulse-server:aaaabbbbccccddddeeeeffff0000111122223333"},
        },
    ],
    "prd": [
        {
            "id": 12,
            "ref": "refs/tags/v0.9.3",
            "sha": "9999888877776666555544443333222211110000",
            "payload": {"dockerImage": "quay.io/decentraland/pulse-server:latest"},
        },
        {
            "id": 11,
            "ref": "refs/tags/v0.9.2",
            "sha": "1234567890abcdef1234567890abcdef12345678",
            "payload": {"dockerImage": "quay.io/decentraland/pulse-server:59791d9"},
        },
    ],
}

DEPLOYMENTS_DEGRADED = {
    "dev": [
        {
            "id": 21,
            "ref": "refs/heads/feat/hotfix",
            "sha": "ccccddddeeeeffff0000111122223333aaaabbbb",
            "payload": {"dockerImage": "quay.io/decentraland/pulse-server:hotfix"},
        },
        {
            "id": 22,
            "ref": "refs/heads/main",
            "sha": "bbbbccccddddeeeeffff0000111122223333aaaa",
            "payload": {"dockerImage": "quay.io/decentraland/pulse-server:latest"},
        },
    ],
    "prd": [],
}

STATUSES = {
    2: {"state": "success", "created_at": "2026-07-18T10:00:00Z"},
    12: {"state": "failure", "created_at": "2026-07-19T08:30:00Z"},
    11: {"state": "success", "created_at": "2026-07-15T17:11:32Z"},
    # 21 deliberately absent: a just-created deployment with no statuses yet.
    22: {"state": "failure", "created_at": "2026-07-19T09:00:00Z"},
}


class Handler(BaseHTTPRequestHandler):
    def _respond(self, payload, status=200):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _record(self, path, body):
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": path, "body": body}, ensure_ascii=False) + "\n")

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path == "/ping":
            self._respond({"ok": True})
            return
        self._record(parsed.path, self.path)
        if re.fullmatch(r"/repos/[^/]+/[^/]+/deployments", parsed.path):
            environment = parse_qs(parsed.query).get("environment", [""])[0]
            if SCENARIO == "gh_error":
                self._respond({"message": "boom"}, status=500)
            elif SCENARIO == "empty":
                self._respond([])
            elif SCENARIO == "degraded":
                self._respond(DEPLOYMENTS_DEGRADED.get(environment, []))
            else:
                self._respond(DEPLOYMENTS.get(environment, []))
            return
        match = re.fullmatch(r"/repos/[^/]+/[^/]+/deployments/(\d+)/statuses", parsed.path)
        if match:
            status = STATUSES.get(int(match.group(1)))
            self._respond([status] if status else [])
            return
        self._respond({"message": "not found"}, status=404)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")
        parsed = urlparse(self.path)
        self._record(parsed.path, body)
        if parsed.path == "/webhook":
            if SCENARIO == "webhook_fail":
                self._respond({"error": "trigger_failed"}, status=500)
            else:
                self._respond({"ok": True})
            return
        self._respond({"message": "not found"}, status=404)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
