// Full-surface scrye-wasm-abi v1 fixture, exercised by WasmPluginTests (and the
// migration's sandbox harness). Written in freestanding C so the fixture has zero
// dependencies; the checked-in test-plugin.wasm is built from this file by build.sh.
// It is deliberately dumb: substring checks instead of a JSON parser (test inputs are
// chosen to need no escape handling).
#define EXPORT(name) __attribute__((export_name(name)))
#define IMPORT(name) __attribute__((import_module("scrye"), import_name(name)))

IMPORT("print")           extern void s_print(const char* p, int l);
IMPORT("send")            extern void s_send(const char* p, int l);
IMPORT("emit")            extern void s_emit(const char* np, int nl, const char* dp, int dl);
IMPORT("get_data")        extern long long s_get_data(void);
IMPORT("get_state")       extern long long s_get_state(const char* p, int l);
IMPORT("set_state")       extern void s_set_state(const char* kp, int kl, const char* vp, int vl);
IMPORT("watch_state")     extern int s_watch_state(const char* p, int l);
IMPORT("get_variable")    extern long long s_get_variable(const char* p, int l);
IMPORT("set_variable")    extern void s_set_variable(const char* kp, int kl, const char* vp, int vl);
IMPORT("store_get")       extern long long s_store_get(const char* p, int l);
IMPORT("store_set")       extern void s_store_set(const char* kp, int kl, const char* vp, int vl);
IMPORT("store_set_many")  extern void s_store_set_many(const char* p, int l);
IMPORT("store_delete")    extern void s_store_delete(const char* p, int l);
IMPORT("store_keys")      extern long long s_store_keys(void);
IMPORT("add_panel")       extern void s_add_panel(const char* p, int l);
IMPORT("register_action") extern int s_register_action(void);
IMPORT("on_line")         extern int s_on_line(void);
IMPORT("on_channel")      extern int s_on_channel(const char* p, int l);
IMPORT("on_gmcp")         extern int s_on_gmcp(const char* p, int l);
IMPORT("on_connect")      extern int s_on_connect(void);
IMPORT("on_command")      extern int s_on_command(void);
IMPORT("on_event")        extern int s_on_event(const char* p, int l);
IMPORT("after")           extern int s_after(double secs);
IMPORT("every")           extern int s_every(double secs);
IMPORT("cancel")          extern void s_cancel(int id);
IMPORT("add_trigger")     extern int s_add_trigger(const char* p, int l);
IMPORT("add_alias")       extern int s_add_alias(const char* p, int l);

// ---- allocator (bump; free is a no-op, which the ABI allows) ----------------
static unsigned char heap[1 << 18];
static unsigned long heap_top = 0;

EXPORT("scrye_alloc") void* scrye_alloc(int n)
{
    if (n < 1) n = 1;
    if (heap_top + (unsigned long)n + 8 > sizeof heap) return 0;
    void* p = &heap[heap_top];
    heap_top += (unsigned long)((n + 7) & ~7);
    return p;
}
EXPORT("scrye_free") void scrye_free(void* p, int n) { (void)p; (void)n; }
EXPORT("scrye_abi_version") int scrye_abi_version(void) { return 1; }

// ---- tiny string kit --------------------------------------------------------
static int slen(const char* s) { int n = 0; while (s[n]) n++; return n; }
static void scpy(char* d, const char* s) { while ((*d++ = *s++)) {} }
static int contains(const char* hay, int hlen, const char* needle)
{
    int nlen = slen(needle);
    for (int i = 0; i + nlen <= hlen; i++)
    {
        int j = 0;
        while (j < nlen && hay[i + j] == needle[j]) j++;
        if (j == nlen) return 1;
    }
    return 0;
}
static char* itoa10(char* d, int v)   // returns end pointer
{
    if (v == 0) { *d++ = '0'; return d; }
    char tmp[12]; int n = 0;
    while (v > 0) { tmp[n++] = (char)('0' + v % 10); v /= 10; }
    while (n > 0) *d++ = tmp[--n];
    return d;
}
#define SET(key, val) s_set_state(key, slen(key), val, slen(val))
#define SETN(key, buf, len) s_set_state(key, slen(key), buf, len)

// Copy a packed (ptr<<32)|len return into state under `key` ("<nil>" for 0).
static void set_from_packed(const char* key, long long packed)
{
    if (packed == 0) { SET(key, "<nil>"); return; }
    const char* p = (const char*)(unsigned long)(packed >> 32);
    int l = (int)(packed & 0xffffffff);
    SETN(key, p, l);
}

// Build {"key":"<payload bytes>"} — payloads we echo are known to be escape-free.
static long long echo_result_counter = 0;

// ---- hook ids registered in init -------------------------------------------
static int h_line, h_party, h_vitals, h_conn, h_cmd, h_evt, h_spin,
           h_once, h_tick, h_cancelme, h_gold, h_alias, h_watch,
           a_click, a_cell, a_hover, a_row;
static int tick_count = 0;

// Result buffer for scrye_hook returns.
static char result[512];
static long long pack(const char* s)
{
    int l = slen(s);
    char* p = scrye_alloc(l);
    if (!p) return 0;
    for (int i = 0; i < l; i++) p[i] = s[i];
    return ((long long)(unsigned long)p << 32) | (unsigned int)l;
}

EXPORT("scrye_init") void scrye_init(void)
{
    s_print("init-ok", 7);

    // data files + store + variables, round-tripped through state so the host can assert
    set_from_packed("data", s_get_data());
    s_store_set("k", 1, "v", 1);
    set_from_packed("store.k", s_store_get("k", 1));
    set_from_packed("store.miss", s_store_get("nope", 4));
    { const char* j = "{\"b\":\"2\",\"c\":\"3\"}"; s_store_set_many(j, slen(j)); }
    set_from_packed("store.keys", s_store_keys());
    s_set_variable("var1", 4, "val1", 4);
    set_from_packed("var1", s_get_variable("var1", 4));
    s_emit("hello", 5, "world", 5);

    h_line   = s_on_line();
    h_party  = s_on_channel("Party", 5);
    h_vitals = s_on_gmcp("Char.Vitals", 11);
    h_conn   = s_on_connect();
    h_cmd    = s_on_command();
    h_evt    = s_on_event("ping", 4);
    h_spin   = s_on_event("spin", 4);
    h_once   = s_after(1.0);
    h_tick   = s_every(0.5);
    h_cancelme = s_every(0.25);
    s_cancel(h_cancelme);                       // must never fire
    h_watch  = s_watch_state("character.hp", 12);

    { const char* j = "{\"pattern\":\"^You have (\\\\d+) gold\",\"regex\":true,\"run\":true}";
      h_gold = s_add_trigger(j, slen(j)); }
    { const char* j = "{\"pattern\":\"*gong*\",\"send\":\"bow\"}";
      s_add_trigger(j, slen(j)); }
    { const char* j = "{\"pattern\":\"^gt (.*)$\",\"regex\":true,\"run\":true}";
      h_alias = s_add_alias(j, slen(j)); }

    a_click = s_register_action();
    a_cell  = s_register_action();
    a_hover = s_register_action();
    a_row   = s_register_action();
    {
        char json[512];
        char* d = json;
        scpy(d, "{\"title\":\"Wasm\",\"width\":30,\"widgets\":["
                 "{\"type\":\"label\",\"text\":\"hi\",\"dim\":true},"
                 "{\"type\":\"gauge\",\"bind\":\"character.hp\",\"max\":100},"
                 "{\"type\":\"button\",\"text\":\"Go\",\"action\":");
        d += slen(d);
        d = itoa10(d, a_click);
        scpy(d, "},{\"type\":\"colorgrid\",\"bind\":\"g\",\"weave\":true,"
                 "\"palette\":{\"#\":\"#00ff00\"},\"onClick\":");
        d += slen(d);
        d = itoa10(d, a_cell);
        scpy(d, ",\"onHover\":");
        d += slen(d);
        d = itoa10(d, a_hover);
        scpy(d, "},{\"type\":\"buttonrow\",\"buttons\":[{\"text\":\"A\",\"action\":");
        d += slen(d);
        d = itoa10(d, a_row);
        scpy(d, "}]}],\"tabs\":[{\"title\":\"More\",\"widgets\":[]}]}");
        d += slen(d);
        s_add_panel(json, (int)(d - json));
    }
}

EXPORT("scrye_hook") long long scrye_hook(int id, const char* p, int l)
{
    if (id == h_line)
    {
        if (contains(p, l, "secret")) return pack("{\"gag\":true}");
        if (contains(p, l, "old-line")) return pack("{\"rewrite\":\"new-line\"}");
        return 0;
    }
    if (id == h_party)  { SETN("party", p, l); return 0; }
    if (id == h_vitals) { SETN("vitals", p, l); return 0; }
    if (id == h_conn)   { SET("conn", "yes"); return 0; }
    if (id == h_cmd)    { SETN("cmd", p, l); return 0; }
    if (id == h_evt)    { SETN("evt", p, l); return 0; }
    if (id == h_spin)   { volatile int x = 0; for (;;) x++; }       // deadline test
    if (id == h_once)   { SET("once", "fired"); return 0; }
    if (id == h_tick)
    {
        char buf[12]; char* e = itoa10(buf, ++tick_count);
        SETN("ticks", buf, (int)(e - buf));
        return 0;
    }
    if (id == h_cancelme) { SET("cancelled", "FIRED-BUG"); return 0; }
    if (id == h_gold)   { SETN("gold", p, l); s_send("buy ale", 7); return 0; }
    if (id == h_alias)  { SETN("alias", p, l); return 0; }
    if (id == h_watch)  { SETN("watched", p, l); return 0; }
    if (id == a_click)  { SET("click", "1"); return 0; }
    if (id == a_cell)   { SETN("cell", p, l); return 0; }
    if (id == a_hover)  { SETN("hover", p, l); return 0; }
    if (id == a_row)    { SETN("row", p, l); return 0; }
    SET("unknown-hook", "BUG");
    return 0;
}
