namespace Earshot.Widget;

// How a battery refresh ended.
//   Heard: a message of the chosen set arrived after the watcher was restarted, so the values are as fresh as they get.
//   WindowsFigure: nothing was heard, but Windows' own Hands-Free figure was read.
//   NothingHeard: neither, within the refresh window: the AirPods are probably shut in their case, which sends nothing.
//   BluetoothOff: the radio is off, so there is nothing to listen to.
//   NotListening: the widget is off or its watcher is not running.
public enum BatteryRefreshOutcome { Heard, WindowsFigure, NothingHeard, BluetoothOff, NotListening }
