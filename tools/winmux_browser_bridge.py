#!/usr/bin/env python3
import argparse
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request


def resolve_token() -> str:
    return (
        os.environ.get("WINMUX_AUTOMATION_TOKEN", "").strip()
        or os.environ.get("NATIVE_TERMINAL_AUTOMATION_TOKEN", "").strip()
    )


def resolve_url(env_name: str, path: str) -> str:
    value = os.environ.get(env_name, "").strip()
    if value:
        return value

    automation_port = os.environ.get("WINMUX_AUTOMATION_PORT", "").strip() or "9331"
    return f"http://127.0.0.1:{automation_port}{path}"


def post_json(url: str, payload: dict) -> dict:
    headers = {"Content-Type": "application/json"}
    token = resolve_token()
    if token:
        headers["X-WinMux-Automation-Token"] = token

    request = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers=headers,
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))


def post_json_via_powershell(path: str, payload: dict) -> dict:
    automation_port = os.environ.get("WINMUX_AUTOMATION_PORT", "").strip() or "9331"
    uri = f"http://127.0.0.1:{automation_port}{path}"
    token = resolve_token()
    payload_text = json.dumps(payload)
    command = (
        "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; "
        "$OutputEncoding = [System.Text.Encoding]::UTF8; "
        "$json = @'\n"
        f"{payload_text}\n"
        "'@; "
        f"$headers = @{{ 'X-WinMux-Automation-Token' = '{token}' }}; "
        f"Invoke-RestMethod -Method Post -Uri '{uri}' -Headers $headers -ContentType 'application/json' -Body $json | ConvertTo-Json -Depth 20"
    )

    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command", command],
        check=True,
        capture_output=True,
        encoding="utf-8",
        errors="replace",
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

    screenshot_parser = subparsers.add_parser("screenshot", help="Capture a screenshot from the selected WinMux browser pane.")
    screenshot_parser.add_argument("--pane-id", default="", help="Optional pane id to capture.")
    screenshot_parser.add_argument("--path", default="", help="Optional output path to request from WinMux.")

    args = parser.parse_args()

    try:
        if args.command == "state":
            payload = {"paneId": args.pane_id}
            path = "/browser-state"
        elif args.command == "eval":
            payload = {"paneId": args.pane_id, "script": args.script}
            path = "/browser-eval"
        else:
            payload = {"paneId": args.pane_id, "path": args.path}
            path = "/browser-screenshot"
        url = resolve_url(
            {
                "/browser-state": "WINMUX_BROWSER_STATE_URL",
                "/browser-eval": "WINMUX_BROWSER_EVAL_URL",
                "/browser-screenshot": "WINMUX_BROWSER_SCREENSHOT_URL",
            }[path],
            path,
        )
        try:
            result = post_json_via_powershell(path, payload)
        except (FileNotFoundError, subprocess.CalledProcessError, json.JSONDecodeError, UnicodeDecodeError):
            result = post_json(url, payload)
    except (urllib.error.URLError, TimeoutError, subprocess.CalledProcessError) as exc:
        print(json.dumps({"ok": False, "error": str(exc)}))
        return 1

    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
