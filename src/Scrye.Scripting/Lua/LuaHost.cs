using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using KeraLua;
using NativeLua = KeraLua.Lua;

namespace Scrye.Scripting.Lua;

/// <summary>
/// One native Lua 5.4 state (via KeraLua) with the plumbing every Scrye use of it needs:
/// UTF-8 string conversion, a traceback-attaching pcall, registry references for held Lua
/// values, text-only chunk loading, and the delegate keep-alive list that stops the .NET GC
/// from collecting callback delegates the native side still points at.
///
/// <para><b>Boundary rules (the longjmp/exception contract):</b> a C# function pushed into
/// Lua must NEVER let a .NET exception escape into the Lua unwinder, and this class never
/// calls into Lua outside a pcall. Scrye goes one step further than the classic pattern:
/// host bindings never raise Lua errors either (<c>lua_error</c> longjmps across the managed
/// callback frame — NLua ships that everywhere, but nothing in the <c>scrye.*</c> surface
/// actually needs it: bad input degrades to a default or a <c>nil, err</c> return, matching
/// the MoonSharp runtime's behaviour). Wrap every callback body in
/// <see cref="Protect"/>.</para>
///
/// <para>Single-threaded by contract: everything runs on the owning session's loop thread,
/// same as the MoonSharp runtime this replaces. A <see cref="LuaHost"/> is never re-entered
/// concurrently.</para>
/// </summary>
public sealed class LuaHost : IDisposable
{
    /// <summary>The wrapped KeraLua state. The runtime uses this directly for stack work;
    /// the host only adds the pieces with invariants attached.</summary>
    public NativeLua State { get; }

    // Native Lua holds raw function pointers to these delegates; the .NET GC does not see
    // those pointers. Everything ever pushed as a callback is rooted here for the lifetime
    // of the state — THE classic KeraLua crash when forgotten. Cleared by Dispose, after
    // the state (and with it every native pointer into the list) is gone.
    private readonly List<LuaFunction> _keepAlive = new();

    private readonly int _tracebackRef;          // msgh used by PCallWithTraceback
    private bool _disposed;

    // ---- instruction budget (see EnableDispatchBudget) ----
    private const int HookGranularity = 100_000;         // instructions between count-hook fires
    public const long DefaultDispatchBudget = 200_000_000;   // ≈ a few hundred ms of pure Lua
    private long _budgetLimit;                            // 0 = no budget
    private long _budgetRemaining;
    private int _callDepth;                               // outermost PCall resets the budget

    public LuaHost()
    {
        State = new NativeLua(openLibs: true) { Encoding = Encoding.UTF8 };

        // The traceback message handler lives in the registry (unreachable from scripts —
        // the sandbox strips 'debug'). A managed msgh is safe: Lua calls it BEFORE the
        // longjmp back to pcall, on a normal stack.
        LuaFunction traceback = static ptr =>
        {
            t_managedDepth++;   // see EnableMemoryLimit: the traceback's own strings are never refused
            try
            {
                NativeLua l = NativeLua.FromIntPtr(ptr);
                string? msg = l.Type(1) == LuaType.String ? l.ToString(1, false) : null;
                l.Traceback(l, msg, 1);
                return 1;
            }
            finally { t_managedDepth--; }
        };
        _keepAlive.Add(traceback);
        State.PushCFunction(traceback);
        _tracebackRef = State.Ref(LuaRegistry.Index);
    }

    // ---- callbacks -----------------------------------------------------------

    /// <summary>Push a C# callback, rooting the delegate for the state's lifetime. The body
    /// runs marked as managed code, so the memory limit never refuses an allocation it makes
    /// (see <see cref="EnableMemoryLimit"/>).</summary>
    public void PushCallback(LuaFunction fn)
    {
        LuaFunction marked = ptr =>
        {
            t_managedDepth++;
            try { return fn(ptr); }
            finally { t_managedDepth--; }
        };
        _keepAlive.Add(marked);
        State.PushCFunction(marked);
    }

    // ---- memory limit (see EnableMemoryLimit) ----
    /// <summary>The memory limit plugin states get: the same as a JS plugin's.</summary>
    public const long DefaultMemoryLimit = 256L * 1024 * 1024;
    // What the allocator reads and writes. Its own object, not fields of this host, so the
    // allocator closure does not keep the host alive (see _allocatorRoot).
    private sealed class MemoryGate
    {
        public long Limit;             // 0 = none
        public long Used;
        public int ProtectedDepth;     // inside the native lua_pcall of PCall
    }
    private readonly MemoryGate _mem = new();
    private LuaAlloc? _allocator;
    // Native Lua holds a raw pointer to the allocator, and lua_close frees every block through
    // it. A host that is dropped without Dispose is closed later by KeraLua's FINALIZER - and
    // if the delegate was collected first, that close is "a callback on a garbage collected
    // delegate": the process dies. So the delegate is rooted by a GCHandle, freed in Dispose
    // after the close; for a host never disposed it stays rooted (a few bytes), and the
    // finalizer's close still finds it. Found running the real Lua under the test suite.
    private GCHandle _allocatorRoot;
    /// <summary>Above 0 while a C# function called from Lua (a binding, the traceback handler)
    /// is running on this thread. Thread-static, not per host: all of a session's states run
    /// on its one loop thread, and a binding of one plugin can reach into another's state.</summary>
    [ThreadStatic] private static int t_managedDepth;

    /// <summary>Bytes Lua has allocated for this state (meaningful once the limit is on).</summary>
    public long MemoryUsed => _mem.Used;
    public long MemoryLimit => _mem.Limit;

    /// <summary>
    /// Cap what this state may allocate. Before 1.10.7 JS and wasm plugins had a memory limit
    /// and Lua had none: a plugin that grew a table forever took the whole client with it.
    ///
    /// <para>How. The state's own allocator (the one <c>luaL_newstate</c> installed) is
    /// wrapped, not replaced: every call is forwarded to it and the byte count kept here, so
    /// blocks allocated before the switch are freed by the same allocator that made them. A
    /// growth that would pass the limit returns NULL; Lua then runs an emergency collection,
    /// retries, and only if that does not help raises "not enough memory" - an ordinary
    /// error into the plugin's pcall, reported and counted toward quarantine.</para>
    ///
    /// <para>When it may refuse - and this is the whole difficulty. A failed allocation raises
    /// a Lua error AT THAT POINT. Outside any pcall (the host pushing a line's text before
    /// calling onLine) that is a panic, and Lua's panic aborts the process. Inside a C#
    /// binding it is a longjmp across a managed frame. So the limit applies only while Lua
    /// code runs inside <see cref="PCall"/> and no C# callback is on top of it; everything
    /// else - the host's own pushes (even those inside PCall around the native call), binding
    /// results, the traceback - is small, bounded and
    /// always allowed, and may take the count a little past the limit. The budget hook is
    /// deliberately NOT marked: it raises its error with a longjmp out of its own frame, which
    /// would skip the unmark and leave the limit off for good.</para>
    /// </summary>
    public void EnableMemoryLimit(long bytes = DefaultMemoryLimit)
    {
        if (_allocator is not null) { _mem.Limit = bytes; return; }
        IntPtr baseUd = IntPtr.Zero;
        LuaAlloc baseAlloc = State.GetAllocFunction(ref baseUd);
        MemoryGate gate = _mem;
        // what the state already holds (lua_gc COUNT is KB, COUNTB the remainder in bytes)
        gate.Used = State.GarbageCollector(LuaGC.Count, 0) * 1024L + State.GarbageCollector(LuaGC.Countb, 0);
        gate.Limit = bytes;
        _allocator = (ud, ptr, osize, nsize) =>
        {
            // osize is the old block's size only when ptr is set; for a new block it is the
            // object type, not a size
            long oldSize = ptr == IntPtr.Zero ? 0 : (long)(ulong)osize;
            long newSize = (long)(ulong)nsize;
            long growth = newSize - oldSize;
            if (growth > 0 && gate.Limit > 0 && gate.ProtectedDepth > 0 && t_managedDepth == 0
                && gate.Used + growth > gate.Limit)
                return IntPtr.Zero;
            IntPtr result = baseAlloc(ud, ptr, osize, nsize);
            if (result != IntPtr.Zero || newSize == 0) gate.Used += growth;   // a free, or it worked
            return result;
        };
        _allocatorRoot = GCHandle.Alloc(_allocator);
        State.SetAllocFunction(_allocator, ref baseUd);
    }

    /// <summary>
    /// Run a callback body under the boundary rules: any .NET exception is caught and
    /// reported to <paramref name="onError"/>, never thrown into Lua. Returns the body's
    /// Lua return count, or 0 on failure (with <paramref name="l"/>'s stack restored to
    /// entry depth, so a failed binding behaves like one that returned nothing).
    /// <paramref name="l"/> is the CALLING state (may be a coroutine thread).
    /// </summary>
    public static int Protect(NativeLua l, Func<int> body, Action<string>? onError = null)
    {
        int baseTop = l.GetTop();
        try
        {
            return body();
        }
        catch (Exception ex)
        {
            l.SetTop(baseTop);
            onError?.Invoke(ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Arm a per-dispatch instruction budget: an <c>LUA_MASKCOUNT</c> hook decrements a
    /// counter every <see cref="HookGranularity"/> instructions and raises a Lua error when
    /// the outermost <see cref="PCall"/>'s allowance is spent — so an accidental
    /// <c>while true do end</c> aborts into the normal error/reporting/quarantine path
    /// instead of freezing the session loop. The budget resets at every outermost call.
    ///
    /// <para>The budget is STICKY once spent: the abort is an ordinary Lua error, so a script
    /// could catch it with <c>pcall</c> and spin on (<c>while true do pcall(f) end</c>). To
    /// stop that, the trip re-arms the count hook at ONE instruction, so the error is raised
    /// again at the very next instruction after any pcall swallows it and unwinds all the way
    /// out; the next outermost <see cref="PCall"/> restores normal granularity. Lua hooks are
    /// per-thread (a coroutine inherits its creator's hook), so a coroutine the plugin resumes
    /// is counted only if it was created after this was armed. Memory is NOT capped here: a
    /// real cap needs a custom allocator - see <see cref="EnableMemoryLimit"/>.</para>
    ///
    /// <para>Boundary note: <c>luaL_error</c> longjmps from inside the managed hook frame
    /// back to the enclosing pcall. This is the one sanctioned exception to the
    /// no-<c>lua_error</c>-from-managed-frames rule (class remarks): the hook body is a
    /// leaf — no try/finally, no live managed state on that frame — and this is the
    /// pattern the wider KeraLua/NLua ecosystem ships everywhere.</para>
    /// </summary>
    public void EnableDispatchBudget(long instructions = DefaultDispatchBudget)
    {
        _budgetLimit = instructions;
        _budgetRemaining = instructions;
        KeraLua.LuaHookFunction hook = (ptr, _) =>
        {
            NativeLua l = NativeLua.FromIntPtr(ptr);   // the running thread (may be a coroutine)
            if (!_budgetTripped)
            {
                _budgetRemaining -= HookGranularity;
                if (_budgetRemaining > 0)
                {
                    // Normal granularity for this thread — a coroutine left at 1 by an
                    // earlier dispatch's trip must not be charged 100k per instruction.
                    l.SetHook(_budgetHook!, KeraLua.LuaHookMask.Count, HookGranularity);
                    return;
                }
                // Spent. From here on raise at EVERY instruction (see remarks): a pcall that
                // swallows the error gets exactly one instruction before it is raised again.
                // Not reset here — the reset lives in the outermost PCall entry.
                _budgetTripped = true;
                State.SetHook(_budgetHook!, KeraLua.LuaHookMask.Count, 1);
            }
            l.SetHook(_budgetHook!, KeraLua.LuaHookMask.Count, 1);
            l.Error("script exceeded its execution budget (infinite loop?)");
        };
        _budgetHook = hook;                               // root it: native holds a raw pointer
        State.SetHook(hook, KeraLua.LuaHookMask.Count, HookGranularity);
    }
    private KeraLua.LuaHookFunction? _budgetHook;
    private bool _budgetTripped;   // budget spent this dispatch: the hook fires every instruction

    // ---- chunks + calls ------------------------------------------------------

    /// <summary>
    /// Load a TEXT chunk (mode "t" — precompiled bytecode is refused, closing the
    /// bytecode-smuggling hole 'load' removal alone wouldn't) and run it under pcall with
    /// traceback. Throws <see cref="LuaHostException"/> on either failure — load errors and
    /// runtime errors in an entry script are load failures to the plugin manager, same as
    /// MoonSharp's <c>DoString</c> throwing.
    /// </summary>
    public void DoText(string code, string chunkName)
    {
        LuaStatus status = State.LoadBuffer(Encoding.UTF8.GetBytes(code), "@" + chunkName, "t");
        if (status != LuaStatus.OK)
        {
            string err = PopErrorMessage(status);
            throw new LuaHostException(err);
        }
        if (!PCall(0, 0, out string? error))
            throw new LuaHostException(error ?? "unknown Lua error");
    }

    /// <summary>
    /// pcall with the traceback handler attached. The function and its
    /// <paramref name="nargs"/> arguments must already be on the stack. On success the
    /// results (exactly <paramref name="nresults"/>, unless -1 for variable) replace them; on
    /// failure the stack is restored to its pre-call depth and <paramref name="error"/> holds
    /// the traceback-decorated message.
    /// </summary>
    public bool PCall(int nargs, int nresults, out string? error)
    {
        // Outermost call = a fresh dispatch = a fresh instruction allowance. Nested
        // pcalls (an emit chain re-entering Lua) share the outer dispatch's budget.
        if (_callDepth == 0 && _budgetLimit > 0)
        {
            _budgetRemaining = _budgetLimit;
            if (_budgetTripped)
            {
                // The previous dispatch tripped the budget and left the hook firing every
                // instruction; a fresh dispatch gets normal granularity back.
                _budgetTripped = false;
                State.SetHook(_budgetHook!, KeraLua.LuaHookMask.Count, HookGranularity);
            }
        }
        _callDepth++;
        try
        {
            // Stack: ... fn a1..aN  →  insert msgh under fn.
            int fnIndex = State.GetTop() - nargs;
            State.RawGetInteger(LuaRegistry.Index, _tracebackRef);
            State.Insert(fnIndex);

            // The limit applies only inside the native call. The pushes around it run outside
            // any protection - or, for a call made from inside a binding, on top of that
            // binding's managed frame - where a refused allocation is a panic or a longjmp
            // across .NET. Inside, Lua code called from a binding is limited again: an error
            // there unwinds to THIS pcall, not across the binding.
            LuaStatus status;
            int managedDepth = t_managedDepth;
            _mem.ProtectedDepth++;
            t_managedDepth = 0;
            try { status = State.PCall(nargs, nresults, fnIndex); }
            finally { _mem.ProtectedDepth--; t_managedDepth = managedDepth; }

            // Ran out, or the host's own pushes took it past: collect now. Lua's emergency
            // collection runs only on its own allocations - a string being built (string.rep,
            // table.concat, a long ..) grows through luaL_Buffer, which asks the allocator
            // directly and fails without one. Without this the garbage of the call that just
            // failed kept the next dispatch's strings refused until the collector got round
            // to it. Outermost call only, and outside the native pcall: a plain API call.
            if (_mem.Limit > 0 && _callDepth == 1
                && (_mem.Used > _mem.Limit
                    || status == LuaStatus.ErrMem
                    || (status != LuaStatus.OK && OutOfMemoryMessage())))   // error on top of the stack
                State.GarbageCollector(LuaGC.Collect, 0);

            if (status == LuaStatus.OK)
            {
                State.Remove(fnIndex);   // drop the msgh, leaving just the results
                error = null;
                return true;
            }

            error = PopErrorMessage(status);
            State.Remove(fnIndex);       // drop the msgh
            return false;
        }
        finally
        {
            _callDepth--;
        }
    }

    // The note goes on the message line, not after a traceback (callers print the first line).
    private static string InsertAfterFirstLine(string msg, string note)
    {
        int nl = msg.IndexOf('\n');
        return nl < 0 ? msg + note : msg[..nl] + note + msg[nl..];
    }

    // luaL_Buffer's "not enough memory" is a plain runtime error, not LUA_ERRMEM.
    private bool OutOfMemoryMessage() =>
        State.Type(-1) == LuaType.String && State.ToString(-1, false).Contains("not enough memory");

    private string PopErrorMessage(LuaStatus status)
    {
        string msg = State.Type(-1) == LuaType.String
            ? State.ToString(-1, false)
            : $"non-string error ({status})";
        State.Pop(1);
        if (_mem.Limit > 0 && (status == LuaStatus.ErrMem || msg.Contains("not enough memory"))
            && !msg.Contains("memory limit"))
            msg = InsertAfterFirstLine(msg, $" - the plugin reached its {_mem.Limit / (1024 * 1024)} MB memory limit");
        return msg;
    }

    // ---- registry references -------------------------------------------------

    /// <summary>Pop the value on top of the stack into the registry; returns its ref.</summary>
    public int PopToRef() => State.Ref(LuaRegistry.Index);

    /// <summary>Copy the value at <paramref name="index"/> into the registry (stack unchanged).</summary>
    public int RefAt(int index)
    {
        State.PushCopy(index);
        return State.Ref(LuaRegistry.Index);
    }

    /// <summary>Push a registry-referenced value onto the stack.</summary>
    public void PushRef(int reference) => State.RawGetInteger(LuaRegistry.Index, reference);

    /// <summary>Release a registry reference. Safe to call with a ref already released
    /// only if you never reuse the int — the runtime clears its lists instead.</summary>
    public void Unref(int reference) => State.Unref(LuaRegistry.Index, reference);

    // ---- conversions ---------------------------------------------------------

    /// <summary>
    /// The value at <paramref name="index"/> as a string, the way the MoonSharp runtime's
    /// <c>CastToString</c> behaved for the types plugins actually pass: strings as-is,
    /// numbers culture-invariant (integers without a decimal point), booleans lowercase,
    /// nil/none as null. Reads without converting the slot, so it is safe on a
    /// <c>lua_next</c> key (<c>lua_tolstring</c> converts number slots IN PLACE, which
    /// corrupts iteration — never call <see cref="NativeLua.ToString(int)"/> on a live
    /// iteration key of unknown type).
    ///
    /// <para>Static over <paramref name="l"/> rather than bound to <see cref="State"/>:
    /// a <c>scrye.*</c> binding invoked from inside a coroutine receives that coroutine's
    /// thread state, and the arguments live on THAT stack.</para>
    /// </summary>
    public static string? ToStringLoose(NativeLua l, int index)
    {
        switch (l.Type(index))
        {
            case LuaType.String:
                return l.ToString(index, false);
            case LuaType.Number:
                return l.IsInteger(index)
                    ? l.ToInteger(index).ToString(CultureInfo.InvariantCulture)
                    : l.ToNumber(index).ToString("G14", CultureInfo.InvariantCulture);
            case LuaType.Boolean:
                return l.ToBoolean(index) ? "true" : "false";
            default:
                return null;
        }
    }

    /// <summary>String argument <paramref name="i"/> (1-based), or "" — the twin of the
    /// MoonSharp runtime's <c>Arg</c> helper.</summary>
    public static string ArgString(NativeLua l, int i)
    {
        if (i > l.GetTop() || l.IsNoneOrNil(i)) return "";
        return ToStringLoose(l, i) ?? "";
    }

    /// <summary>Number argument <paramref name="i"/> (1-based), or 0 — the twin of the
    /// MoonSharp runtime's <c>Num</c> helper (numbers directly, strings parsed, else 0).</summary>
    public static double ArgNumber(NativeLua l, int i)
    {
        if (i > l.GetTop() || l.IsNoneOrNil(i)) return 0;
        if (l.Type(i) == LuaType.Number) return l.ToNumber(i);
        string? s = ToStringLoose(l, i);
        return s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        State.Dispose();      // lua_close: frees every Lua-side value, ref and pointer
        _keepAlive.Clear();   // only now is it safe to let the delegates go
        if (_allocatorRoot.IsAllocated) _allocatorRoot.Free();   // lua_close was its last caller
    }
}

/// <summary>A Lua load or runtime error surfaced from <see cref="LuaHost"/>. Message is
/// plugin-author-facing (includes chunk name, line, and traceback where available).</summary>
public sealed class LuaHostException : Exception
{
    public LuaHostException(string message) : base(message) { }
}
