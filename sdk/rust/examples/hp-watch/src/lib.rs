//! The Rust twin of the guide's hp-watch example: watches your HP, warns when it's low,
//! adds an `hp` command, and shows a gauge. Build:
//!
//!   rustup target add wasm32-unknown-unknown
//!   cargo build --release --target wasm32-unknown-unknown
//!
//! then copy target/wasm32-unknown-unknown/release/hp_watch.wasm next to plugin.json
//! in your Scrye plugins folder.
use scrye_plugin as scrye;
use scrye_plugin::{Panel, Rule, Widget};
use std::cell::Cell;

scrye::plugin_main!(init);

thread_local! {
    static WARNED: Cell<bool> = const { Cell::new(false) };
}

fn hp_percent() -> Option<(i64, i64, i64)> {
    let cur: i64 = scrye::get_state("character.health.current").parse().ok()?;
    let max: i64 = scrye::get_state("character.health.max").parse().ok()?;
    if max <= 0 { return None; }
    Some((cur, max, cur * 100 / max))
}

fn init() {
    let prefix = "plugin.hp-watch-rs.";

    // Re-check on every prompt (cheap, and exactly when vitals just updated).
    scrye::watch_state("character.health", move |_path, _value| {
        if let Some((_, _, pct)) = hp_percent() {
            scrye::set_state(&format!("{prefix}pct"), &pct.to_string());
            let low = pct < 25;
            let already = WARNED.with(|w| w.get());
            if low && !already {
                scrye::notify(&format!("LOW HP: {pct}%"));
                WARNED.with(|w| w.set(true));
            } else if !low && already {
                WARNED.with(|w| w.set(false));
            }
        }
    });

    // `hp` typed command: report to the world output.
    scrye::add_alias_fn(Rule::new("^hp$").regex(true), move |_wildcards| {
        match hp_percent() {
            Some((cur, max, pct)) => scrye::print(&format!("HP {cur}/{max} ({pct}%)")),
            None => scrye::print("HP unknown (no vitals yet)"),
        }
    });

    Panel::new("HP (Rust)")
        .width(26.0)
        .widget(Widget::gauge("character.health.current", "character.health.max")
            .color("#cc3344"))
        .widget(Widget::label("").bind(&format!("{prefix}pct")))
        .widget(Widget::button("Heal", || scrye::send("cast heal")))
        .add();

    scrye::print("hp-watch (Rust) loaded");
}
