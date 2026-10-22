"""Tests for game_agent.py: python -m unittest (from this folder)."""

import http.server
import json
import os
import sys
import tempfile
import threading
import unittest

import game_agent as ga

TOKEN = "dga_" + "A" * 43


def config(actions=None):
    return {
        "apiBaseUrl": "http://localhost",
        "tokenFile": "token.txt",
        "actions": actions if actions is not None else {"valheim": {"argv": [sys.executable, "-c", "print('restarted')"]}},
    }


class ConfigTests(unittest.TestCase):
    def write(self, data):
        folder = tempfile.mkdtemp()
        path = os.path.join(folder, "config.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(data, f)
        return path

    def test_https_only_except_localhost(self):
        self.assertEqual("https://devbot.example.com", ga.check_base_url("https://devbot.example.com/"))
        self.assertEqual("http://localhost:5001", ga.check_base_url("http://localhost:5001"))
        for bad in ["http://devbot.example.com", "ftp://x", "https://user:pw@x.com", "https://x.com/?a=1", "", None]:
            with self.assertRaises(ga.ConfigError):
                ga.check_base_url(bad)

    def test_valid_config_resolves_the_token_file_next_to_it(self):
        path = self.write(config())
        loaded = ga.load_config(path)
        self.assertEqual(os.path.join(os.path.dirname(path), "token.txt"), loaded["tokenFile"])
        self.assertEqual(120, loaded["actions"]["valheim"]["timeoutSeconds"])

    def test_bad_actions_are_refused(self):
        for actions in [
            {},
            {"../x": {"argv": ["true"]}},
            {"valheim": {"argv": "docker restart valheim"}},
            {"valheim": {"argv": []}},
            {"valheim": {"argv": ["true"], "timeoutSeconds": 0}},
        ]:
            with self.assertRaises(ga.ConfigError):
                ga.load_config(self.write(config(actions)))

    def test_token_file_must_hold_a_token(self):
        folder = tempfile.mkdtemp()
        path = os.path.join(folder, "token.txt")
        with open(path, "w") as f:
            f.write(TOKEN + "\n")
        self.assertEqual(TOKEN, ga.read_token(path))
        with open(path, "w") as f:
            f.write("not a token")
        with self.assertRaises(ga.ConfigError):
            ga.read_token(path)


class OutputTests(unittest.TestCase):
    def test_secrets_are_redacted(self):
        text = ga.redact("token=abc123 password: hunter2 Bearer abcdefghijkl {} https://u:p@host/x {}".format(TOKEN, "x" * 50))
        for secret in ["abc123", "hunter2", "abcdefghijkl", TOKEN, "u:p@", "x" * 50]:
            self.assertNotIn(secret, text)
        self.assertEqual("ok [redacted]", ga.redact("ok mine", extra=("mine",)))

    def test_output_keeps_the_tail(self):
        out = ga.tail("a" * 5000 + "END")
        self.assertEqual(ga.MAX_OUTPUT, len(out))
        self.assertTrue(out.endswith("END"))


class RunActionTests(unittest.TestCase):
    def test_unknown_actions_are_refused(self):
        ok, code, output = ga.run_action("rm -rf /", ga.load_config_dict(config())["actions"])
        self.assertFalse(ok)
        self.assertIsNone(code)
        self.assertIn("Unknown action", output)

    def test_runs_argv_without_a_shell_and_reports_exit_code(self):
        actions = ga.load_config_dict(config({
            "ok": {"argv": [sys.executable, "-c", "print('hi; echo injected')"]},
            "bad": {"argv": [sys.executable, "-c", "import sys; print('password=hunter2'); sys.exit(3)"]},
        }))["actions"]
        self.assertEqual((True, 0, "hi; echo injected"), ga.run_action("ok", actions))
        ok, code, output = ga.run_action("bad", actions)
        self.assertEqual((False, 3), (ok, code))
        self.assertNotIn("hunter2", output)

    def test_timeouts_fail(self):
        actions = ga.load_config_dict(config({"slow": {"argv": [sys.executable, "-c", "import time; time.sleep(5)"], "timeoutSeconds": 1}}))["actions"]
        ok, code, output = ga.run_action("slow", actions)
        self.assertFalse(ok)
        self.assertIn("Timed out", output)


class FakeApi(http.server.BaseHTTPRequestHandler):
    """Hands out one command, takes its result, then answers 401 as if the token were revoked."""
    requests = []
    commands = []

    def log_message(self, *args):
        pass

    def reply(self, status, body=None):
        data = json.dumps(body).encode() if body is not None else b""
        self.send_response(status)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def handle_any(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = json.loads(self.rfile.read(length)) if length else None
        FakeApi.requests.append((self.command, self.path, self.headers.get("Authorization"), body))
        if self.headers.get("Authorization") != "Bearer " + TOKEN:
            return self.reply(401)
        if self.path == "/api/agent/hello":
            return self.reply(200, {"id": 1, "name": "home"})
        if self.path == "/api/agent/commands/next":
            return self.reply(200, FakeApi.commands.pop(0)) if FakeApi.commands else self.reply(401)
        if self.path.startswith("/api/agent/commands/"):
            return self.reply(200, {"id": 1})
        self.reply(404)

    do_GET = handle_any
    do_POST = handle_any


class EndToEndTests(unittest.TestCase):
    def test_hello_poll_run_report_then_stop_on_401(self):
        FakeApi.requests = []
        FakeApi.commands = [{"id": 7, "action": "valheim"}, {"id": 8, "action": "not-configured"}]
        server = http.server.HTTPServer(("127.0.0.1", 0), FakeApi)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            cfg = ga.load_config_dict(config())
            client = ga.Client("http://127.0.0.1:{}".format(server.server_address[1]), TOKEN)
            exit_code = ga.Agent(cfg, client, sleep=lambda _: None).run()
        finally:
            server.shutdown()
            server.server_close()

        self.assertEqual(2, exit_code)
        paths = [(m, p) for m, p, _, _ in FakeApi.requests]
        self.assertEqual(("POST", "/api/agent/hello"), paths[0])
        self.assertEqual(["valheim"], FakeApi.requests[0][3]["actions"])
        results = {p: b for m, p, _, b in FakeApi.requests if p.endswith("/result")}
        self.assertEqual({"ok": True, "exitCode": 0, "output": "restarted"}, results["/api/agent/commands/7/result"])
        self.assertFalse(results["/api/agent/commands/8/result"]["ok"])
        self.assertTrue(all(auth == "Bearer " + TOKEN for _, _, auth, _ in FakeApi.requests))


if __name__ == "__main__":
    unittest.main()
