"""Regression: a delayed Add reply must not block inventory visibility."""
import json
import socket
import threading
import time
from types import SimpleNamespace

import bridge


def run():
    queued = threading.Event()
    model = bridge.Bridge.__new__(bridge.Bridge)
    model.catalog = {"Item": {"coat": (1, 0)}}
    model.lock = threading.Lock()
    model.next_serial = 1
    model.waiters = {}
    model.last_inventory_update = time.monotonic()
    model.inventory_visible = True
    model.inventory_state = 2
    model.observed_game_pid = None
    model.ensure_attached = lambda: None
    model.script = SimpleNamespace(exports_sync=SimpleNamespace(queue=lambda *args: queued.set()))
    running = threading.Event()
    running.set()
    bridge.process_alive = lambda _pid: running.is_set()
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    server = threading.Thread(target=bridge.serve, args=(model, 1, port), daemon=True)
    server.start()

    def request(message):
        with socket.create_connection(("127.0.0.1", port), timeout=2) as client:
            client.sendall(json.dumps(message).encode())
            return json.loads(client.makefile().readline())

    result = {}
    for _ in range(100):
        try:
            request({"op": "inventory"})
            break
        except ConnectionRefusedError:
            time.sleep(0.01)
    add = threading.Thread(target=lambda: result.update(request({"kind": "Item", "id": "coat"})))
    add.start()
    assert queued.wait(2), "Add did not enter its placement wait"
    try:
        start = time.monotonic()
        state = request({"op": "inventory"})
        assert state["visible"] is True, state
        assert time.monotonic() - start < 0.5, "Visibility waited for Add"
        assert add.is_alive(), "Test did not exercise a pending Add"
        model.on_message({"type": "send", "payload": {"serial": 1, "ok": True, "location": "inventory"}}, None)
        add.join(2)
        assert result.get("ok") is True, result
        print("PASS: inventory status responds while Add is pending; Add confirmation still completes")
    finally:
        if 1 in model.waiters:
            model.waiters[1]["event"].set()
        running.clear()
        server.join(2)


if __name__ == "__main__":
    run()
