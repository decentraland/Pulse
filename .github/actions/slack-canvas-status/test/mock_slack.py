#!/usr/bin/env python3
"""Mock Slack Web API for update-canvas.sh flow tests.

Scenario is selected with the MOCK_SCENARIO env var:
  has_canvas  - channel has a canvas, every section lookup matches.
  no_canvas   - channel has no canvas yet.
  no_sections - channel has a canvas, no section lookup matches.
Every request is appended to MOCK_LOG as one JSON line: {"path": ..., "body": ...}.
"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

SCENARIO = os.environ.get("MOCK_SCENARIO", "has_canvas")
LOG_PATH = os.environ["MOCK_LOG"]


class Handler(BaseHTTPRequestHandler):
    def _respond(self, payload):
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _record(self, path, body):
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(json.dumps({"path": path, "body": body}, ensure_ascii=False) + "\n")

    def do_GET(self):
        path = self.path.split("?")[0]
        self._record(path, self.path)
        if path == "/conversations.info":
            if SCENARIO == "no_canvas":
                self._respond({"ok": True, "channel": {"id": "C123", "properties": {}}})
            else:
                self._respond({"ok": True, "channel": {"id": "C123", "properties": {"canvas": {"file_id": "F999"}}}})
        else:
            self._respond({"ok": False, "error": "unknown_method"})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")
        path = self.path.split("?")[0]
        self._record(path, body)
        if path == "/canvases.sections.lookup":
            if SCENARIO == "no_sections":
                self._respond({"ok": True, "sections": []})
            else:
                marker = body["criteria"]["contains_text"]
                section_id = "sec-" + marker.replace(" ", "-").rstrip(":")
                self._respond({"ok": True, "sections": [{"id": section_id}]})
        elif path in ("/canvases.edit", "/conversations.canvases.create"):
            self._respond({"ok": True, "canvas_id": "F999"})
        else:
            self._respond({"ok": False, "error": "unknown_method"})

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    HTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
