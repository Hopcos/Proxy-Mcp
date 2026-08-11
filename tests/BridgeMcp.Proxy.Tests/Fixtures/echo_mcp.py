#!/usr/bin/env python3
"""Minimal MCP-like STDIO server for tests.

Reads newline-delimited JSON-RPC 2.0 messages from stdin and writes a
response (for requests) or echoes a notification. Used by the .NET test
suite to exercise StdioUpstreamTransport against a real child process.
"""
import sys, json, threading, time

def write(msg):
    sys.stdout.write(json.dumps(msg) + "\n")
    sys.stdout.flush()

def handle(msg):
    if "method" in msg and "id" in msg:
        method = msg["method"]
        if method == "initialize":
            write({
                "jsonrpc": "2.0", "id": msg["id"],
                "result": {
                    "protocolVersion": "2025-06-18",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "echo", "version": "1.0.0"},
                },
            })
        elif method == "tools/list":
            write({"jsonrpc": "2.0", "id": msg["id"],
                   "result": {"tools": [{"name": "echo", "description": "echoes args",
                                         "inputSchema": {"type": "object",
                                                         "properties": {"text": {"type": "string"}}}}]}})
        elif method == "tools/call":
            args = msg.get("params", {}).get("arguments", {})
            write({"jsonrpc": "2.0", "id": msg["id"],
                   "result": {"content": [{"type": "text", "text": args.get("text", "")}]}})
        else:
            write({"jsonrpc": "2.0", "id": msg["id"],
                   "result": {"echoed": method}})
    elif "method" in msg:
        # notification — no response
        pass

# Keep alive: read line by line. Using readline() rather than iterating
# sys.stdin directly because the latter buffers on Windows pipes and would
# not see lines written one at a time by the .NET host.
while True:
    line = sys.stdin.readline()
    if not line:
        break
    line = line.strip()
    if not line:
        continue
    try:
        handle(json.loads(line))
    except Exception as e:
        write({"jsonrpc": "2.0", "id": None,
               "error": {"code": -32700, "message": str(e)}})
