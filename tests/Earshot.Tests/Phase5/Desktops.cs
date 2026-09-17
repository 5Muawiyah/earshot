using System.Drawing;
using Earshot.Popup;

namespace Earshot.Tests.Phase5;

// Desktops for the placement tests, in physical pixels. The first matches the owner's PC as recorded in
// the research: two 1920x1080 displays, the primary on the left with a 48 px bottom taskbar.
internal static class Desktops
{
    // The owner's device name as recorded in phase 0, with its curly apostrophe (U+2019).
    public const string AirPodsName = "Jonathan\u2019s AirPods Pro";

    public static readonly Rectangle Primary = new(0, 0, 1920, 1080);
    public static readonly Rectangle Secondary = new(1920, 0, 1920, 1080);

    public static PlacementScene BottomTaskbar(Point? cursor = null) => new(
        cursor ?? new Point(1800, 1056),
        [new DisplayArea(Primary, new Rectangle(0, 0, 1920, 1032), IsPrimary: true)],
        Taskbar: Rectangle.FromLTRB(0, 1032, 1920, 1080),
        TaskbarAutoHide: false);

    public static PlacementScene TopTaskbar(Point? cursor = null) => new(
        cursor ?? new Point(1800, 24),
        [new DisplayArea(Primary, Rectangle.FromLTRB(0, 48, 1920, 1080), IsPrimary: true)],
        Taskbar: Rectangle.FromLTRB(0, 0, 1920, 48),
        TaskbarAutoHide: false);

    public static PlacementScene LeftTaskbar(Point? cursor = null) => new(
        cursor ?? new Point(31, 1000),
        [new DisplayArea(Primary, Rectangle.FromLTRB(62, 0, 1920, 1080), IsPrimary: true)],
        Taskbar: Rectangle.FromLTRB(0, 0, 62, 1080),
        TaskbarAutoHide: false);

    public static PlacementScene RightTaskbar(Point? cursor = null) => new(
        cursor ?? new Point(1889, 1000),
        [new DisplayArea(Primary, Rectangle.FromLTRB(0, 0, 1858, 1080), IsPrimary: true)],
        Taskbar: Rectangle.FromLTRB(1858, 0, 1920, 1080),
        TaskbarAutoHide: false);

    // Primary with the main taskbar at the bottom, secondary to its right with its own 48 px taskbar.
    public static PlacementScene TwoDisplays(Point cursor) => new(
        cursor,
        [
            new DisplayArea(Primary, new Rectangle(0, 0, 1920, 1032), IsPrimary: true),
            new DisplayArea(Secondary, Rectangle.FromLTRB(1920, 0, 3840, 1032), IsPrimary: false),
        ],
        Taskbar: Rectangle.FromLTRB(0, 1032, 1920, 1080),
        TaskbarAutoHide: false);
}
