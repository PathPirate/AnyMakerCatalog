"""Live AnyMaker catalog bridge. Calls the game's own Give Item event on its UI thread."""

import ctypes
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
import pathlib
import re
import socket
import struct
import sys
import threading
import time

import frida


GAME_EXE = "game.exe"
PORT = 48273
SUPPORTED_GCL_SHA256 = {
    "8083011d6af3fb07966034f9cdf7efa2ec7b24e1df9b83a81c7b721d51745370",
    "32c1389e6bbc49aeae5a7eff93f2f580fed78ec453a69e6a491c014253e4ae09",
    "d9725681f1b0bff321c58fcadc0d2e12104919a541eef074cf12945b283edb75",
    "8b877a19fea261e0524a4cea39beb89a200796dfc74229a2bd13f884d2d01d4a",
}

DIRECT_STORAGE_SUPPORTED_GCL = {
    "32c1389e6bbc49aeae5a7eff93f2f580fed78ec453a69e6a491c014253e4ae09",
    "d9725681f1b0bff321c58fcadc0d2e12104919a541eef074cf12945b283edb75",
    "8b877a19fea261e0524a4cea39beb89a200796dfc74229a2bd13f884d2d01d4a",
}

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.OpenProcess.argtypes = (ctypes.c_uint32, ctypes.c_int, ctypes.c_uint32)
kernel.OpenProcess.restype = ctypes.c_void_p
kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
kernel.GetExitCodeProcess.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))
kernel.GetExitCodeProcess.restype = ctypes.c_int
kernel.ReadProcessMemory.argtypes = (ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t))
kernel.ReadProcessMemory.restype = ctypes.c_int
kernel.VirtualQueryEx.argtypes = (ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t)
kernel.VirtualQueryEx.restype = ctypes.c_size_t


class MemoryInfo(ctypes.Structure):
    _fields_ = [
        ("base", ctypes.c_void_p),
        ("allocation_base", ctypes.c_void_p),
        ("allocation_protect", ctypes.c_uint32),
        ("partition_id", ctypes.c_uint16),
        ("size", ctypes.c_size_t),
        ("state", ctypes.c_uint32),
        ("protect", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
    ]


def read_memory(handle, address, count):
    result = ctypes.create_string_buffer(count)
    read = ctypes.c_size_t()
    if kernel.ReadProcessMemory(handle, address, result, count, ctypes.byref(read)) and read.value == count:
        return result.raw
    return None


def regions(handle, protection=None):
    address = 0x10000
    while address < 0x7FFF00000000:
        info = MemoryInfo()
        if not kernel.VirtualQueryEx(handle, address, ctypes.byref(info), ctypes.sizeof(info)):
            break
        start = int(info.base or 0)
        size = int(info.size)
        address = start + size
        if not size or size > 512 * 1024 * 1024 or info.state != 0x1000 or info.protect & 0x101:
            continue
        if protection is not None and info.protect not in protection:
            continue
        yield start, size


def chunks(handle, protection=None, address_min=0, address_max=0x7FFF00000000):
    size_limit = 4 * 1024 * 1024
    overlap = 256
    for start, size in regions(handle, protection):
        if start + size <= address_min or start >= address_max:
            continue
        offset = max(0, address_min - start)
        limit = min(size, address_max - start)
        while offset < limit:
            count = min(size_limit, limit - offset)
            data = read_memory(handle, start + offset, count)
            if data is not None:
                yield start + offset, data
            offset += size_limit - overlap


SYMBOLS = {
    "give": (b"client_peer.data.push_event_inventory_debug_give_item", bytes.fromhex("535556574881EC98000000")),
    "update": (b"client_scene.update_ui", bytes.fromhex("53554883EC68")),
    "inventory": (b"frontend_ui_inventory.update_ui", bytes.fromhex("5355565741544155415641574881ECC8")),
    "pickup": (b"server_scene.inventory.pick_up_item", bytes.fromhex("5355565741544155415641574881ECC8")),
    "store": (b"server_scene.inventory.try_store_item", bytes.fromhex("53555657415441554881EC38")),
}

# Resolve every storage routine independently: runtime patches can move native
# functions even when the game.gcl file itself has not changed.
STORAGE_SYMBOLS = {
    "lookup": (b"server_scene.inventory.get_item_by_id_recursive", bytes.fromhex("5355565741544881ECA0")),
    "items_ctor": (b"() ctor (vector<ptr<server_scene.item_world>>)", bytes.fromhex("4883EC0848890C24")),
    "get_items": (b"server_scene.inventory.get_items", bytes.fromhex("534883EC4048894C24")),
    "get_type": (b"server_scene.item_world.get_virtual_type_id", bytes.fromhex("4883EC1848890C24")),
    "grid_store": (b"server_scene.item_world_ref.try_store_item_in_grid_type", bytes.fromhex("535556574881ECD801")),
    "actual_store": (b"server_scene.item_world_ref.store_item", bytes.fromhex("535556574881ECE800")),
    "merge": (b"server_scene.item_world_ref.merge_item", bytes.fromhex("534883EC4048894C24")),
    "floor_create": (b"server_scene.item_floor_container.create_item", bytes.fromhex("535556574881EC88000000")),
    "storage_handler": (b"() server.on_event (server, server_peer.data, client_peer.data.event.inventory_debug_give_item)",
                        bytes.fromhex("5355565741544881EC50050000")),
}


def resolve(pid, direct_storage=False):
    symbols = {key: value for key, value in SYMBOLS.items()
               if key in ("give", "update", "inventory") or
               (direct_storage and key in ("pickup", "store"))}
    if direct_storage:
        symbols.update(STORAGE_SYMBOLS)
    handle = kernel.OpenProcess(0x0400 | 0x0010, False, pid)
    if not handle:
        raise RuntimeError("Could not read the game process")
    try:
        hits = {key: [] for key in symbols}
        names = {needle: key for key, (needle, _) in symbols.items()}
        name_pattern = re.compile(b"|".join(re.escape(needle) for needle in names))
        for start, data in chunks(handle, {4}):
            for match in name_pattern.finditer(data):
                needle = match.group()
                key = names[needle]
                after = match.end()
                if after == len(data) or not (65 <= data[after] <= 90 or 97 <= data[after] <= 122 or 48 <= data[after] <= 57 or data[after] == 95):
                    hits[key].append(start + match.start())
        if any(not locations for locations in hits.values()):
            raise RuntimeError("Could not locate this game's inventory UI symbols")
        if os.environ.get("ANYMAKER_DEBUG_RESOLVE"):
            print("Resolver markers:", {key: [hex(x) for x in value] for key, value in hits.items()}, flush=True)
        targets = {}
        for key, locations in hits.items():
            for marker in locations:
                # Reflected strings have a self pointer immediately before
                # their text. Ignore signature substrings and search the
                # actual string data pointer, rather than several guesses.
                header = read_memory(handle, marker - 8, 8)
                if header == struct.pack("<Q", marker - 8):
                    targets[struct.pack("<Q", marker)] = key
        resolved = {}
        pointer_refs = re.compile(b"|".join(re.escape(pattern) for pattern in targets))
        addresses = [address for locations in hits.values() for address in locations]
        address_min = max(0, min(addresses) - 512 * 1024 * 1024) if addresses else 0
        address_max = max(addresses) + 512 * 1024 * 1024 if addresses else 0x7FFF00000000
        for start, data in chunks(handle, {2, 4, 8, 0x40}, address_min, address_max):
            for match in pointer_refs.finditer(data):
                key = targets[match.group()]
                if key in resolved:
                    continue
                ref = start + match.start()
                for code_offset in (-32, 24, 40):
                    ptr_data = read_memory(handle, ref + code_offset, 8)
                    if not ptr_data:
                        continue
                    code = struct.unpack("<Q", ptr_data)[0]
                    if read_memory(handle, code, len(symbols[key][1])) != symbols[key][1]:
                        continue
                    if key == "storage_handler":
                        size_raw = read_memory(handle, ref + code_offset + (16 if code_offset > 0 else 8), 8)
                        size = struct.unpack("<Q", size_raw)[0] >> 32 if size_raw else 0
                        if not 16 <= size <= 131072:
                            continue
                        resolved["storage_handler_end"] = code + size
                    resolved[key] = code
                    break
            if all(key in resolved for key in symbols):
                break
        if any(key not in resolved for key in symbols):
            missing = ", ".join(sorted(set(symbols) - set(resolved)))
            if os.environ.get("ANYMAKER_DEBUG_RESOLVE"):
                print("Resolver matched:", {key: hex(value) for key, value in resolved.items()}, flush=True)
            raise RuntimeError(f"Could not locate game routines: {missing}")
        return resolved
    finally:
        kernel.CloseHandle(handle)


def load_catalog(game_path):
    root = pathlib.Path(game_path) / "rom" / "data"
    def definitions(filename):
        with (root / filename).open(encoding="utf-8") as stream:
            return json.load(stream)["definitions"]
    items = definitions("inventory_definitions.json")
    components = definitions("vehicle_component_definitions.json")
    tool_index = next(i + 1 for i, item in enumerate(items) if item.get("id") == "vehicle_editor_add_component")
    building_tool_index = next(i + 1 for i, item in enumerate(items)
                               if item.get("id") == "vehicle_editor_add_building_component")
    catalog = {
        "Item": {item["id"]: (i + 1, 0) for i, item in enumerate(items)},
        "Component": {item["id"]: (building_tool_index if item.get("is_building_component") else tool_index, i + 1)
                      for i, item in enumerate(components)},
    }
    # The runtime type ID is the one-based index of the item definition.
    # A nonempty storage grid marks carried containers, including pouches.
    storage_types = [i + 1 for i, item in enumerate(items)
                     if any(grid.get("size") for grid in item.get("inventory", {}).get("grids", []))]
    return catalog, storage_types


def game_pid(game_path):
    expected = os.path.normcase(os.path.abspath(os.path.join(game_path, GAME_EXE)))
    for process in frida.get_local_device().enumerate_processes():
        if process.name.lower() != GAME_EXE:
            continue
        # The bridge is launched for one configured installation. The game's
        # GCL hash check below also prevents attaching to a different version.
        return process.pid
    return None


def process_alive(pid):
    handle = kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        exit_code = ctypes.c_uint32()
        return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(exit_code))) and exit_code.value == 259
    finally:
        kernel.CloseHandle(handle)


SCRIPT = r'''
const give = new NativeFunction(ptr('__GIVE__'), 'void',
  ['pointer', 'pointer', 'pointer', 'pointer', 'pointer']);
const pending = [];
const awaitingPlacement = [];
let lastInventoryTick = 0;
let lastInventoryState = -1;
let inventoryStateErrorSent = false;
if (__STORAGE_ENABLED__) {
  const pickup = ptr('__PICKUP__');
  const store = new NativeFunction(ptr('__STORE__'), 'void', Array(7).fill('pointer'));
  const originalPickup = new NativeFunction(pickup, 'void', Array(10).fill('pointer'));
  const lookup = new NativeFunction(ptr('__LOOKUP__'), 'void', Array(5).fill('pointer'));
  const itemsCtor = new NativeFunction(ptr('__ITEMS_CTOR__'), 'void', ['pointer']);
  const getItems = new NativeFunction(ptr('__GET_ITEMS__'), 'void', ['pointer', 'pointer']);
  const getType = new NativeFunction(ptr('__GET_TYPE__'), 'void', ['pointer', 'pointer']);
  const gridStore = new NativeFunction(ptr('__GRID_STORE__'), 'void', Array(4).fill('pointer'));
  const storageTypes = new Set(__STORAGE_TYPES__);
  const handlerStart = ptr('__STORAGE_HANDLER__');
  const handlerEnd = ptr('__STORAGE_HANDLER_END__');
  const anyGrid = Memory.alloc(4);
  anyGrid.writeS32(-1);
  const itemVector = Memory.alloc(128);
  let vectorReady = false;
  let active = null;
  let inExtraStore = false;
  function itemId(ref) {
    try { return ref.add(0x20).readPointer().add(88).readS32(); }
    catch (_) { return -1; }
  }
  function findItem(inv, idValue) {
    if (idValue < 0) return NULL;
    const id = Memory.alloc(4), out = Memory.alloc(8);
    const world = Memory.alloc(8), grid = Memory.alloc(8);
    id.writeS32(idValue);
    out.writePointer(NULL); world.writePointer(NULL); grid.writePointer(NULL);
    lookup(out, inv, id, world, grid);
    return out.readPointer();
  }
  function isStored() {
    return active !== null &&
      (active.placed || !findItem(active.inv, active.id).isNull());
  }
  Interceptor.attach(ptr('__ACTUAL_STORE__'), {
    onEnter(args) {
      this.ours = active !== null && itemId(args[4]) === active.id;
    },
    onLeave() { if (this.ours && active !== null) active.placed = true; }
  });
  Interceptor.attach(ptr('__MERGE__'), {
    onEnter(args) {
      this.ours = active !== null && itemId(args[1]) === active.id;
    },
    onLeave() { if (this.ours && active !== null) active.placed = true; }
  });
  Interceptor.attach(ptr('__FLOOR_CREATE__'), {
    onEnter() { if (active !== null && active.fallback) active.dropped = true; }
  });
  Interceptor.attach(ptr('__GRID_STORE__'), {
    onEnter(args) {
      if (active === null || inExtraStore || active.extraTried ||
          itemId(args[1]) !== active.id) return;
      this.extra = true;
      this.item = args[1]; this.grid = args[2]; this.type = args[3];
    },
    onLeave() {
      if (!this.extra || active === null || isStored()) return;
      active.extraTried = true;
      inExtraStore = true;
      try {
        if (!vectorReady) { itemsCtor(itemVector); vectorReady = true; }
        itemVector.add(12).writeU32(0);
        getItems(active.inv, itemVector);
        const data = itemVector.readPointer();
        const count = Math.min(itemVector.add(12).readU32(), 1024);
        for (let i = 0; i < count; i++) {
          const world = data.add(i * 8).readPointer();
          if (world.isNull()) continue;
          const type = Memory.alloc(4);
          getType(type, world);
          if (!storageTypes.has(type.readS32())) continue;
          const candidateId = world.add(88).readS32();
          if (candidateId === active.id) continue;
          const ref = findItem(active.inv, candidateId);
          if (ref.isNull()) continue;
          gridStore(ref, this.item, this.grid, this.type);
          if (isStored()) { active.nested = true; break; }
        }
      } catch (error) {
        send({event: 'catalog_storage_error', error: 'Nested storage: ' + String(error)});
      } finally { inExtraStore = false; }
    }
  });
  Interceptor.replace(pickup, new NativeCallback(function(inv, server, peer, item, hotbar, flag, physics, floor, matrix, velocity) {
    if (awaitingPlacement.length && this.returnAddress.compare(handlerStart) >= 0 &&
        this.returnAddress.compare(handlerEnd) < 0) {
      const request = awaitingPlacement.shift();
      active = {inv, id: itemId(item), placed: false, extraTried: false,
                nested: false, fallback: false, dropped: false};
      try {
        store(inv, item, anyGrid, physics, floor, matrix, velocity);
        if (!isStored()) {
          active.fallback = true;
          originalPickup(inv, server, peer, item, hotbar, flag, physics, floor, matrix, velocity);
        }
        if (request) send({serial: request.serial, ok: true,
          location: active.fallback ? (active.dropped ? 'ground' : 'hand') :
                    (active.nested ? 'secondary' : 'inventory')});
      } catch (error) {
        send({event: 'catalog_storage_error', error: String(error)});
        try {
          active.fallback = true;
          originalPickup(inv, server, peer, item, hotbar, flag, physics, floor, matrix, velocity);
          if (request) send({serial: request.serial, ok: true,
            location: active.dropped ? 'ground' : 'hand'});
        } catch (fallbackError) {
          if (request) send({serial: request.serial, ok: false, error: String(fallbackError)});
        }
      } finally {
        active = null;
      }
      return;
    }
    originalPickup(inv, server, peer, item, hotbar, flag, physics, floor, matrix, velocity);
  }, 'void', Array(10).fill('pointer')));
}
rpc.exports = {
  queue(defId, compId, paintColor, serial) {
    pending.push({defId, compId, paintColor, serial});
    return true;
  },
  cancel(serial) {
    for (const queue of [pending, awaitingPlacement]) {
      const index = queue.findIndex(request => request.serial === serial);
      if (index >= 0) queue.splice(index, 1);
    }
    return true;
  }
};
Interceptor.attach(ptr('__INVENTORY__'), {
  onEnter(args) {
    try {
      // frontend_ui_inventory.update_ui receives frontend_ui as its second
      // argument. The game's reflected current-state field is at 0x828;
      // enum value 2 is Inventory, while 1 is HUD.
      const state = args[1].add(0x828).readU32();
      const now = Date.now();
      if (state !== lastInventoryState || now - lastInventoryTick >= 100) {
        lastInventoryState = state;
        lastInventoryTick = now;
        send({event: 'inventory_state', state});
      }
    } catch (error) {
      if (!inventoryStateErrorSent) {
        inventoryStateErrorSent = true;
        send({event: 'inventory_state_error', error: String(error)});
      }
    }
  }
});
Interceptor.attach(ptr('__UPDATE__'), {
  onEnter(args) {
    this.client = args[4];
    this.scene = args[0];
  },
  onLeave() {
    if (!pending.length || this.client.isNull() || this.scene.isNull()) return;
    const request = pending.shift();
    try {
      const def = Memory.alloc(4);
      const comp = Memory.alloc(4);
      const color = Memory.alloc(4);
      def.writeS32(request.defId);
      comp.writeS32(request.compId);
      color.writeS32(request.paintColor >= 0 ? request.paintColor : this.scene.add(0x388).readS32());
      if (__STORAGE_ENABLED__) awaitingPlacement.push({serial: request.serial, when: Date.now()});
      give(this.client.add(0x90), def, comp, color, this.scene.add(0x390));
      if (!__STORAGE_ENABLED__) send({serial: request.serial, ok: true});
    } catch (error) {
      if (__STORAGE_ENABLED__) {
        const index = awaitingPlacement.findIndex(item => item.serial === request.serial);
        if (index >= 0) awaitingPlacement.splice(index, 1);
      }
      send({serial: request.serial, ok: false, error: String(error)});
    }
  }
});
'''


class Bridge:
    def __init__(self, game_path, gcl_hash):
        self.game_path = game_path
        self.direct_storage = gcl_hash in DIRECT_STORAGE_SUPPORTED_GCL
        self.catalog, self.storage_type_ids = load_catalog(game_path)
        self.session = None
        self.script = None
        self.pid = None
        self.observed_game_pid = None
        self.lock = threading.Lock()
        self.next_serial = 1
        self.waiters = {}
        self.last_inventory_update = 0.0
        self.inventory_visible = False
        self.inventory_state = None

    def on_message(self, message, data):
        if message.get("type") == "send":
            result = message.get("payload", {})
            if result.get("event") == "inventory_state":
                self.last_inventory_update = time.monotonic()
                self.inventory_state = result.get("state")
                self.inventory_visible = result.get("state") == 2
                return
            if result.get("event") == "inventory_state_error":
                print("Inventory state probe error:", result.get("error"), flush=True)
                return
            if result.get("event") == "catalog_storage_error":
                print("Catalog storage fallback:", result.get("error"), flush=True)
                return
            serial = result.get("serial")
            waiter = self.waiters.get(serial)
            if waiter:
                waiter["result"] = result
                waiter["event"].set()
        elif message.get("type") == "error":
            print("Bridge script error:", message.get("description"), flush=True)

    def ensure_attached(self):
        if self.pid is not None and self.script is not None:
            if process_alive(self.pid):
                return None
            self.detach()
        pid = game_pid(self.game_path)
        if pid is None:
            return "AnyMaker is not running"
        if self.observed_game_pid is None:
            self.observed_game_pid = pid
        if self.pid == pid and self.script is not None:
            return None
        self.detach()
        addresses = resolve(pid, self.direct_storage)
        self.session = frida.attach(pid)
        source = (SCRIPT.replace("__GIVE__", hex(addresses["give"]))
                  .replace("__UPDATE__", hex(addresses["update"]))
                  .replace("__INVENTORY__", hex(addresses["inventory"]))
                  .replace("__STORAGE_ENABLED__", "true" if self.direct_storage else "false")
                  .replace("__PICKUP__", hex(addresses.get("pickup", 0)))
                  .replace("__STORE__", hex(addresses.get("store", 0)))
                  .replace("__LOOKUP__", hex(addresses.get("lookup", 0)))
                  .replace("__ITEMS_CTOR__", hex(addresses.get("items_ctor", 0)))
                  .replace("__GET_ITEMS__", hex(addresses.get("get_items", 0)))
                  .replace("__GET_TYPE__", hex(addresses.get("get_type", 0)))
                  .replace("__GRID_STORE__", hex(addresses.get("grid_store", 0)))
                  .replace("__ACTUAL_STORE__", hex(addresses.get("actual_store", 0)))
                  .replace("__MERGE__", hex(addresses.get("merge", 0)))
                  .replace("__FLOOR_CREATE__", hex(addresses.get("floor_create", 0)))
                  .replace("__STORAGE_TYPES__", json.dumps(self.storage_type_ids))
                  .replace("__STORAGE_HANDLER__", hex(addresses.get("storage_handler", 0)))
                  .replace("__STORAGE_HANDLER_END__", hex(addresses.get("storage_handler_end", 0))))
        self.script = self.session.create_script(source)
        self.script.on("message", self.on_message)
        self.script.load()
        self.pid = pid
        print(f"Connected to AnyMaker {pid}; Give Item ready", flush=True)
        return None

    def detach(self):
        if self.script:
            try: self.script.unload()
            except frida.InvalidOperationError: pass
        if self.session:
            try: self.session.detach()
            except frida.InvalidOperationError: pass
        self.script = self.session = self.pid = None
        self.last_inventory_update = 0.0
        self.inventory_visible = False
        self.inventory_state = None

    def handle(self, message):
        # Visibility must remain available while an Add waits for placement.
        if message.get("op") == "inventory":
            return {"ok": True, "visible": self.script is not None and
                    self.inventory_visible and time.monotonic() - self.last_inventory_update < 0.5,
                    "state": self.inventory_state}
        with self.lock:
            if message.get("op") == "status":
                error = self.ensure_attached()
                return {"ok": error is None, "error": error}
            kind = message.get("kind")
            item_id = message.get("id")
            if kind not in self.catalog or item_id not in self.catalog[kind]:
                return {"ok": False, "error": "Unknown catalog entry"}
            paint_color = message.get("color")
            if paint_color is not None:
                if (kind != "Item" or item_id != "vehicle_editor_paint" or
                        type(paint_color) is not int or not 0 <= paint_color <= 85):
                    return {"ok": False, "error": "Invalid Paint Tool color"}
            error = self.ensure_attached()
            if error:
                return {"ok": False, "error": error}
            def_id, comp_id = self.catalog[kind][item_id]
            serial = self.next_serial
            self.next_serial += 1
            waiter = {"event": threading.Event(), "result": None}
            self.waiters[serial] = waiter
            try:
                self.script.exports_sync.queue(def_id, comp_id,
                                               paint_color if paint_color is not None else -1, serial)
                if not waiter["event"].wait(10):
                    self.script.exports_sync.cancel(serial)
                    return {"ok": False, "error": "Game did not place the item within ten seconds"}
                result = waiter["result"]
                return {"ok": bool(result["ok"]), "error": result.get("error"),
                        "location": result.get("location")}
            except (frida.InvalidOperationError, frida.TransportError) as error:
                self.detach()
                return {"ok": False, "error": str(error)}
            finally:
                self.waiters.pop(serial, None)


def serve_connection(bridge, connection):
    try:
        with connection:
            connection.settimeout(8)
            try:
                data = connection.recv(4096)
                message = json.loads(data.decode("utf-8"))
                response = bridge.handle(message)
            except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                return
            except Exception as error:
                response = {"ok": False, "error": str(error)}
            connection.sendall((json.dumps(response) + "\n").encode("utf-8"))
    except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
        # The overlay may exit while a reply is in flight.
        pass


def serve(bridge, owner_pid=None, port=PORT):
    with ThreadPoolExecutor(max_workers=4) as workers, socket.socket(socket.AF_INET, socket.SOCK_STREAM) as server:
        server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        server.bind(("127.0.0.1", port))
        server.listen(4)
        server.settimeout(1)
        print(f"Catalog bridge listening on 127.0.0.1:{port}", flush=True)
        while True:
            if owner_pid is not None and not process_alive(owner_pid):
                print("Catalog overlay exited; closing Add helper", flush=True)
                break
            if bridge.observed_game_pid is not None and not process_alive(bridge.observed_game_pid):
                print("AnyMaker exited; closing Add helper", flush=True)
                break
            try:
                connection, _ = server.accept()
            except socket.timeout:
                continue
            workers.submit(serve_connection, bridge, connection)


def main():
    if len(sys.argv) != 3:
        raise SystemExit("Use the matching catalog overlay and bridge from one package")
    owner_pid = int(sys.argv[1])
    port = int(sys.argv[2])
    package_folder = pathlib.Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent
    # Read the manual path from the package instead of putting it on a Windows command line.
    config = (package_folder / "game-path.txt").read_text(encoding="utf-8-sig").strip().strip('"')
    if not config:
        raise SystemExit("game-path.txt is empty")
    game_path = pathlib.Path(config)
    gcl_hash = hashlib.sha256((game_path / "bin" / "game.gcl").read_bytes()).hexdigest()
    if gcl_hash not in SUPPORTED_GCL_SHA256:
        raise SystemExit("Unsupported AnyMaker game version; bridge kept disabled")
    bridge = Bridge(game_path, gcl_hash)
    serve(bridge, owner_pid, port)


if __name__ == "__main__":
    main()
