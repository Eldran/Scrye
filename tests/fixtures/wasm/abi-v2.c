// Fixture: a module speaking a future ABI version — the host must refuse it by name.
#define EXPORT(name) __attribute__((export_name(name)))
static unsigned char heap[256]; static unsigned long top = 0;
EXPORT("scrye_alloc") void* scrye_alloc(int n) { void* p = &heap[top]; top += (unsigned long)n; return p; }
EXPORT("scrye_free") void scrye_free(void* p, int n) { (void)p; (void)n; }
EXPORT("scrye_abi_version") int scrye_abi_version(void) { return 2; }
EXPORT("scrye_init") void scrye_init(void) {}
EXPORT("scrye_hook") long long scrye_hook(int id, const char* p, int l) { (void)id; (void)p; (void)l; return 0; }
