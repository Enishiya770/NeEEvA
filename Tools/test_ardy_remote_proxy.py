"""Local contract checks for the ARDY authentication relay (no real network)."""
from __future__ import annotations

import json
import runpy
import socket
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

make_handler = runpy.run_path(str(Path(__file__).with_name("ardy_remote_proxy.py")))["make_handler"]


class Upstream(BaseHTTPRequestHandler):
    requests = []

    def do_GET(self):
        self.respond()

    def do_POST(self):
        self.respond()

    def respond(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self.requests.append((self.command, self.path, self.headers.get("Authorization"), body))
        status = 401 if self.path.endswith("/cancel") else 200
        data = json.dumps({"path": self.path, "size": len(body)}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *_):
        pass


class RelayTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.upstream = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        token = "local-test-bearer"
        cls.relay = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(
            f"http://127.0.0.1:{cls.upstream.server_port}", token))
        for server in (cls.upstream, cls.relay):
            threading.Thread(target=server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        for server in (cls.relay, cls.upstream):
            server.shutdown()
            server.server_close()

    def setUp(self):
        Upstream.requests.clear()

    def request(self, path, body=None):
        url = f"http://127.0.0.1:{self.relay.server_port}{path}"
        return urlopen(Request(url, data=body), timeout=3)

    def test_health_and_capabilities_forward_with_auth(self):
        with self.request("/health") as response:
            self.assertEqual(response.status, 200)
            self.assertEqual(json.load(response)["path"], "/health")
        with self.request("/v1/locomotion/capabilities") as response:
            self.assertEqual(response.status, 200)
        self.assertEqual([item[2] for item in Upstream.requests],
                         ["Bearer local-test-bearer", "Bearer local-test-bearer"])

    def test_generation_body_and_cancel_status(self):
        payload = b'{"description":"Raise both hands."}'
        with self.request("/v1/motion/generate", payload) as response:
            self.assertEqual(json.load(response)["size"], len(payload))
        self.assertEqual(Upstream.requests[0][3], payload)
        with self.assertRaises(HTTPError) as result:
            self.request("/v1/motion/cancel", b"{}")
        self.assertEqual(result.exception.code, 401)
        self.assertEqual(json.load(result.exception)["path"], "/v1/motion/cancel")

    def test_internal_status_and_unknown_routes_do_not_reach_upstream(self):
        with self.request("/_relay/health") as response:
            self.assertEqual(json.load(response)["service"], "neeeva-ardy-remote-relay")
        with self.assertRaises(HTTPError) as result:
            self.request("/other", b"{}")
        self.assertEqual(result.exception.code, 404)
        self.assertEqual(Upstream.requests, [])

    def test_unavailable_upstream_returns_bounded_error(self):
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            unused_port = probe.getsockname()[1]
        server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(
            f"http://127.0.0.1:{unused_port}", "local-test-bearer"))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            with self.assertRaises(HTTPError) as result:
                urlopen(f"http://127.0.0.1:{server.server_port}/health", timeout=3)
            self.assertEqual(result.exception.code, 502)
            self.assertEqual(json.load(result.exception)["error"]["code"], "upstream_unavailable")
        finally:
            server.shutdown()
            server.server_close()


if __name__ == "__main__":
    unittest.main()
