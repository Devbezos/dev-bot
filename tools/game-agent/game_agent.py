#!/usr/bin/env python3
"""Dev Bot home game agent.

Holds an outbound HTTPS long-poll to dev-api and runs only the actions named in its own config. The server only ever
picks an action name; nothing it sends is passed to a command. Standard library only, Python 3.8+.

    python3 game_agent.py --config config.json
"""

import argparse
import json
import logging
import os
import random
import re
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

VERSION = "1.0.0"
ACTION_NAME = re.compile(r"\A[A-Za-z0-9][A-Za-z0-9 _-]{0,63}\Z")
MAX_OUTPUT = 1000
POLL_TIMEOUT = 40  # The server holds a poll for up to 25 seconds.
REQUEST_TIMEOUT = 20
MIN_BACKOFF = 1.0
MAX_BACKOFF = 60.0

log = logging.getLogger("game-agent")


class ConfigError(Exception):
    """The config, token file or apiBaseUrl is unusable."""


class Unauthorized(Exception):
    """The server refused the token (401): it's wrong or was revoked."""


class Stop(Exception):
    """Ctrl+C or SIGTERM."""


class ServerError(Exception):
    """The server answered with an unexpected status; the message is just that status."""


# --- Config -----------------------------------------------------------------------------------------------------

def check_base_url(url):
    """The API's address: https only, except http://localhost for testing. Returns it without a trailing slash."""
    if not isinstance(url, str) or not url:
        raise ConfigError("apiBaseUrl is missing.")
    parts = urllib.parse.urlsplit(url)
    local = parts.hostname in ("localhost", "127.0.0.1", "::1")
    if parts.scheme != "https" and not (parts.scheme == "http" and local):
        raise ConfigError("apiBaseUrl must start with https:// (plain http is only allowed for localhost).")
    if not parts.hostname or parts.username or parts.password or parts.query or parts.fragment:
        raise ConfigError("apiBaseUrl must be a plain address like https://devbot.example.com, with no user, query or fragment.")
    return url.rstrip("/")


def load_config(path):
    """Reads and checks the config. Each action is an argv list run without a shell."""
    try:
        with open(path, "r", encoding="utf-8") as f:
            raw = json.load(f)
    except (OSError, ValueError) as e:
        raise ConfigError("Can't read config {}: {}".format(path, e))
    return load_config_dict(raw, os.path.dirname(os.path.abspath(path)))


def load_config_dict(raw, base_dir="."):
    """Checks a parsed config; a relative tokenFile is next to the config."""
    if not isinstance(raw, dict):
        raise ConfigError("The config must be a JSON object.")

    base_url = check_base_url(raw.get("apiBaseUrl"))
    token_file = raw.get("tokenFile")
    if not isinstance(token_file, str) or not token_file:
        raise ConfigError("tokenFile is missing.")
    if not os.path.isabs(token_file):
        token_file = os.path.join(base_dir, token_file)

    actions = raw.get("actions")
    if not isinstance(actions, dict) or not actions:
        raise ConfigError("actions must be an object with at least one action.")
    if len(actions) > 50:
        raise ConfigError("An agent can offer at most 50 actions.")
    checked = {}
    for name, spec in actions.items():
        if not ACTION_NAME.match(name):
            raise ConfigError("Action name {!r} must be 1-64 letters, numbers, spaces, underscores or hyphens.".format(name))
        if not isinstance(spec, dict):
            raise ConfigError("Action {!r} must be an object with argv.".format(name))
        argv = spec.get("argv")
        if not isinstance(argv, list) or not argv or not all(isinstance(a, str) and a for a in argv):
            raise ConfigError("Action {!r} needs argv: a non-empty list of strings.".format(name))
        timeout = spec.get("timeoutSeconds", 120)
        if not isinstance(timeout, int) or isinstance(timeout, bool) or not 1 <= timeout <= 3600:
            raise ConfigError("Action {!r} timeoutSeconds must be a whole number from 1 to 3600.".format(name))
        cwd = spec.get("cwd")
        if cwd is not None and not isinstance(cwd, str):
            raise ConfigError("Action {!r} cwd must be a path.".format(name))
        checked[name] = {"argv": list(argv), "timeoutSeconds": timeout, "cwd": cwd}
    return {"apiBaseUrl": base_url, "tokenFile": token_file, "actions": checked}


def read_token(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            token = f.read().strip()
    except OSError as e:
        raise ConfigError("Can't read the token file {}: {}".format(path, e.strerror or e))
    if not token.startswith("dga_") or any(c.isspace() for c in token):
        raise ConfigError("The token file must hold just the dga_... token from the Game servers page.")
    return token


# --- Output -----------------------------------------------------------------------------------------------------

REDACTIONS = [
    (re.compile(r"dga_[A-Za-z0-9_-]+"), "dga_[redacted]"),
    (re.compile(r"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{8,}"), r"\1 [redacted]"),
    (re.compile(r"(?i)\b([\w.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)[\w.-]*)(\s*[:=]\s*)(\"[^\"]*\"|'[^']*'|\S+)"),
     r"\1\2[redacted]"),
    (re.compile(r"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@"), r"\1[redacted]@"),
    (re.compile(r"\b[A-Za-z0-9+/_-]{40,}={0,2}"), "[redacted]"),
]


def redact(text, extra=()):
    """Blanks out what looks like a secret: tokens, passwords in key=value form, URL credentials, long random strings."""
    for secret in extra:
        if secret:
            text = text.replace(secret, "[redacted]")
    for pattern, replacement in REDACTIONS:
        text = pattern.sub(replacement, text)
    return text


def tail(text, limit=MAX_OUTPUT):
    text = text.strip()
    return text if len(text) <= limit else "..." + text[-(limit - 3):]


def run_action(name, actions, secrets=()):
    """Runs a configured action by name and returns (ok, exit_code, output). Unknown names are refused."""
    spec = actions.get(name)
    if spec is None:
        return False, None, "Unknown action {!r}: this agent doesn't offer it.".format(name)
    try:
        proc = subprocess.run(spec["argv"], shell=False, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=spec["timeoutSeconds"], cwd=spec.get("cwd"))
    except subprocess.TimeoutExpired as e:
        output = e.output.decode("utf-8", "replace") if e.output else ""
        return False, None, tail(redact(output + "\nTimed out after {} seconds.".format(spec["timeoutSeconds"]), secrets))
    except OSError as e:
        return False, None, "Couldn't start {}: {}".format(os.path.basename(spec["argv"][0]), e.strerror or type(e).__name__)
    output = proc.stdout.decode("utf-8", "replace") if proc.stdout else ""
    return proc.returncode == 0, proc.returncode, tail(redact(output, secrets)) or "(no output)"


# --- HTTP -------------------------------------------------------------------------------------------------------

class _NoRedirects(urllib.request.HTTPRedirectHandler):
    # A redirect could carry the token somewhere else; the API never sends one.
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, "Refusing to follow a redirect", headers, fp)


class Client:
    def __init__(self, base_url, token):
        self.base_url = base_url
        self._token = token
        # Default HTTPS handler: system CA certificates and hostname checks.
        self._opener = urllib.request.build_opener(_NoRedirects())

    def _send(self, method, path, body=None, timeout=REQUEST_TIMEOUT):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = urllib.request.Request(self.base_url + path, data=data, method=method)
        request.add_header("Authorization", "Bearer " + self._token)
        request.add_header("User-Agent", "dev-bot-game-agent/" + VERSION)
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with self._opener.open(request, timeout=timeout) as response:
                raw = response.read(65536)
                return response.status, (json.loads(raw.decode("utf-8")) if raw else None)
        except urllib.error.HTTPError as e:
            if e.code == 401:
                raise Unauthorized()
            return e.code, None

    def hello(self, actions):
        return self._send("POST", "/api/agent/hello", {"actions": actions, "version": VERSION})

    def next_command(self):
        return self._send("GET", "/api/agent/commands/next", timeout=POLL_TIMEOUT)

    def report(self, command_id, ok, exit_code, output):
        return self._send("POST", "/api/agent/commands/{}/result".format(int(command_id)),
                          {"ok": ok, "exitCode": exit_code, "output": output})


# --- Main loop --------------------------------------------------------------------------------------------------

class Agent:
    def __init__(self, config, client, sleep=time.sleep):
        self.actions = config["actions"]
        self.client = client
        self.sleep = sleep
        self.stopping = False
        self.busy = False
        self._failures = 0
        self._secrets = (client._token,)

    def backoff(self):
        delay = min(MAX_BACKOFF, MIN_BACKOFF * (2 ** self._failures))
        self._failures = min(self._failures + 1, 10)
        delay *= random.uniform(0.5, 1.0)
        log.info("Retrying in %.1fs", delay)
        self.sleep(delay)

    def handle(self, command):
        command_id, action = command.get("id"), command.get("action")
        if not isinstance(command_id, int) or not isinstance(action, str):
            log.warning("Ignoring a malformed command from the server")
            return
        log.info("Command %s: running %r", command_id, action)
        self.busy = True
        try:
            ok, exit_code, output = run_action(action, self.actions, self._secrets)
        finally:
            self.busy = False
        log.info("Command %s: %s (exit code %s)", command_id, "succeeded" if ok else "failed", exit_code)
        for attempt in range(3):
            try:
                status, _ = self.client.report(command_id, ok, exit_code, output)
                if status == 200:
                    return
                log.warning("Command %s: the server answered %s to the result", command_id, status)
                if status in (400, 404):
                    return
            except (urllib.error.URLError, OSError, ValueError) as e:
                log.warning("Command %s: couldn't report the result (%s)", command_id, type(e).__name__)
            self.sleep(2 * (attempt + 1))

    def run(self):
        """Hello, then long-poll / run / report until stopped. Returns the process exit code."""
        names = sorted(self.actions)
        need_hello = True
        while not self.stopping:
            try:
                if need_hello:
                    status, _ = self.client.hello(names)
                    if status != 200:
                        raise ServerError("hello answered {}".format(status))
                    log.info("Connected to %s offering %s", self.client.base_url, ", ".join(names))
                    need_hello = False
                    self._failures = 0
                status, body = self.client.next_command()
                if status == 204:
                    self._failures = 0
                    continue
                if status == 200 and isinstance(body, dict):
                    self._failures = 0
                    self.handle(body)
                    continue
                raise ServerError("poll answered {}".format(status))
            except Unauthorized:
                log.error("The server refused this agent's token (401). It's wrong or was revoked: create a new one on "
                          "the dashboard's Game servers page and put it in the token file.")
                return 2
            except Stop:
                break
            except (ServerError, urllib.error.URLError, OSError, ValueError) as e:
                # Only the error's type for network errors: their messages can include addresses or headers.
                log.warning("Connection problem: %s", e if isinstance(e, ServerError) else type(e).__name__)
                need_hello = True
                self.backoff()
        log.info("Stopped")
        return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="Dev Bot home game agent")
    parser.add_argument("--config", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json"))
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    try:
        config = load_config(args.config)
        token = read_token(config["tokenFile"])
    except ConfigError as e:
        log.error("%s", e)
        return 1

    agent = Agent(config, Client(config["apiBaseUrl"], token))

    def on_signal(signum, _frame):
        agent.stopping = True
        log.info("Stopping (signal %s)%s", signum, "; finishing the running action first" if agent.busy else "")
        if not agent.busy:
            raise Stop()

    for name in ("SIGINT", "SIGTERM", "SIGBREAK"):
        if hasattr(signal, name):
            signal.signal(getattr(signal, name), on_signal)
    try:
        return agent.run()
    except Stop:
        return 0


if __name__ == "__main__":
    sys.exit(main())
