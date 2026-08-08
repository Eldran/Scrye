namespace Scrye.Scripting;

/// <summary>Abstraction over the scripting engine so the implementation
/// (native Lua 5.4 via KeraLua) stays swappable.</summary>
public interface IScriptHost
{
    /// <summary>Execute a chunk of script (e.g. a plugin body or a trigger action).</summary>
    void Execute(string code);

    /// <summary>Call a named script function, if defined (e.g. a trigger callback).</summary>
    void CallFunction(string name, params object[] args);
}
