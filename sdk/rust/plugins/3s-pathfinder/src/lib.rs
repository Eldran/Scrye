//! 3s-pathfinder: BFS path search for Scrye's mapping plugins, in Rust/wasm.
//!
//! Both 3s-map (`map goto`) and 3s-chaossea (exploration / leave / find) delegate their
//! path searches here over inter-plugin events, which dispatch synchronously — the whole
//! exchange completes inside the requester's emit, and a requester that gets no reply
//! falls back to its own Lua search.
//!
//! Request `map.path.find`:
//! `{ id, area, serial, from: {x,y,z}, allowUp?, to? | targets?: [{x,y,z}...], rooms? }`
//! - `to` — single-target search (3s-map's goto).
//! - `targets` — a PRIORITY-ordered list (chaossea's frontier): one BFS sweep, the first
//!   listed target that proved reachable wins. Reply carries its 1-based `index`.
//! - `allowUp` (default true) — false never climbs a compass 'u' exit (exploration must
//!   not wander back onto cleared floors).
//! - `rooms` — the graph (`[{x,y,z,exits,links?}]`), attached only when we asked: requests
//!   carry the requester's change counter (`serial`), and a `{ id, needArea: true }` reply
//!   means our cache for that `area` is stale. The cache is keyed BY AREA, so 3s-map and
//!   chaossea don't evict each other.
//!
//! Reply `map.path.result`: `{ id, found, dirs, index? }` | `{ id, found: false }` |
//! `{ id, needArea: true }`.
//!
//! The manifest declares NO permissions: events and print are always available, and this
//! plugin needs nothing else.

use scrye_plugin as scrye;
use std::cell::RefCell;
use std::collections::HashMap;

pub mod bfs;

scrye::plugin_main!(init);

thread_local! {
    /// area name → (serial, graph built from that area's last rooms payload).
    static CACHE: RefCell<HashMap<String, (i64, bfs::Graph)>> = RefCell::new(HashMap::new());
}

fn init() {
    scrye::on_event("map.path.find", |data, _source| {
        let req: serde_json::Value = match serde_json::from_str(data) {
            Ok(v) => v,
            Err(_) => return,
        };
        let id = req["id"].as_i64().unwrap_or(0);
        let area = req["area"].as_str().unwrap_or("").to_string();
        let serial = req["serial"].as_i64().unwrap_or(0);
        let allow_up = req["allowUp"].as_bool().unwrap_or(true);

        if let Some(rooms) = req.get("rooms") {
            let graph = bfs::Graph::from_rooms(rooms);
            CACHE.with(|c| { c.borrow_mut().insert(area.clone(), (serial, graph)); });
        }
        let fresh = CACHE.with(|c| {
            matches!(c.borrow().get(&area), Some((s, _)) if *s == serial)
        });
        if !fresh {
            scrye::emit("map.path.result",
                        &serde_json::json!({ "id": id, "needArea": true }).to_string());
            return;
        }

        let from = bfs::pos(&req["from"]);
        let reply = CACHE.with(|c| {
            let cache = c.borrow();
            let (_, graph) = cache.get(&area).unwrap();   // fresh ⇒ present
            if let Some(list) = req["targets"].as_array() {
                // priority list: one sweep, first reachable listed target wins
                let targets: Vec<bfs::Pos> = list.iter().map(bfs::pos).collect();
                match graph.find_first_reachable(from, &targets, allow_up) {
                    Some((index, dirs)) => serde_json::json!(
                        { "id": id, "found": true, "index": index + 1, "dirs": dirs }),
                    None => serde_json::json!({ "id": id, "found": false }),
                }
            } else {
                match graph.find_path(from, bfs::pos(&req["to"]), allow_up) {
                    Some(dirs) => serde_json::json!({ "id": id, "found": true, "dirs": dirs }),
                    None => serde_json::json!({ "id": id, "found": false }),
                }
            }
        });
        scrye::emit("map.path.result", &reply.to_string());
    });

    scrye::print("pathfinder ready (BFS in Rust/wasm; 3s-map and 3s-chaossea delegate here)");
}
