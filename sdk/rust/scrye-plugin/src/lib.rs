//! SDK for writing Scrye plugins in Rust, speaking **scrye-wasm-abi v1**
//! (`docs/scrye-wasm-abi.md` in the Scrye repo — the spec is authoritative).
//!
//! ```ignore
//! use scrye_plugin as scrye;
//!
//! scrye::plugin_main!(init);
//!
//! fn init() {
//!     scrye::print("hello from Rust");
//!     scrye::on_line(|line| {
//!         if line.contains("secret") { scrye::LineAction::Gag } else { scrye::LineAction::Pass }
//!     });
//!     scrye::every(1.0, || scrye::set_state("plugin.me.tick", "1"));
//! }
//! ```
//!
//! Build with `cargo build --release --target wasm32-unknown-unknown` and put the
//! resulting `.wasm` next to a `plugin.json` with `"lang": "wasm"`. Remember that
//! **permissions are enforced** for wasm plugins: an import used without its manifest
//! permission traps with a message naming it.
//!
//! Panics abort the current dispatch (the host reports the trap and counts it toward
//! quarantine); prefer returning early over panicking.

use std::cell::RefCell;
use std::collections::HashMap;

pub use serde_json::{json, Value};

// ---- raw ABI ---------------------------------------------------------------

#[link(wasm_import_module = "scrye")]
extern "C" {
    #[link_name = "print"]           fn im_print(p: *const u8, l: i32);
    #[link_name = "log"]             fn im_log(p: *const u8, l: i32);
    #[link_name = "send"]            fn im_send(p: *const u8, l: i32);
    #[link_name = "notify"]          fn im_notify(p: *const u8, l: i32);
    #[link_name = "sound"]           fn im_sound(p: *const u8, l: i32);
    #[link_name = "capture"]         fn im_capture(pp: *const u8, pl: i32, tp: *const u8, tl: i32);
    #[link_name = "get_state"]       fn im_get_state(p: *const u8, l: i32) -> i64;
    #[link_name = "set_state"]       fn im_set_state(kp: *const u8, kl: i32, vp: *const u8, vl: i32);
    #[link_name = "watch_state"]     fn im_watch_state(p: *const u8, l: i32) -> i32;
    #[link_name = "get_variable"]    fn im_get_variable(p: *const u8, l: i32) -> i64;
    #[link_name = "set_variable"]    fn im_set_variable(kp: *const u8, kl: i32, vp: *const u8, vl: i32);
    #[link_name = "store_get"]       fn im_store_get(p: *const u8, l: i32) -> i64;
    #[link_name = "store_set"]       fn im_store_set(kp: *const u8, kl: i32, vp: *const u8, vl: i32);
    #[link_name = "store_set_many"]  fn im_store_set_many(p: *const u8, l: i32);
    #[link_name = "store_delete"]    fn im_store_delete(p: *const u8, l: i32);
    #[link_name = "store_keys"]      fn im_store_keys() -> i64;
    #[link_name = "emit"]            fn im_emit(np: *const u8, nl: i32, dp: *const u8, dl: i32);
    #[link_name = "get_data"]        fn im_get_data() -> i64;
    #[link_name = "add_panel"]       fn im_add_panel(p: *const u8, l: i32);
    #[link_name = "register_action"] fn im_register_action() -> i32;
    #[link_name = "on_line"]         fn im_on_line() -> i32;
    #[link_name = "on_channel"]      fn im_on_channel(p: *const u8, l: i32) -> i32;
    #[link_name = "on_gmcp"]         fn im_on_gmcp(p: *const u8, l: i32) -> i32;
    #[link_name = "on_connect"]      fn im_on_connect() -> i32;
    #[link_name = "on_disconnect"]   fn im_on_disconnect() -> i32;
    #[link_name = "on_prompt"]       fn im_on_prompt() -> i32;
    #[link_name = "on_idle"]         fn im_on_idle() -> i32;
    #[link_name = "on_command"]      fn im_on_command() -> i32;
    #[link_name = "on_event"]        fn im_on_event(p: *const u8, l: i32) -> i32;
    #[link_name = "after"]           fn im_after(secs: f64) -> i32;
    #[link_name = "every"]           fn im_every(secs: f64) -> i32;
    #[link_name = "cancel"]          fn im_cancel(id: i32);
    #[link_name = "add_trigger"]     fn im_add_trigger(p: *const u8, l: i32) -> i32;
    #[link_name = "add_alias"]       fn im_add_alias(p: *const u8, l: i32) -> i32;
}

// ---- required exports ------------------------------------------------------

const ALIGN: usize = 8;

#[no_mangle]
pub extern "C" fn scrye_alloc(len: i32) -> *mut u8 {
    let size = (len.max(1)) as usize;
    unsafe { std::alloc::alloc(std::alloc::Layout::from_size_align_unchecked(size, ALIGN)) }
}

#[no_mangle]
pub extern "C" fn scrye_free(ptr: *mut u8, len: i32) {
    if ptr.is_null() { return; }
    let size = (len.max(1)) as usize;
    unsafe { std::alloc::dealloc(ptr, std::alloc::Layout::from_size_align_unchecked(size, ALIGN)) }
}

#[no_mangle]
pub extern "C" fn scrye_abi_version() -> i32 { 1 }

/// Declare the plugin's init function (the body that registers hooks and panels):
/// `scrye_plugin::plugin_main!(init);` where `fn init()` is yours. Expands to the
/// `scrye_init` export in YOUR cdylib.
#[macro_export]
macro_rules! plugin_main {
    ($f:ident) => {
        #[no_mangle]
        pub extern "C" fn scrye_init() { $f(); }
    };
}

type Handler = Box<dyn FnMut(&Value) -> Option<String>>;

thread_local! {
    // Hook id → handler. Handlers are TAKEN OUT while running and reinserted after:
    // `emit` can synchronously dispatch back into this module (self-events), and a plain
    // borrow across the user's closure would panic on that reentry. A hook that fires
    // while its own handler is out (direct self-recursion) is skipped.
    static HOOKS: RefCell<HashMap<i32, Handler>> = RefCell::new(HashMap::new());
}

#[no_mangle]
pub extern "C" fn scrye_hook(id: i32, ptr: *mut u8, len: i32) -> i64 {
    let payload: Value = if len > 0 && !ptr.is_null() {
        let bytes = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
        let v = serde_json::from_slice(bytes).unwrap_or(Value::Null);
        scrye_free(ptr, len); // the host handed us ownership
        v
    } else {
        Value::Null
    };

    let mut handler = HOOKS.with(|m| m.borrow_mut().remove(&id));
    let result = handler.as_mut().and_then(|f| f(&payload));
    if let Some(f) = handler {
        HOOKS.with(|m| { m.borrow_mut().entry(id).or_insert(f); });
    }

    match result {
        None => 0,
        Some(s) => pack_out(&s),
    }
}

fn register(id: i32, handler: Handler) -> i32 {
    HOOKS.with(|m| m.borrow_mut().insert(id, handler));
    id
}

// ---- string plumbing -------------------------------------------------------

fn s(v: &str) -> (*const u8, i32) { (v.as_ptr(), v.len() as i32) }

/// Read a packed `(ptr << 32) | len` return. Whole-zero = nil; the buffer came from our
/// own `scrye_alloc`, so we reclaim it here.
fn unpack(packed: i64) -> Option<String> {
    if packed == 0 { return None; }
    let ptr = (packed >> 32) as u32 as *mut u8;
    let len = (packed & 0xffff_ffff) as i32;
    let out = if len > 0 {
        let bytes = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
        String::from_utf8_lossy(bytes).into_owned()
    } else {
        String::new()
    };
    scrye_free(ptr, len);
    Some(out)
}

/// Copy a result string into a fresh `scrye_alloc` block, packed for the host (which
/// frees it via `scrye_free` after copying).
fn pack_out(v: &str) -> i64 {
    let len = v.len() as i32;
    let ptr = scrye_alloc(len);
    if ptr.is_null() { return 0; }
    unsafe { std::ptr::copy_nonoverlapping(v.as_ptr(), ptr, v.len()); }
    ((ptr as u32 as i64) << 32) | (len as u32 as i64)
}

// ---- output & alerts -------------------------------------------------------

/// Echo to the world output, tagged with the plugin id. *(no permission needed)*
pub fn print(text: &str) { let (p, l) = s(text); unsafe { im_print(p, l) } }
/// Append to the plugin's log file. *(needs `log.write`)*
pub fn log(text: &str) { let (p, l) = s(text); unsafe { im_log(p, l) } }
/// Send a command to the MUD. *(needs `commands.send`)*
pub fn send(text: &str) { let (p, l) = s(text); unsafe { im_send(p, l) } }
/// Toast notification. *(needs `notifications.show`)*
pub fn notify(text: &str) { let (p, l) = s(text); unsafe { im_notify(p, l) } }
/// Play a sound ("beep", or a sounds-folder file). *(needs `sound.play`)*
pub fn sound(name: &str) { let (p, l) = s(name); unsafe { im_sound(p, l) } }
/// Route a line into a named capture pane. *(needs `capture.write`)*
pub fn capture(pane: &str, text: &str) {
    let (pp, pl) = s(pane); let (tp, tl) = s(text);
    unsafe { im_capture(pp, pl, tp, tl) }
}

// ---- state & variables -----------------------------------------------------

/// Current value of a state path ("" if unset). *(needs `state.read`)*
pub fn get_state(path: &str) -> String {
    let (p, l) = s(path);
    unpack(unsafe { im_get_state(p, l) }).unwrap_or_default()
}
/// Publish into the state tree (plugins publish under `plugin.<id>.*`). *(needs `state.write`)*
pub fn set_state(path: &str, value: &str) {
    let (kp, kl) = s(path); let (vp, vl) = s(value);
    unsafe { im_set_state(kp, kl, vp, vl) }
}
/// Watch a state path/subtree; the callback gets `(changed_path, value)`. *(needs `state.read`)*
pub fn watch_state(path: &str, mut f: impl FnMut(&str, &str) + 'static) {
    let (p, l) = s(path);
    let id = unsafe { im_watch_state(p, l) };
    register(id, Box::new(move |v| {
        f(v["path"].as_str().unwrap_or(""), v["value"].as_str().unwrap_or(""));
        None
    }));
}
/// *(needs `variables.read`)*
pub fn get_variable(name: &str) -> String {
    let (p, l) = s(name);
    unpack(unsafe { im_get_variable(p, l) }).unwrap_or_default()
}
/// *(needs `variables.write`)*
pub fn set_variable(name: &str, value: &str) {
    let (kp, kl) = s(name); let (vp, vl) = s(value);
    unsafe { im_set_variable(kp, kl, vp, vl) }
}

// ---- persistent store (scrye.store) ----------------------------------------

pub mod store {
    use super::*;
    /// *(all store functions need `storage.private`)*
    pub fn get(key: &str) -> Option<String> {
        let (p, l) = s(key);
        unpack(unsafe { im_store_get(p, l) })
    }
    pub fn set(key: &str, value: &str) {
        let (kp, kl) = s(key); let (vp, vl) = s(value);
        unsafe { im_store_set(kp, kl, vp, vl) }
    }
    /// N keys, one disk write.
    pub fn set_many<'a>(entries: impl IntoIterator<Item = (&'a str, &'a str)>) {
        let map: HashMap<&str, &str> = entries.into_iter().collect();
        let json = serde_json::to_string(&map).unwrap_or_else(|_| "{}".into());
        let (p, l) = s(&json);
        unsafe { im_store_set_many(p, l) }
    }
    pub fn delete(key: &str) {
        let (p, l) = s(key);
        unsafe { im_store_delete(p, l) }
    }
    pub fn keys() -> Vec<String> {
        unpack(unsafe { im_store_keys() })
            .and_then(|j| serde_json::from_str(&j).ok())
            .unwrap_or_default()
    }
}

// ---- events ----------------------------------------------------------------

/// Broadcast an inter-plugin event (every plugin's matching `on_event` fires, yours included).
pub fn emit(name: &str, data: &str) {
    let (np, nl) = s(name); let (dp, dl) = s(data);
    unsafe { im_emit(np, nl, dp, dl) }
}
/// Handle an inter-plugin event: callback gets `(data, source_plugin_id)`.
pub fn on_event(name: &str, mut f: impl FnMut(&str, &str) + 'static) {
    let (p, l) = s(name);
    let id = unsafe { im_on_event(p, l) };
    register(id, Box::new(move |v| {
        f(v["data"].as_str().unwrap_or(""), v["source"].as_str().unwrap_or(""));
        None
    }));
}

/// The manifest's declared data files, parsed (`scrye.data` equivalent).
pub fn data() -> Value {
    unpack(unsafe { im_get_data() })
        .and_then(|j| serde_json::from_str(&j).ok())
        .unwrap_or(Value::Null)
}

// ---- lines, hooks, rules ---------------------------------------------------

/// What an `on_line` handler wants done with the line.
pub enum LineAction {
    Pass,
    /// Suppress the line. *(needs `output.modify`)*
    Gag,
    /// Replace the displayed line. *(needs `output.modify`)*
    Rewrite(String),
}

/// See every output line. *(needs `output.read`; gag/rewrite additionally `output.modify`)*
pub fn on_line(mut f: impl FnMut(&str) -> LineAction + 'static) {
    let id = unsafe { im_on_line() };
    register(id, Box::new(move |v| {
        match f(v["line"].as_str().unwrap_or("")) {
            LineAction::Pass => None,
            LineAction::Gag => Some(r#"{"gag":true}"#.into()),
            LineAction::Rewrite(text) => Some(json!({ "rewrite": text }).to_string()),
        }
    }));
}

/// MIP chat messages as `(channel, message)`; empty filter = all channels. *(needs `output.read`)*
pub fn on_channel(filter: &str, mut f: impl FnMut(&str, &str) + 'static) {
    let (p, l) = s(filter);
    let id = unsafe { im_on_channel(p, l) };
    register(id, Box::new(move |v| {
        f(v["channel"].as_str().unwrap_or(""), v["message"].as_str().unwrap_or(""));
        None
    }));
}

/// GMCP as `(json, package)`; empty filter = all packages. *(needs `output.read`)*
pub fn on_gmcp(filter: &str, mut f: impl FnMut(&str, &str) + 'static) {
    let (p, l) = s(filter);
    let id = unsafe { im_on_gmcp(p, l) };
    register(id, Box::new(move |v| {
        f(v["json"].as_str().unwrap_or(""), v["package"].as_str().unwrap_or(""));
        None
    }));
}

pub fn on_connect(mut f: impl FnMut() + 'static) {
    let id = unsafe { im_on_connect() };
    register(id, Box::new(move |_| { f(); None }));
}
pub fn on_disconnect(mut f: impl FnMut() + 'static) {
    let id = unsafe { im_on_disconnect() };
    register(id, Box::new(move |_| { f(); None }));
}
pub fn on_prompt(mut f: impl FnMut() + 'static) {
    let id = unsafe { im_on_prompt() };
    register(id, Box::new(move |_| { f(); None }));
}
/// The idle guard fired — stop whatever this plugin is driving.
pub fn on_idle(mut f: impl FnMut() + 'static) {
    let id = unsafe { im_on_idle() };
    register(id, Box::new(move |_| { f(); None }));
}
/// Observe every command sent to the MUD (never veto — the send already happened).
/// *(needs `output.read`)*
pub fn on_command(mut f: impl FnMut(&str) + 'static) {
    let id = unsafe { im_on_command() };
    register(id, Box::new(move |v| { f(v["command"].as_str().unwrap_or("")); None }));
}

/// One-shot timer. Returns an id for `cancel`. *(needs `timers.manage`; 250 ms resolution)*
pub fn after(seconds: f64, mut f: impl FnMut() + 'static) -> i32 {
    let id = unsafe { im_after(seconds) };
    register(id, Box::new(move |_| { f(); None }))
}
/// Repeating timer. Returns an id for `cancel`. *(needs `timers.manage`)*
pub fn every(seconds: f64, mut f: impl FnMut() + 'static) -> i32 {
    let id = unsafe { im_every(seconds) };
    register(id, Box::new(move |_| { f(); None }))
}
pub fn cancel(timer_id: i32) {
    unsafe { im_cancel(timer_id) }
    HOOKS.with(|m| { m.borrow_mut().remove(&timer_id); });
}

/// A trigger/alias definition. Wildcard patterns (`*`/`?`) are whole-line anchored;
/// regex patterns match anywhere. `ignore_case` defaults to true.
#[derive(serde::Serialize)]
pub struct Rule {
    pattern: String,
    regex: bool,
    #[serde(rename = "ignoreCase")]
    ignore_case: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    send: Option<String>,
    run: bool,
}

impl Rule {
    pub fn new(pattern: &str) -> Self {
        Rule { pattern: pattern.into(), regex: false, ignore_case: true, send: None, run: false }
    }
    pub fn regex(mut self, yes: bool) -> Self { self.regex = yes; self }
    pub fn case_sensitive(mut self) -> Self { self.ignore_case = false; self }
    /// Template sent on match (`%1`.. expand to wildcards, host-side).
    pub fn send(mut self, template: &str) -> Self { self.send = Some(template.into()); self }
}

fn add_rule(rule: Rule, run: Option<Box<dyn FnMut(Vec<String>) + 'static>>, alias: bool) {
    let mut rule = rule;
    rule.run = run.is_some();
    let json = serde_json::to_string(&rule).unwrap_or_default();
    let (p, l) = s(&json);
    let id = unsafe { if alias { im_add_alias(p, l) } else { im_add_trigger(p, l) } };
    if let (Some(mut f), true) = (run, id != 0) {
        register(id, Box::new(move |v| {
            let wildcards = v["wildcards"].as_array().map_or(Vec::new(), |a| {
                a.iter().map(|w| w.as_str().unwrap_or("").to_string()).collect()
            });
            f(wildcards);
            None
        }));
    }
}

/// *(needs `triggers.manage`)*
pub fn add_trigger(rule: Rule) { add_rule(rule, None, false); }
pub fn add_trigger_fn(rule: Rule, f: impl FnMut(Vec<String>) + 'static) {
    add_rule(rule, Some(Box::new(f)), false);
}
/// *(needs `aliases.manage`; a matching alias consumes the typed input)*
pub fn add_alias(rule: Rule) { add_rule(rule, None, true); }
pub fn add_alias_fn(rule: Rule, f: impl FnMut(Vec<String>) + 'static) {
    add_rule(rule, Some(Box::new(f)), true);
}

// ---- panels ----------------------------------------------------------------

/// A HUD panel, built as the same JSON shape the Lua API uses (docs/scrye-wasm-abi.md).
/// Widget callbacks are closures; the SDK registers them and embeds the hook ids.
/// Re-adding a panel with the same title rebuilds it (the old build's callbacks retire).
/// *(needs `ui.panels`)*
pub struct Panel {
    value: Value,
}

impl Panel {
    pub fn new(title: &str) -> Self {
        Panel { value: json!({ "title": title, "widgets": [] }) }
    }
    pub fn width(mut self, w: f64) -> Self { self.value["width"] = json!(w); self }
    pub fn background(mut self, color: &str) -> Self { self.value["background"] = json!(color); self }
    pub fn accent(mut self, color: &str) -> Self { self.value["accent"] = json!(color); self }
    pub fn widget(mut self, w: Widget) -> Self {
        self.value["widgets"].as_array_mut().unwrap().push(w.value);
        self
    }
    pub fn tab(mut self, title: &str, widgets: Vec<Widget>) -> Self {
        let tab = json!({ "title": title,
                          "widgets": widgets.into_iter().map(|w| w.value).collect::<Vec<_>>() });
        match self.value.get_mut("tabs").and_then(|t| t.as_array_mut()) {
            Some(tabs) => tabs.push(tab),
            None => { self.value["tabs"] = json!([tab]); }
        }
        self
    }
    /// Deliver to the host (call again with the same title to rebuild).
    pub fn add(self) {
        let json = self.value.to_string();
        let (p, l) = s(&json);
        unsafe { im_add_panel(p, l) }
    }
}

/// One widget. Field names/types mirror the Lua widget vocabulary.
pub struct Widget {
    value: Value,
}

impl Widget {
    pub fn new(kind: &str) -> Self { Widget { value: json!({ "type": kind }) } }
    pub fn label(text: &str) -> Self { Widget::new("label").set("text", json!(text)) }
    pub fn gauge(bind: &str, max: &str) -> Self {
        Widget::new("gauge").set("bind", json!(bind)).set("max", json!(max))
    }
    pub fn button(text: &str, f: impl FnMut() + 'static) -> Self {
        Widget::new("button").set("text", json!(text)).set("action", json!(action_hook_unit(f)))
    }

    pub fn set(mut self, field: &str, v: Value) -> Self { self.value[field] = v; self }
    pub fn text(self, t: &str) -> Self { self.set("text", json!(t)) }
    pub fn bind(self, b: &str) -> Self { self.set("bind", json!(b)) }
    pub fn color(self, c: &str) -> Self { self.set("color", json!(c)) }
    pub fn dim(self) -> Self { self.set("dim", json!(true)) }
    pub fn weave(self) -> Self { self.set("weave", json!(true)) }
    pub fn palette<'a>(self, entries: impl IntoIterator<Item = (&'a str, &'a str)>) -> Self {
        let map: HashMap<&str, &str> = entries.into_iter().collect();
        self.set("palette", serde_json::to_value(map).unwrap_or(Value::Null))
    }
    pub fn columns(self, cols: &[&str]) -> Self { self.set("columns", json!(cols)) }
    /// Colorgrid cell click: `(col, row, ch)`.
    pub fn on_click(self, mut f: impl FnMut(i64, i64, &str) + 'static) -> Self {
        let id = unsafe { im_register_action() };
        register(id, Box::new(move |v| {
            f(v["col"].as_i64().unwrap_or(0), v["row"].as_i64().unwrap_or(0),
              v["ch"].as_str().unwrap_or(""));
            None
        }));
        self.set("onClick", json!(id))
    }
    /// Colorgrid hover (desktop only; leave = `(-1, -1, "")`).
    pub fn on_hover(self, mut f: impl FnMut(i64, i64, &str) + 'static) -> Self {
        let id = unsafe { im_register_action() };
        register(id, Box::new(move |v| {
            f(v["col"].as_i64().unwrap_or(0), v["row"].as_i64().unwrap_or(0),
              v["ch"].as_str().unwrap_or(""));
            None
        }));
        self.set("onHover", json!(id))
    }
    /// Input widget submit: the entered text.
    pub fn on_submit(self, mut f: impl FnMut(&str) + 'static) -> Self {
        let id = unsafe { im_register_action() };
        register(id, Box::new(move |v| { f(v["text"].as_str().unwrap_or("")); None }));
        self.set("onSubmit", json!(id))
    }
    /// Buttonrow child rows: `(text, on_click)` pairs.
    pub fn buttons(mut self, entries: Vec<(&str, Box<dyn FnMut() + 'static>)>) -> Self {
        let mut arr = Vec::new();
        for (text, f) in entries {
            arr.push(json!({ "text": text, "action": action_hook_boxed(f) }));
        }
        self.value["buttons"] = Value::Array(arr);
        self
    }
}

fn action_hook_unit(mut f: impl FnMut() + 'static) -> i32 {
    let id = unsafe { im_register_action() };
    register(id, Box::new(move |_| { f(); None }))
}

fn action_hook_boxed(mut f: Box<dyn FnMut() + 'static>) -> i32 {
    let id = unsafe { im_register_action() };
    register(id, Box::new(move |_| { f(); None }))
}
