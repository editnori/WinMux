#!/usr/bin/env python3
import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request


def require_url(env_name: str) -> str:
    value = os.environ.get(env_name, "").strip()
    if not value:
        raise SystemExit(f"Missing required environment variable: {env_name}")
    return value


def post_json(url: str, payload: dict) -> dict:
    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))


def post_json_via_powershell(path: str, payload: dict) -> dict:
    automation_port = os.environ.get("WINMUX_AUTOMATION_PORT", "").strip() or "9331"
    uri = f"http://127.0.0.1:{automation_port}{path}"
    payload_text = json.dumps(payload)
    command = (
        "$json = @'\n"
        f"{payload_text}\n"
        "'@; "
        f"Invoke-RestMethod -Method Post -Uri '{uri}' -ContentType 'application/json' -Body $json | ConvertTo-Json -Depth 20"
    )

    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command", command],
        check=True,
        capture_output=True,
        text=True,
    )
    return json.loads(result.stdout)


def main() -> int:
    parser = argparse.ArgumentParser(description="Bridge to the WinMux browser automation endpoints from WSL.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    state_parser = subparsers.add_parser("state", help="Read the current WinMux browser-pane state.")
    state_parser.add_argument("--pane-id", default="", help="Optional pane id to scope the browser state query.")

    eval_parser = subparsers.add_parser("eval", help="Evaluate JavaScript inside the selected WinMux browser pane.")
    eval_parser.add_argument("script", help="JavaScript expression or statement to evaluate.")
    eval_parser.add_argument("--pane-id", default="", help="Optional pane id to evaluate against.")

    args = parser.parse_args()

    try:
        if args.command == "state":
            url = require_url("WINMUX_BROWSER_STATE_URL")
            payload = {"paneId": args.pane_id}
            path = "/browser-state"
        else:
            url = require_url("WINMUX_BROWSER_EVAL_URL")
            payload = {"paneId": args.pane_id, "script": args.script}
            path = "/browser-eval"
        try:
            result = post_json_via_powershell(path, payload)
        except (FileNotFoundError, subprocess.CalledProcessError, json.JSONDecodeError):
            result = post_json(url, payload)
    except (urllib.error.URLError, TimeoutError, subprocess.CalledProcessError) as exc:
        print(json.dumps({"ok": False, "error": str(exc)}))
        return 1

    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
