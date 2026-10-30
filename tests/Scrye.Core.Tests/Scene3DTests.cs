using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The <c>scene3d</c> widget's scene (plugin API 1.24): the JSON a plugin binds, and the camera
/// that turns it into pixels. Everything the desktop view does except the drawing lives in
/// these two classes, so this is where the 3D map is pinned down.
/// </summary>
public sealed class Scene3DTests
{
    // Three rooms in a row east, and one straight above the middle one (a staircase).
    private const string Line = """
        { "nodes": [ ["1", 0, 0, 0, "#"], ["2", 1, 0, 0, "@"], ["3", 2, 0, 0, "#"], ["4", 1, 0, 1, "^", 0.5] ],
          "links": [ [0, 1, "-"], [1, 2], [1, 3, "|"] ],
          "focus": 1, "level": 0, "view": 7, "zscale": 2 }
        """;

    [Fact]
    public void ASceneParsesNodesLinksAndSettings()
    {
        Scene3D s = Scene3D.Parse(Line);
        Assert.Equal(4, s.Nodes.Count);
        Assert.Equal(new SceneNode("4", 1, 0, 1, '^', 0.5), s.Nodes[3]);
        Assert.Equal(3, s.Links.Count);
        Assert.Equal(new SceneLink(1, 2, '-'), s.Links[1]);          // colour defaults to '-'
        Assert.Equal(new SceneLink(1, 3, '|'), s.Links[2]);
        Assert.Equal(1, s.Focus);
        Assert.Equal("2", s.FocusId);
        Assert.Equal(0.0, s.Level);
        Assert.Equal(7L, s.View);
        Assert.Equal(2.0, s.ZScale);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "nodes": [] }""")]
    [InlineData("""{ "nodes": [ ["a", "x", 0, 0] ] }""")]
    public void NothingUsableIsTheEmptyScene(string json)
    {
        Scene3D s = Scene3D.Parse(json);
        Assert.True(s.IsEmpty);
        Assert.Equal(-1, s.Focus);
    }

    [Fact]
    public void BadLinksAndAnOutOfRangeFocusAreDroppedNotThrown()
    {
        Scene3D s = Scene3D.Parse("""
            { "nodes": [ [1, 0, 0, 0], [2, 1, 0, 0] ],
              "links": [ [0, 5], [-1, 0], [0, 0], [0], "x", [0, 1] ],
              "focus": 9, "zscale": 500 }
            """);
        Assert.Equal(new[] { new SceneLink(0, 1, '-') }, s.Links);
        Assert.Equal(-1, s.Focus);
        Assert.Equal("1", s.Nodes[0].Id);                             // a numeric id reads as text
        Assert.Equal('#', s.Nodes[0].Ch);                              // and the colour defaults
        Assert.Equal(20.0, s.ZScale);                                  // clamped
        Assert.Null(s.Level);
    }

    [Fact]
    public void SizesAreClamped()
    {
        Scene3D s = Scene3D.Parse("""{ "nodes": [ ["a", 0, 0, 0, "#", 99], ["b", 1, 0, 0, "#", 0] ] }""");
        Assert.Equal(3.0, s.Nodes[0].Size);
        Assert.Equal(0.2, s.Nodes[1].Size);
    }

    [Fact]
    public void FromStraightAboveTheSceneIsTheTwoDMap()
    {
        // pitch 0 = top-down: east is right, north is up, the focus room in the middle
        var cam = new SceneCamera();
        cam.Rotate(0, -1000);                                          // tilt all the way flat
        Assert.Equal(0.0, cam.Pitch);
        Scene3D s = Scene3D.Parse("""
            { "nodes": [ ["me", 0, 0, 0], ["e", 1, 0, 0], ["n", 0, 1, 0] ], "focus": 0 }
            """);
        ProjectedNode[] p = cam.Project(s, 200, 100);
        Assert.Equal(100.0, p[0].X, 6);
        Assert.Equal(50.0, p[0].Y, 6);
        Assert.True(p[1].X > p[0].X, "east is to the right");
        Assert.Equal(p[0].Y, p[1].Y, 6);
        Assert.True(p[2].Y < p[0].Y, "north is up the screen");
        Assert.Equal(p[0].X, p[2].X, 6);
    }

    [Fact]
    public void TiltedTheUpperLevelIsDrawnHigherAndNearer()
    {
        var cam = new SceneCamera();                                   // the default 50 degrees
        Scene3D s = Scene3D.Parse(Line);
        ProjectedNode[] p = cam.Project(s, 300, 300);
        Assert.True(p[3].Y < p[1].Y, "the room upstairs is above the one below it");
        Assert.True(p[3].Depth > p[1].Depth, "...and nearer the viewer, who looks down");
        Assert.Equal(p[1].X, p[3].X, 6);                               // straight above
        // the viewer stands to the south: of two rooms on one level, the southern is nearer
        Scene3D ns = Scene3D.Parse("""{ "nodes": [ ["s", 0, -1, 0], ["n", 0, 1, 0] ] }""");
        ProjectedNode[] pn = cam.Project(ns, 300, 300);
        Assert.True(pn[0].Depth > pn[1].Depth, "south is nearer than north");
        Assert.True(pn[0].Y > pn[1].Y, "...and lower on the screen");
        // levels stand ZScale apart: twice the zscale, twice the rise
        Scene3D s4 = Scene3D.Parse(Line.Replace("\"zscale\": 2", "\"zscale\": 4"));
        ProjectedNode[] p4 = cam.Project(s4, 300, 300);
        Assert.Equal(2 * (p[1].Y - p[3].Y), p4[1].Y - p4[3].Y, 6);
    }

    [Fact]
    public void TurningTheMapMovesEastToWhereNorthWas()
    {
        var cam = new SceneCamera();
        cam.Rotate(0, -1000);                                          // top-down, easier to read
        cam.Rotate(Math.PI / 2 / 0.01, 0);                             // a quarter turn
        Scene3D s = Scene3D.Parse("""{ "nodes": [ ["me", 0, 0, 0], ["e", 1, 0, 0] ], "focus": 0 }""");
        ProjectedNode[] p = cam.Project(s, 100, 100);
        Assert.Equal(p[0].X, p[1].X, 6);
        Assert.True(p[1].Y < p[0].Y, "after a quarter turn east points up the screen");
    }

    [Fact]
    public void TheViewFollowsTheFocusAndPanShiftsIt()
    {
        var cam = new SceneCamera();
        Scene3D s = Scene3D.Parse(Line);
        ProjectedNode[] p = cam.Project(s, 400, 200);
        Assert.Equal(200.0, p[1].X, 6);                                // the focus is centred
        Assert.Equal(100.0, p[1].Y, 6);
        cam.Pan(10, -5);
        p = cam.Project(s, 400, 200);
        Assert.Equal(210.0, p[1].X, 6);
        Assert.Equal(95.0, p[1].Y, 6);
    }

    [Fact]
    public void WithNoFocusTheMiddleOfTheSceneIsCentred()
    {
        var cam = new SceneCamera();
        cam.Rotate(0, -1000);
        Scene3D s = Scene3D.Parse("""{ "nodes": [ ["a", 0, 0, 0], ["b", 4, 2, 0] ] }""");
        ProjectedNode[] p = cam.Project(s, 100, 100);
        Assert.Equal(100.0, p[0].X + p[1].X, 6);                       // symmetric about the middle
        Assert.Equal(100.0, p[0].Y + p[1].Y, 6);
    }

    [Fact]
    public void SyncResetsAndFitsOnANewViewTokenAndDropsThePanWhenYouMove()
    {
        var cam = new SceneCamera();
        Scene3D s = Scene3D.Parse(Line);
        Assert.False(cam.Sync(s, 0, 0), "an unlaid-out view cannot fit, and keeps the token");
        Assert.True(cam.Sync(s, 300, 300), "the first real size resets and fits");
        Assert.False(cam.Sync(s, 300, 300), "the same token does not reset again");

        cam.Rotate(50, 20); cam.Pan(30, 30);
        double yaw = cam.Yaw;
        Assert.False(cam.Sync(s, 300, 300));
        Assert.Equal(30.0, cam.PanX);                                  // same focus: the pan stays

        Scene3D moved = Scene3D.Parse(Line.Replace("\"focus\": 1", "\"focus\": 2"));
        Assert.False(cam.Sync(moved, 300, 300));
        Assert.Equal(0.0, cam.PanX);                                   // you moved: follow you again
        Assert.Equal(0.0, cam.PanY);
        Assert.Equal(yaw, cam.Yaw);                                    // ...keeping the angle

        Scene3D reset = Scene3D.Parse(Line.Replace("\"view\": 7", "\"view\": 8"));
        Assert.True(cam.Sync(reset, 300, 300));
        Assert.Equal(SceneCamera.DefaultYaw, cam.Yaw);
        Assert.Equal(SceneCamera.DefaultPitch, cam.Pitch);
    }

    [Fact]
    public void FitKeepsEveryRoomInsideTheView()
    {
        var cam = new SceneCamera();
        var nodes = new List<string>();
        for (int i = 0; i < 40; i++) nodes.Add($"[\"{i}\", {i % 8}, {i / 8}, {i % 3}]");
        Scene3D s = Scene3D.Parse("{ \"nodes\": [" + string.Join(",", nodes) + "], \"focus\": 0 }");
        cam.Fit(s, 300, 200);
        foreach (ProjectedNode n in cam.Project(s, 300, 200))
        {
            Assert.True(n.X >= 0 && n.X <= 300, $"x {n.X}");
            Assert.True(n.Y >= 0 && n.Y <= 200, $"y {n.Y}");
        }
        // ...and a tiny map is not blown up to fill the panel
        cam.Fit(Scene3D.Parse("""{ "nodes": [ ["a", 0, 0, 0], ["b", 1, 0, 0] ] }"""), 300, 200);
        Assert.Equal(SceneCamera.MaxFitScale, cam.Scale);
    }

    [Fact]
    public void ZoomIsClampedAndIgnoresNonsense()
    {
        var cam = new SceneCamera();
        cam.Zoom(1000);
        Assert.Equal(SceneCamera.MaxScale, cam.Scale);
        cam.Zoom(0.00001);
        Assert.Equal(SceneCamera.MinScale, cam.Scale);
        cam.Zoom(2);
        Assert.Equal(2 * SceneCamera.MinScale, cam.Scale);
        cam.Zoom(double.NaN); cam.Zoom(-2); cam.Zoom(0); cam.Zoom(double.PositiveInfinity);
        Assert.Equal(2 * SceneCamera.MinScale, cam.Scale);             // nonsense changes nothing
    }

    [Fact]
    public void PitchStaysBetweenTopDownAndSideOn()
    {
        var cam = new SceneCamera();
        cam.Rotate(0, 100000);
        Assert.Equal(SceneCamera.MaxPitch, cam.Pitch);
        cam.Rotate(0, -100000);
        Assert.Equal(0.0, cam.Pitch);
    }

    [Fact]
    public void PickTakesTheRoomOnTopAndNeverScenery()
    {
        var cam = new SceneCamera();
        cam.Rotate(0, -1000);                                          // top-down: 2 and 4 overlap
        Scene3D s = Scene3D.Parse(Line.Replace("[\"1\", 0, 0, 0, \"#\"]", "[\"\", 0, 0, 0, \"#\"]"));
        ProjectedNode[] p = cam.Project(s, 300, 300);
        Assert.Equal(3, SceneCamera.Pick(s, p, p[1].X, p[1].Y));     // the upper room is on top
        Assert.Equal(2, SceneCamera.Pick(s, p, p[2].X + p[2].Radius + 2, p[2].Y));   // slack
        Assert.Equal(-1, SceneCamera.Pick(s, p, p[0].X, p[0].Y));    // no id: scenery
        Assert.Equal(-1, SceneCamera.Pick(s, p, 1, 1));               // nothing there
    }

    [Fact]
    public void DrawOrderIsBackToFront()
    {
        var cam = new SceneCamera();
        Scene3D s = Scene3D.Parse(Line);
        ProjectedNode[] p = cam.Project(s, 300, 300);
        int[] order = SceneCamera.DrawOrder(p);
        for (int i = 1; i < order.Length; i++)
            Assert.True(p[order[i - 1]].Depth <= p[order[i]].Depth);
        Assert.Equal(3, order[^1]);                                    // the upper room last
    }

    [Fact]
    public void OtherLevelsAreDimmedAndTheBackIsFaded()
    {
        var cam = new SceneCamera();
        Scene3D s = Scene3D.Parse(Line);
        ProjectedNode[] p = cam.Project(s, 300, 300);
        double min = p.Min(n => n.Depth), max = p.Max(n => n.Depth);
        double near = SceneCamera.Shade(s, p, 3, min, max);            // upstairs, off the level
        double here = SceneCamera.Shade(s, p, 1, min, max);
        Assert.True(near < here, "a room off the shown level is dimmer");
        Assert.True(near <= 0.35 + 1e-9);
        Scene3D all = Scene3D.Parse(Line.Replace("\"level\": 0,", ""));
        Assert.True(SceneCamera.Shade(all, p, 3, min, max) > here, "with no level the nearest is brightest");
        Assert.Equal(1.0, SceneCamera.Shade(all, p, 3, min, max), 9);
        Assert.Equal(0.6, SceneCamera.Shade(all, p, Array.IndexOf(p, p.MinBy(n => n.Depth)), min, max), 9);
    }

    [Fact]
    public void RoomsGrowWithZoomWithinLimits()
    {
        var cam = new SceneCamera();
        var room = new SceneNode("a", 0, 0, 0, '#', 1);
        double r = cam.RadiusOf(room);
        cam.Zoom(2);
        Assert.True(cam.RadiusOf(room) > r);
        cam.Zoom(1000);
        Assert.Equal(14.0, cam.RadiusOf(room));
        cam.Zoom(0.000001);
        Assert.Equal(1.5, cam.RadiusOf(room));
    }
}
