"""Loopback-only relay for the authenticated remote ARDY service."""
from __future__ import annotations

import argparse
import http.client
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


GET_PATHS = frozenset({"/health", "/v1/locomotion/capabilities"})
POST_PATHS = frozenset({
    "/v1/motion/generate", "/v1/motion/cancel",
    "/v1/locomotion/generate", "/v1/locomotion/cancel",
    "/v1/locomotion/stream/step", "/v1/locomotion/stream/cancel",
})
MAX_REQUEST_BYTES = 128 * 1024
MAX_RESPONSE_BYTES = 8 * 1024 * 1024


def make_handler(upstream: str, token: str):
    parsed = urlsplit(upstream)
    if parsed.scheme != "http" or not parsed.hostname or parsed.path not in ("", "/"):
        raise ValueError("ARDY upstream must be an HTTP origin without a path")
    if parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise ValueError("ARDY upstream must not contain credentials or a query")
    if not token or any(c in token for c in "\r\n"):
        raise ValueError("Missing or invalid ARDY bearer token")

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format, *args):
            # Request paths and response codes are useful; the token is never logged.
            super().log_message(format, *args)

        def do_GET(self):
            if self.path == "/_relay/health":
                self.respond(200, json.dumps({"service": "neeeva-ardy-remote-relay", "upstream": upstream}).encode())
            elif self.path in GET_PATHS:
                self.forward()
            else:
                self.respond(404, b'{"error":{"code":"not_found"}}')

        def do_POST(self):
            if self.path in POST_PATHS:
                self.forward()
            else:
                self.respond(404, b'{"error":{"code":"not_found"}}')

        def forward(self):
            size = self.headers.get("Content-Length", "0")
            if not size.isdecimal() or int(size) > MAX_REQUEST_BYTES:
                self.respond(413, b'{"error":{"code":"body_too_large"}}')
                return
            body = self.rfile.read(int(size)) if self.command == "POST" else None
            connection = http.client.HTTPConnection(parsed.hostname, parsed.port or 80, timeout=35)
            try:
                headers = {"Authorization": "Bearer " + token, "Accept": "application/json"}
                if body is not None:
                    headers["Content-Type"] = "application/json"
                connection.request(self.command, self.path, body=body, headers=headers)
                upstream_response = connection.getresponse()
                data = upstream_response.read(MAX_RESPONSE_BYTES + 1)
                if len(data) > MAX_RESPONSE_BYTES:
                    raise ValueError("ARDY response exceeded the 8 MiB limit")
                self.respond(upstream_response.status, data,
                             upstream_response.getheader("Content-Type", "application/json"))
            except (OSError, http.client.HTTPException, ValueError) as error:
                self.log_error("upstream unavailable: %s", type(error).__name__)
                self.respond(502, b'{"error":{"code":"upstream_unavailable"}}')
            finally:
                connection.close()

        def respond(self, status: int, data: bytes, content_type: str = "application/json"):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Connection", "close")
            self.end_headers()
            try:
                self.wfile.write(data)
            except (BrokenPipeError, ConnectionResetError):
                pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--port", type=int, default=8093)
    args = parser.parse_args()
    handler = make_handler(args.upstream, os.environ.get("NEEEVA_ARDY_BEARER_TOKEN", ""))
    server = ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    server.daemon_threads = True
    print(f"ARDY relay listening on 127.0.0.1:{args.port}", flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
