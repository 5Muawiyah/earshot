using Earshot.Contracts;

namespace Earshot.Widget;

// What one read of Windows' own Hands-Free battery property found.
//   Percent: the figure, or null when there is none to take (empty, an unexpected type, out of range, or two
//   different figures).
//   Origin: which kind of object the figure came from ("device node" or "paired object"), never an id.
//   Note: one plain line about what was found when there is no figure, so a missing figure is never silent. Every
//   failing step's raw code is in Steps.
internal sealed record HandsFreeBatteryRead(int? Percent, string? Origin, string? Note, IReadOnlyList<StepOutcome> Steps);

// Windows' Hands-Free battery figure for the paired AirPods, read from the property Windows fills in for a
// Bluetooth device while the Hands-Free profile is up. Read-only, and it never throws for anything Windows did.
// The real source reads device properties with CfgMgr32 and DevQuery and so lives outside this namespace: nothing
// under Earshot.Widget may reach a device path, and a property read is the nearest thing to one.
internal interface IHandsFreeBatterySource
{
    // container and address12 are the pinned device's. An invalid pair reads as no figure.
    HandsFreeBatteryRead Read(Guid container, string address12);
}
