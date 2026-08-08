//! The search itself — pure logic, no ABI, so `cargo test` exercises it natively.
//! Semantics mirror 3s-map's Lua `find_path` (see that file's comments): BFS through
//! known rooms via compass exits and in-area links; cross-area links excluded; exits
//! explored in a fixed canonical order so results are deterministic.

use std::collections::HashMap;

pub type Pos = (i64, i64, i64);

/// Canonical compass order — determinism (Lua's `pairs` order was luck of the hash).
const DIRS: [(&str, (i64, i64, i64)); 10] = [
    ("n", (0, 1, 0)), ("s", (0, -1, 0)), ("e", (1, 0, 0)), ("w", (-1, 0, 0)),
    ("ne", (1, 1, 0)), ("nw", (-1, 1, 0)), ("se", (1, -1, 0)), ("sw", (-1, -1, 0)),
    ("u", (0, 0, 1)), ("d", (0, 0, -1)),
];

const MAX_VISITED: usize = 1_000_000;

pub struct Room {
    /// Compass exits as indexes into DIRS (kept sorted by canonical order).
    exits: Vec<u8>,
    /// In-area special links: (send command, destination). Sorted by command.
    links: Vec<(String, Pos)>,
}

pub struct Graph {
    rooms: HashMap<Pos, Room>,
}

pub fn pos(v: &serde_json::Value) -> Pos {
    (v["x"].as_i64().unwrap_or(0), v["y"].as_i64().unwrap_or(0), v["z"].as_i64().unwrap_or(0))
}

impl Graph {
    /// Build from the mapper's `area_to_table().rooms` JSON array:
    /// `[{ x, y, z, exits: ["n", ...], links: { "enter well": {x,y,z, area?} } }, ...]`.
    pub fn from_rooms(rooms: &serde_json::Value) -> Graph {
        let mut map = HashMap::new();
        let Some(list) = rooms.as_array() else { return Graph { rooms: map } };
        for rec in list {
            let at = pos(rec);
            let mut exits: Vec<u8> = Vec::new();
            if let Some(ex) = rec["exits"].as_array() {
                for e in ex {
                    if let Some(code) = e.as_str() {
                        if let Some(i) = DIRS.iter().position(|(d, _)| *d == code) {
                            exits.push(i as u8);
                        }
                    }
                }
            }
            exits.sort_unstable();
            let mut links: Vec<(String, Pos)> = Vec::new();
            if let Some(ls) = rec["links"].as_object() {
                for (cmd, d) in ls {
                    // links with an "area" field cross areas — a goto stays in its area
                    if d.get("area").map_or(true, |a| a.is_null()) {
                        links.push((cmd.clone(), pos(d)));
                    }
                }
            }
            links.sort_by(|a, b| a.0.cmp(&b.0));
            map.insert(at, Room { exits, links });
        }
        Graph { rooms: map }
    }

    pub fn len(&self) -> usize { self.rooms.len() }
    pub fn is_empty(&self) -> bool { self.rooms.is_empty() }

    /// BFS from `from` to `to` through known rooms. `Some(vec![])` = already there,
    /// `None` = unreachable (or beyond the visited cap). `allow_up: false` never takes a
    /// compass 'u' exit (chaossea exploration must not climb back onto cleared floors).
    pub fn find_path(&self, from: Pos, to: Pos, allow_up: bool) -> Option<Vec<String>> {
        if from == to { return Some(Vec::new()); }
        if !self.rooms.contains_key(&to) { return None; }

        // came_from: pos → (previous pos, send word index into `sends`)
        let mut sends: Vec<String> = Vec::new();
        let mut came: HashMap<Pos, (Pos, usize)> = HashMap::new();
        let mut queue: std::collections::VecDeque<Pos> = std::collections::VecDeque::new();
        came.insert(from, (from, usize::MAX));
        queue.push_back(from);
        let mut visited = 0usize;

        while let Some(cur) = queue.pop_front() {
            visited += 1;
            if visited > MAX_VISITED { return None; }
            let Some(room) = self.rooms.get(&cur) else { continue };

            let offer = |nxt: Pos, send: &str,
                             came: &mut HashMap<Pos, (Pos, usize)>,
                             queue: &mut std::collections::VecDeque<Pos>,
                             sends: &mut Vec<String>| -> bool {
                if came.contains_key(&nxt) || !self.rooms.contains_key(&nxt) { return false; }
                sends.push(send.to_string());
                came.insert(nxt, (cur, sends.len() - 1));
                if nxt == to { return true; }
                queue.push_back(nxt);
                false
            };

            let mut found = false;
            for &i in &room.exits {
                let (code, (dx, dy, dz)) = DIRS[i as usize];
                if !allow_up && code == "u" { continue; }
                if offer((cur.0 + dx, cur.1 + dy, cur.2 + dz), code,
                         &mut came, &mut queue, &mut sends) { found = true; break; }
            }
            if !found {
                for (cmd, dest) in &room.links {
                    if offer(*dest, cmd, &mut came, &mut queue, &mut sends) { found = true; break; }
                }
            }
            if found { break; }
        }

        // walk back from the goal
        let mut path: Vec<String> = Vec::new();
        let mut at = to;
        loop {
            let &(prev, send_idx) = came.get(&at)?;
            if send_idx == usize::MAX { break; }
            path.push(sends[send_idx].clone());
            at = prev;
        }
        path.reverse();
        Some(path)
    }
}

impl Graph {
    /// One BFS answering the whole priority list: explore everything reachable from
    /// `from` (respecting `allow_up`), then pick the FIRST target in list order that was
    /// reached. This reproduces chaossea's "pop candidates in priority order, take the
    /// first reachable one" loop — which cost one full BFS *per unreachable candidate* —
    /// in a single sweep. Returns `(index, dirs)` (0-based index into `targets`), or
    /// `None` when nothing on the list is reachable.
    pub fn find_first_reachable(&self, from: Pos, targets: &[Pos], allow_up: bool)
        -> Option<(usize, Vec<String>)>
    {
        // trivial hit: standing on a listed target
        if let Some(i) = targets.iter().position(|t| *t == from) {
            return Some((i, Vec::new()));
        }

        // full reachable-component BFS, recording the parent tree
        let mut sends: Vec<String> = Vec::new();
        let mut came: HashMap<Pos, (Pos, usize)> = HashMap::new();
        let mut queue: std::collections::VecDeque<Pos> = std::collections::VecDeque::new();
        came.insert(from, (from, usize::MAX));
        queue.push_back(from);
        let mut visited = 0usize;
        while let Some(cur) = queue.pop_front() {
            visited += 1;
            if visited > MAX_VISITED { break; }
            let Some(room) = self.rooms.get(&cur) else { continue };
            for &i in &room.exits {
                let (code, (dx, dy, dz)) = DIRS[i as usize];
                if !allow_up && code == "u" { continue; }
                let nxt = (cur.0 + dx, cur.1 + dy, cur.2 + dz);
                if came.contains_key(&nxt) || !self.rooms.contains_key(&nxt) { continue; }
                sends.push(code.to_string());
                came.insert(nxt, (cur, sends.len() - 1));
                queue.push_back(nxt);
            }
            for (cmd, dest) in &room.links {
                if came.contains_key(dest) || !self.rooms.contains_key(dest) { continue; }
                sends.push(cmd.clone());
                came.insert(*dest, (cur, sends.len() - 1));
                queue.push_back(*dest);
            }
        }

        // first listed target that the sweep reached wins
        let (index, goal) = targets.iter().enumerate()
            .find(|(_, t)| came.contains_key(t))?;
        let mut path: Vec<String> = Vec::new();
        let mut at = *goal;
        loop {
            let &(prev, send_idx) = came.get(&at)?;
            if send_idx == usize::MAX { break; }
            path.push(sends[send_idx].clone());
            at = prev;
        }
        path.reverse();
        Some((index, path))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn grid(w: i64, h: i64) -> Graph {
        // a w×h grid where every room has all four compass exits that stay in bounds
        let mut rooms = Vec::new();
        for x in 0..w {
            for y in 0..h {
                let mut exits = Vec::new();
                if y + 1 < h { exits.push("n"); }
                if y > 0 { exits.push("s"); }
                if x + 1 < w { exits.push("e"); }
                if x > 0 { exits.push("w"); }
                rooms.push(json!({ "x": x, "y": y, "z": 0, "exits": exits }));
            }
        }
        Graph::from_rooms(&serde_json::Value::Array(rooms))
    }

    #[test]
    fn straight_line() {
        let g = grid(1, 4);
        assert_eq!(g.find_path((0, 0, 0), (0, 3, 0), true), Some(vec!["n".into(); 3]));
    }

    #[test]
    fn already_there_and_unreachable() {
        let g = grid(3, 3);
        assert_eq!(g.find_path((1, 1, 0), (1, 1, 0), true), Some(vec![]));
        assert_eq!(g.find_path((0, 0, 0), (9, 9, 0), true), None);   // unmapped target
    }

    #[test]
    fn shortest_path_length_on_grid() {
        let g = grid(10, 10);
        let path = g.find_path((0, 0, 0), (9, 9, 0), true).unwrap();
        assert_eq!(path.len(), 18);   // manhattan distance, no diagonals mapped
    }

    #[test]
    fn links_join_paths_but_cross_area_links_do_not() {
        let rooms = json!([
            { "x": 0, "y": 0, "z": 0, "exits": [],
              "links": { "enter well": { "x": 5, "y": 5, "z": -1 },
                          "board ship": { "x": 0, "y": 0, "z": 0, "area": "elsewhere" } } },
            { "x": 5, "y": 5, "z": -1, "exits": ["u"] },
            { "x": 5, "y": 5, "z": 0, "exits": ["d"] },
        ]);
        let g = Graph::from_rooms(&rooms);
        assert_eq!(g.find_path((0, 0, 0), (5, 5, 0), true),
                   Some(vec!["enter well".into(), "u".into()]));
        // the cross-area link is not an edge: nothing else reaches (0,0,0)'s neighbours
        assert_eq!(g.rooms[&(0, 0, 0)].links.len(), 1);
    }

    #[test]
    fn disconnected_is_none_not_hang() {
        let rooms = json!([
            { "x": 0, "y": 0, "z": 0, "exits": [] },
            { "x": 7, "y": 7, "z": 0, "exits": [] },
        ]);
        let g = Graph::from_rooms(&rooms);
        assert_eq!(g.find_path((0, 0, 0), (7, 7, 0), true), None);
    }

    #[test]
    fn allow_up_false_never_climbs() {
        let rooms = json!([
            { "x": 0, "y": 0, "z": 0, "exits": ["u"] },
            { "x": 0, "y": 0, "z": 1, "exits": ["d"] },
        ]);
        let g = Graph::from_rooms(&rooms);
        assert_eq!(g.find_path((0, 0, 0), (0, 0, 1), true), Some(vec!["u".into()]));
        assert_eq!(g.find_path((0, 0, 0), (0, 0, 1), false), None);
    }

    #[test]
    fn first_reachable_honours_priority_order_not_distance() {
        // A: 3 steps away; B: 1 step away. A listed first and reachable → A wins.
        let g = grid(1, 4);   // rooms (0,0)..(0,3) in a north line
        let (idx, path) = g.find_first_reachable(
            (0, 0, 0), &[(0, 3, 0), (0, 1, 0)], true).unwrap();
        assert_eq!(idx, 0);
        assert_eq!(path.len(), 3);
    }

    #[test]
    fn first_reachable_skips_unreachable_heads() {
        // first two targets unmapped/disconnected; the third is the winner
        let g = grid(3, 3);
        let (idx, path) = g.find_first_reachable(
            (0, 0, 0), &[(9, 9, 0), (7, 7, 7), (2, 2, 0)], true).unwrap();
        assert_eq!(idx, 2);
        assert_eq!(path.len(), 4);
        assert!(g.find_first_reachable((0, 0, 0), &[(9, 9, 0)], true).is_none());
    }

    #[test]
    fn first_reachable_standing_on_target() {
        let g = grid(2, 2);
        let (idx, path) = g.find_first_reachable((1, 1, 0), &[(0, 0, 0), (1, 1, 0)], true).unwrap();
        assert_eq!((idx, path.len()), (1, 0));   // positional match wins even if listed second
    }

    #[test]
    fn big_grid_timing() {
        // 100k rooms — far beyond the Lua BFS's 20k visited cap. Prints timing with
        // `cargo test -- --nocapture` (native speed; wasm lands within ~2x of this).
        let t0 = std::time::Instant::now();
        let g = grid(400, 250);
        let built = t0.elapsed();
        let t1 = std::time::Instant::now();
        let path = g.find_path((0, 0, 0), (399, 249, 0), true).unwrap();
        let searched = t1.elapsed();
        assert_eq!(path.len(), 399 + 249);
        println!("100k rooms: build {:?}, BFS {:?}", built, searched);
        assert!(searched.as_millis() < 1000);
    }
}
