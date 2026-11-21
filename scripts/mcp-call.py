#!/usr/bin/env python3
"""Call SkyLink's MCP tools from WSL, the way Claude Desktop does, so a Claude Code session can run in-game tests itself.

Starts the deployed server (SkyrimMCP.dll, with Windows' dotnet), runs the MCP handshake over stdio, makes the calls in
order and prints each result. The server is a second pipe client next to Claude Desktop's (the plugin serves up to 4).

    scripts/mcp-call.py --list                                   # tool names
    scripts/mcp-call.py --manifest > tools.json                  # names, descriptions and input schemas
    scripts/mcp-call.py check_connection
    scripts/mcp-call.py find_hairstyles '{"sheet":true}' get_crash_log '{"index":0}'

Each tool name may be followed by a JSON object of arguments. Tools act on the game for real.
SKYLINK_MCP_LOG=<file> keeps the server's log; images in replies are saved to SKYLINK_MCP_IMAGES (default: the current
folder) and their paths printed.
"""
import base64
import json
import os
import subprocess
import sys

DOTNET = "/mnt/c/Program Files/dotnet/dotnet.exe"
SERVER = os.environ.get(
    "SKYLINK_SERVER_DLL",
    r"L:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data\SKSE\Plugins\SkyLinkAI_Server\SkyrimMCP.dll")


def main(argv):
    calls, i = [], 0
    while i < len(argv):
        name = argv[i]
        args = {}
        if i + 1 < len(argv) and argv[i + 1].lstrip().startswith("{"):
            args = json.loads(argv[i + 1])
            i += 1
        calls.append((name, args))
        i += 1
    if not calls:
        print(__doc__)
        return 2

    proc = subprocess.Popen([DOTNET, SERVER], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=open(os.environ.get("SKYLINK_MCP_LOG", os.devnull), "w"), cwd="/mnt/c",
                            text=True, encoding="utf-8", bufsize=1)
    next_id = 0

    def request(method, params):
        nonlocal next_id
        next_id += 1
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params}) + "\n")
        proc.stdin.flush()
        while True:
            line = proc.stdout.readline()
            if not line:
                raise SystemExit("server exited")
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == next_id:
                return msg

    try:
        request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                               "clientInfo": {"name": "skylink-wsl-test", "version": "1"}})
        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        proc.stdin.flush()
        for name, args in calls:
            if name == "--list":
                tools = request("tools/list", {}).get("result", {}).get("tools", [])
                print("\n".join(sorted(t["name"] for t in tools)))
                continue
            if name == "--manifest":
                # Every tool's name, description and input schema as JSON (for review in another AI client).
                tools = request("tools/list", {}).get("result", {}).get("tools", [])
                print(json.dumps(sorted(tools, key=lambda t: t["name"]), indent=1, ensure_ascii=False))
                continue
            reply = request("tools/call", {"name": name, "arguments": args})
            print(f"=== {name} {json.dumps(args) if args else ''}")
            if "error" in reply:
                print(json.dumps(reply["error"], indent=1))
                continue
            for part in reply.get("result", {}).get("content", []):
                text = part.get("text")
                if part.get("type") == "image" and part.get("data"):
                    folder = os.environ.get("SKYLINK_MCP_IMAGES", ".")
                    os.makedirs(folder, exist_ok=True)
                    ext = (part.get("mimeType") or "image/png").split("/")[-1]
                    path = os.path.join(folder, f"{name}-{next_id}.{ext}")
                    with open(path, "wb") as f:
                        f.write(base64.b64decode(part["data"]))
                    print(f"[image saved: {path}]")
                    continue
                if text is None:
                    print(f"[{part.get('type')} content]")
                    continue
                try:
                    print(json.dumps(json.loads(text), indent=1, ensure_ascii=False))
                except json.JSONDecodeError:
                    print(text)
    finally:
        proc.stdin.close()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
