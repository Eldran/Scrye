using System.Globalization;
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
            NativeLua l = NativeLua.FromIntPtr(ptr);
            string? msg = l.Type(1) == LuaType.String ? l.ToString(1, false) : null;
            l.Traceback(l, msg, 1);
            return 1;
        };
        _keepAlive.Add(traceback);
        State.PushCFunction(traceback);
        _tracebackRef = State.Ref(LuaRegistry.Index);
    }

    // ---- callbacks -----------------------------------------------------------

    /// <summary>Push a C# callback, rooting the delegate for the state's lifetime.</summary>
    public void PushCallback(LuaFunction fn)
    {
        _keepAlive.Add(fn);
        State.PushCFunction(fn);
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
    /// <para>Two honest limitations. The abort is an ordinary Lua error, so a script that
    /// deliberately wraps its spin in <c>pcall</c> can swallow it and keep spinning —
    /// this is a seatbelt against accidents, not an adversarial sandbox (wasm's epoch
    /// traps are the uncatchable version). And Lua hooks are per-thread: code running
    /// inside a coroutine the plugin resumes is not counted.</para>
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
            _budgetRemaining -= HookGranularity;
            if (_budgetRemaining > 0) return;
            // Not reset here: if the script swallows the error and keeps spinning, every
            // subsequent fire raises again. The reset lives in the outermost PCall entry.
            NativeLua.FromIntPtr(ptr).Error(
                "script exceeded its execution budget (infinite loop?)");
        };
        _budgetHook = hook;                               // root it: native holds a raw pointer
        State.SetHook(hook, KeraLua.LuaHookMask.Count, HookGranularity);
    }
    private KeraLua.LuaHookFunction? _budgetHook;

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
        if (_callDepth == 0 && _budgetLimit > 0) _budgetRemaining = _budgetLimit;
        _callDepth++;
        try
        {
            // Stack: ... fn a1..aN  →  insert msgh under fn.
            int fnIndex = State.GetTop() - nargs;
            State.RawGetInteger(LuaRegistry.Index, _tracebackRef);
            State.Insert(fnIndex);

            LuaStatus status = State.PCall(nargs, nresults, fnIndex);
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

    private string PopErrorMessage(LuaStatus status)
    {
        string msg = State.Type(-1) == LuaType.String
            ? State.ToString(-1, false)
            : $"non-string error ({status})";
        State.Pop(1);
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
    }
}

/// <summary>A Lua load or runtime error surfaced from <see cref="LuaHost"/>. Message is
/// plugin-author-facing (includes chunk name, line, and traceback where available).</summary>
public sealed class LuaHostException : Exception
{
    public LuaHostException(string message) : base(message) { }
}
