using System.Text.Json;

namespace Scrye.Core.Plugins;

/// <summary>One point of a <c>scene3d</c> widget's scene (plugin API 1.24): an id the plugin
/// hears back on click/hover/right-click (empty = not clickable), a position, the palette
/// character that colours it, and a size factor (1 = a normal room).</summary>
public readonly record struct SceneNode(string Id, double X, double Y, double Z, char Ch, double Size);

/// <summary>A line between two nodes of a scene, by their index in <see cref="Scene3D.Nodes"/>,
/// coloured by a palette character.</summary>
public readonly record struct SceneLink(int A, int B, char Ch);

/// <summary>
/// The scene a <c>scene3d</c> widget draws (plugin API 1.24) — parsed from the JSON a plugin
/// puts at the widget's bound state path. UI-free on purpose: the desktop view only draws
/// what <see cref="SceneCamera"/> projects, so the parsing, the maths and the picking are all
/// here, where they can be tested without a window.
///
/// <para>The JSON (every field optional; anything malformed is dropped, never thrown):</para>
/// <code>
/// { "nodes": [ [id, x, y, z, "c", size?], ... ],   // x east, y north, z up (levels)
///   "links": [ [i, j, "c"], ... ],                 // i, j: 0-based indexes into nodes
///   "focus": i,                                    // the node the view follows (you)
///   "level": z,                                    // nodes on other levels are dimmed
///   "view":  n,                                    // change it to reset the camera
///   "zscale": 3 }                                  // how far apart levels are drawn
/// </code>
/// <para>An empty string (or one with no nodes) is "no scene": the widget then shows its 2D
/// child, which is how a plugin switches between 2D and 3D without rebuilding its panel.</para>
/// </summary>
public sealed class Scene3D
{
    /// <summary>Most nodes / links a scene keeps; the rest are dropped. A map is hundreds of
    /// rooms, so these only stop a runaway plugin from stalling the UI thread.</summary>
    public const int MaxNodes = 20000, MaxLinks = 40000;

    public IReadOnlyList<SceneNode> Nodes { get; }
    public IReadOnlyList<SceneLink> Links { get; }
    /// <summary>Index of the node the view centres on, or -1.</summary>
    public int Focus { get; }
    /// <summary>The level (z) shown at full brightness; null = every level alike.</summary>
    public double? Level { get; }
    /// <summary>The plugin's camera-reset token: a different value resets the view.</summary>
    public long View { get; }
    /// <summary>World units between levels, so floors stand apart (default 3, clamped 0.5-20).</summary>
    public double ZScale { get; }

    public bool IsEmpty => Nodes.Count == 0;

    public static Scene3D Empty { get; } = new(Array.Empty<SceneNode>(), Array.Empty<SceneLink>(), -1, null, 0, 3);

    private Scene3D(IReadOnlyList<SceneNode> nodes, IReadOnlyList<SceneLink> links, int focus,
                    double? level, long view, double zscale)
    {
        Nodes = nodes; Links = links; Focus = focus; Level = level; View = view; ZScale = zscale;
    }

    /// <summary>The id of the focus node, or null when there is none.</summary>
    public string? FocusId => Focus >= 0 && Focus < Nodes.Count ? Nodes[Focus].Id : null;

    public static Scene3D Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;

            var nodes = new List<SceneNode>();
            if (root.TryGetProperty("nodes", out JsonElement ns) && ns.ValueKind == JsonValueKind.Array)
                foreach (JsonElement n in ns.EnumerateArray())
                {
                    if (nodes.Count >= MaxNodes) break;
                    if (ReadNode(n) is { } node) nodes.Add(node);
                }
            if (nodes.Count == 0) return Empty;

            var links = new List<SceneLink>();
            if (root.TryGetProperty("links", out JsonElement ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (JsonElement l in ls.EnumerateArray())
                {
                    if (links.Count >= MaxLinks) break;
                    if (l.ValueKind != JsonValueKind.Array || l.GetArrayLength() < 2) continue;
                    if (!TryInt(l[0], out int a) || !TryInt(l[1], out int b)) continue;
                    if (a < 0 || b < 0 || a >= nodes.Count || b >= nodes.Count || a == b) continue;
                    links.Add(new SceneLink(a, b, l.GetArrayLength() > 2 ? Char(l[2], '-') : '-'));
                }

            int focus = root.TryGetProperty("focus", out JsonElement f) && TryInt(f, out int fi)
                        && fi >= 0 && fi < nodes.Count ? fi : -1;
            double? level = root.TryGetProperty("level", out JsonElement lv) && lv.ValueKind == JsonValueKind.Number
                ? lv.GetDouble() : null;
            long view = root.TryGetProperty("view", out JsonElement v) && v.ValueKind == JsonValueKind.Number
                        && v.TryGetInt64(out long vl) ? vl : 0;
            double zs = root.TryGetProperty("zscale", out JsonElement z) && z.ValueKind == JsonValueKind.Number
                ? Math.Clamp(z.GetDouble(), 0.5, 20) : 3;
            if (double.IsNaN(zs)) zs = 3;
            return new Scene3D(nodes, links, focus, level, view, zs);
        }
        catch (JsonException) { return Empty; }
    }

    private static SceneNode? ReadNode(JsonElement n)
    {
        if (n.ValueKind != JsonValueKind.Array || n.GetArrayLength() < 4) return null;
        string id = n[0].ValueKind switch
        {
            JsonValueKind.String => n[0].GetString() ?? "",
            JsonValueKind.Number => n[0].GetRawText(),
            _ => "",
        };
        if (!TryNum(n[1], out double x) || !TryNum(n[2], out double y) || !TryNum(n[3], out double zz)) return null;
        char ch = n.GetArrayLength() > 4 ? Char(n[4], '#') : '#';
        double size = n.GetArrayLength() > 5 && TryNum(n[5], out double s) ? Math.Clamp(s, 0.2, 3) : 1;
        return new SceneNode(id, x, y, zz, ch, size);
    }

    private static bool TryNum(JsonElement e, out double v)
    {
        v = 0;
        if (e.ValueKind != JsonValueKind.Number) return false;
        v = e.GetDouble();
        return double.IsFinite(v);
    }

    private static bool TryInt(JsonElement e, out int v)
    {
        v = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out v);
    }

    private static char Char(JsonElement e, char fallback)
    {
        if (e.ValueKind != JsonValueKind.String) return fallback;
        string s = e.GetString() ?? "";
        return s.Length > 0 ? s[0] : fallback;
    }
}

/// <summary>Where one node lands on screen: its centre, how near the viewer it is (bigger =
/// nearer, so draw in ascending order), and its radius in pixels.</summary>
public readonly record struct ProjectedNode(double X, double Y, double Depth, double Radius);

/// <summary>
/// The orbit camera of a <c>scene3d</c> view: it looks at the scene's focus node (you) from
/// an angle, and the user turns it (<see cref="Rotate"/>), zooms it and pans it. Orthographic
/// on purpose — a map read at an angle must keep its rooms the same size at the back as at
/// the front, or a corridor looks like it narrows.
///
/// <para>Angles: <see cref="Yaw"/> turns the map about the vertical axis (0 = north away from
/// you); <see cref="Pitch"/> tilts it, 0 = straight down (the 2D map) to almost side-on.</para>
/// </summary>
public sealed class SceneCamera
{
    public const double DefaultYaw = 0;
    public const double DefaultPitch = 50 * Math.PI / 180;
    public const double MaxPitch = 89 * Math.PI / 180;
    public const double MinScale = 0.5, MaxScale = 80;
    /// <summary>A fit never zooms in past this - a three-room map should not fill the panel.</summary>
    public const double MaxFitScale = 24;

    public double Yaw { get; private set; } = DefaultYaw;
    public double Pitch { get; private set; } = DefaultPitch;
    /// <summary>Pixels per world unit (one grid step).</summary>
    public double Scale { get; private set; } = 16;
    public double PanX { get; private set; }
    public double PanY { get; private set; }

    private long? _view;
    private string? _focusId;

    public void Reset()
    {
        Yaw = DefaultYaw; Pitch = DefaultPitch; PanX = 0; PanY = 0;
    }

    /// <summary>Turn by a pointer drag: across turns about the vertical, up/down tilts.</summary>
    public void Rotate(double dx, double dy)
    {
        Yaw = (Yaw + dx * 0.01) % (2 * Math.PI);
        Pitch = Math.Clamp(Pitch + dy * 0.01, 0, MaxPitch);
    }

    public void Zoom(double factor)
    {
        if (!(factor > 0) || !double.IsFinite(factor)) return;
        Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
    }

    public void Pan(double dx, double dy) { PanX += dx; PanY += dy; }

    /// <summary>
    /// Bring the camera in line with a (new) scene before drawing it. The first scene, and any
    /// scene whose <see cref="Scene3D.View"/> token changed, resets the angle and fits the
    /// whole scene in the view; a different focus id (you moved) drops the pan so the view
    /// follows you again. Returns true when it reset.
    /// </summary>
    public bool Sync(Scene3D scene, double width, double height)
    {
        if (scene.IsEmpty) return false;
        bool reset = false;
        // (a view not laid out yet cannot fit anything: keep the token for the first real size)
        if (_view != scene.View && width > 0 && height > 0)
        {
            _view = scene.View;
            Reset();
            Fit(scene, width, height);
            reset = true;
        }
        string? focus = scene.FocusId;
        if (focus != _focusId)
        {
            _focusId = focus;
            PanX = 0; PanY = 0;
        }
        return reset;
    }

    /// <summary>The point the camera looks at: the focus node, else the middle of the scene.</summary>
    public static (double X, double Y, double Z) Target(Scene3D scene)
    {
        if (scene.Focus >= 0) { SceneNode f = scene.Nodes[scene.Focus]; return (f.X, f.Y, f.Z); }
        if (scene.IsEmpty) return (0, 0, 0);
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue,
               maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (SceneNode n in scene.Nodes)
        {
            minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X);
            minY = Math.Min(minY, n.Y); maxY = Math.Max(maxY, n.Y);
            minZ = Math.Min(minZ, n.Z); maxZ = Math.Max(maxZ, n.Z);
        }
        return ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
    }

    /// <summary>Screen position of a world point, BEFORE scale and the centring offset: x right,
    /// y down, plus the depth toward the viewer.</summary>
    private (double Sx, double Sy, double Depth) View(double x, double y, double z,
                                                     (double X, double Y, double Z) t, double zscale)
    {
        double px = x - t.X, py = y - t.Y, pz = (z - t.Z) * zscale;
        double cy = Math.Cos(Yaw), sy = Math.Sin(Yaw);
        double rx = px * cy - py * sy;
        double ry = px * sy + py * cy;            // north, turned
        double cp = Math.Cos(Pitch), sp = Math.Sin(Pitch);
        return (rx, -(ry * cp) - pz * sp, pz * cp - ry * sp);
    }

    /// <summary>Zoom so the whole scene fits <paramref name="width"/> x <paramref name="height"/>
    /// around the target, with a margin; never past <see cref="MaxFitScale"/>.</summary>
    public void Fit(Scene3D scene, double width, double height)
    {
        if (scene.IsEmpty || width <= 0 || height <= 0) return;
        var t = Target(scene);
        double mx = 0.5, my = 0.5;
        foreach (SceneNode n in scene.Nodes)
        {
            var (sx, sy, _) = View(n.X, n.Y, n.Z, t, scene.ZScale);
            mx = Math.Max(mx, Math.Abs(sx)); my = Math.Max(my, Math.Abs(sy));
        }
        double s = Math.Min(width / 2 / mx, height / 2 / my) * 0.9;
        Scale = Math.Clamp(s, MinScale, MaxFitScale);
    }

    /// <summary>Radius in pixels of a node at the current zoom: a room is about two thirds of
    /// a grid step across, but never a speck nor a blot.</summary>
    public double RadiusOf(SceneNode n) => Math.Clamp(Scale * 0.33 * n.Size, 1.5, 14 * n.Size);

    /// <summary>Every node of the scene on a <paramref name="width"/> x <paramref name="height"/> view.</summary>
    public ProjectedNode[] Project(Scene3D scene, double width, double height)
    {
        var t = Target(scene);
        var outp = new ProjectedNode[scene.Nodes.Count];
        double cx = width / 2 + PanX, cy = height / 2 + PanY;
        for (int i = 0; i < outp.Length; i++)
        {
            SceneNode n = scene.Nodes[i];
            var (sx, sy, d) = View(n.X, n.Y, n.Z, t, scene.ZScale);
            outp[i] = new ProjectedNode(cx + sx * Scale, cy + sy * Scale, d, RadiusOf(n));
        }
        return outp;
    }

    /// <summary>Node indexes back to front: draw in this order so nearer rooms cover farther ones.</summary>
    public static int[] DrawOrder(ProjectedNode[] p)
    {
        var order = new int[p.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = p[a].Depth.CompareTo(p[b].Depth);
            return c != 0 ? c : a.CompareTo(b);          // stable for equal depth
        });
        return order;
    }

    /// <summary>
    /// The clickable node under (<paramref name="x"/>, <paramref name="y"/>): of the nodes whose
    /// disc (plus a little slack, so a 3-px room can still be hit) covers the point, the NEAREST
    /// the viewer — the one drawn on top. Nodes with an empty id are scenery and never picked.
    /// -1 when there is none.
    /// </summary>
    public static int Pick(Scene3D scene, ProjectedNode[] p, double x, double y, double slack = 3)
    {
        int best = -1;
        for (int i = 0; i < p.Length && i < scene.Nodes.Count; i++)
        {
            if (scene.Nodes[i].Id.Length == 0) continue;
            double dx = p[i].X - x, dy = p[i].Y - y, r = p[i].Radius + slack;
            if (dx * dx + dy * dy > r * r) continue;
            if (best < 0 || p[i].Depth > p[best].Depth) best = i;
        }
        return best;
    }

    /// <summary>
    /// How bright to draw node <paramref name="i"/>, 0-1: nodes off the scene's
    /// <see cref="Scene3D.Level"/> are dimmed to a third, and depth fades the back of the
    /// scene a little so near and far read apart. <paramref name="minDepth"/> /
    /// <paramref name="maxDepth"/> are the projected range.
    /// </summary>
    public static double Shade(Scene3D scene, ProjectedNode[] p, int i, double minDepth, double maxDepth)
    {
        double s = 1;
        if (scene.Level is double lv && Math.Abs(scene.Nodes[i].Z - lv) > 1e-9) s = 0.35;
        double span = maxDepth - minDepth;
        if (span > 1e-9) s *= 0.6 + 0.4 * (p[i].Depth - minDepth) / span;
        return s;
    }
}
