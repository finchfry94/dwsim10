"""A stand-in for dwsim-bridge: answers echo/add/fail/slow, honours $/cancelRequest, and exits on 'exit'."""
import json, sys, threading, time

out_lock = threading.Lock()
cancelled = set()

def send(message):
    with out_lock:
        sys.stdout.write(json.dumps(message) + "\n")
        sys.stdout.flush()

def handle(req):
    rid, method, params = req.get("id"), req.get("method"), req.get("params") or {}
    if method == "$/cancelRequest":
        cancelled.add(params.get("id"))
        return
    if method == "echo":
        send({"jsonrpc": "2.0", "method": "test/echoed", "params": params})
        send({"jsonrpc": "2.0", "id": rid, "result": params})
    elif method == "add":
        send({"jsonrpc": "2.0", "id": rid, "result": params["a"] + params["b"]})
    elif method == "slow":
        for _ in range(100):
            if rid in cancelled:
                send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32800, "message": "Request cancelled"}})
                return
            time.sleep(0.02)
        send({"jsonrpc": "2.0", "id": rid, "result": "done"})
    elif method == "exit":
        sys.stdout.flush()
        import os; os._exit(0)
    else:
        send({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "Method not found: " + method}})

sys.stderr.write("fake bridge starting\n"); sys.stderr.flush()
send({"jsonrpc": "2.0", "method": "engine/ready", "params": {}})
for line in sys.stdin:
    if line.strip():
        threading.Thread(target=handle, args=(json.loads(line),), daemon=True).start()
