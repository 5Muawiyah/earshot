namespace Earshot.Widget;

// What the card knows of this PC's own Bluetooth, from the radio check's last result: Ok says nothing is wrong, Off is a radio that is
// off or switched off, NotWorking a radio that is absent, cannot do Classic Bluetooth or reports a problem, and NoAirPodsPaired a PC
// with no AirPods paired. The card shows them only where it opened as asked.
internal enum CardRadio { Ok, Off, NotWorking, NoAirPodsPaired }
