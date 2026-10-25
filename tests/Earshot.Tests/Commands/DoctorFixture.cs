using System.Text.Json;
using Earshot.Commands;

namespace Earshot.Tests.Commands;

// One invented PC for the doctor report, read by both the C# report and the install script's Doctor action so their output can
// be compared. Every personal value in it is made up, and Planted lists them: none may survive into a report.
internal static class DoctorFixture
{
    // The text a failed read would carry, with a name, an address, a container, a SID, a PC name and a path in it.
    internal const string EventLogText =
        "The event log said: Jonathan's AirPods Pro (0A1B2C3D4E8C) failed on JONATHAN-PC for S-1-5-21-1111111111-2222222222-3333333333-1001 " +
        "container 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13 at C:\\Users\\jonathan\\AppData\\Local\\Earshot\\x.log";

    internal static readonly string[] Planted =
    [
        "Jonathan", "JONATHAN-PC", "0A1B2C3D4E8C", "0A:1B:2C:3D:4E:8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13",
        "H9XQ2LMN0Z", "F2LXK9QWP1", "2A3B4C5D", "S-1-5-21-1111111111", "C:\\Users\\jonathan", "C00000000", "9d1e7f30",
        // The address 0A1B2C3D4E8C as a decimal integer (and, in the fixture's history, zero-extended to 16 hex digits).
        "11111822610060", "00000A1B2C3D4E8C",
    ];

    // The facts as JSON, in the shape the script's hooks read and the C# facts deserialise from. The tray read is left out
    // (null) and its failure is in unreadable, so both languages report the same read that failed.
    internal static string Json() => """
        {
          "install": { "folder": "C:\\Users\\jonathan\\AppData\\Local\\Programs\\Earshot", "installed": true, "version": "1.4.0", "appsEntry": true, "appsVersion": "1.4.0", "startMenuShortcut": true, "runValue": false },
          "trayProcesses": null,
          "tasks": [
            { "name": "Gate", "present": true, "state": "Ready" },
            { "name": "Protect", "present": false, "state": "" },
            { "name": "BootBlock", "present": true, "state": "Ready" }
          ],
          "service": { "present": true, "state": "Stopped" },
          "devices": [
            { "instanceId": "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\\7&2A3B4C5D&0&0A1B2C3D4E8C_C00000000", "name": "Jonathan's AirPods Pro", "serial": "H9XQ2LMN0Z", "container": "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13", "status": "Error", "present": true, "problem": 43, "configFlags": 1 },
            { "instanceId": "BTHLE\\DEV_0A1B2C3D4E8C\\8&1&0&0A1B2C3D4E8C", "name": "Jonathan's AirPods Pro", "serial": null, "container": null, "status": "OK", "present": false, "problem": null, "configFlags": 0 },
            { "instanceId": "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&12A8\\7&9D1E7F30&0&F2LXK9QWP1_C00000000", "name": "Jonathan's iPhone", "serial": "F2LXK9QWP1", "container": null, "status": "OK", "present": true, "problem": 0, "configFlags": 0 },
            { "instanceId": "BTHENUM\\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&200E\\7&9D1E7F30&0&AAAAAAAAAAAA_C00000001", "name": null, "serial": null, "container": null, "status": "OK", "present": false, "problem": null, "configFlags": null }
          ],
          "btServices": [
            { "name": "bthserv", "state": "Running" },
            { "name": "BTAGService", "state": "Stopped" },
            { "name": "BthAvctpSvc", "state": "" }
          ],
          "radio": {
            "found": true, "driverVersion": "23.60.0.2", "adapterFound": true, "classicSupported": true, "state": "Problem", "radioState": "On",
            "devNodeStatus": "0x00000400", "problemCode": 10, "driverDate": "2024-03-09",
            "readCodes": [ "cm-status-radio:CR_SUCCESS", "bluetooth-radio:S_OK" ]
          },
          "startup": { "runCommand": "\"C:\\Users\\jonathan\\AppData\\Local\\Programs\\Earshot\\Earshot.exe\" --startup", "targetExists": false, "approvedFlag": "02" },
          "filters": [
            { "name": "BattMonFltr", "on": "airpods" },
            { "name": "BthEnum", "on": "rfcomm" }
          ],
          "history": {
            "exists": true,
            "lines": 7,
            "last": [
              "{\"time\":1760000000,\"step\":\"connect\",\"outcome\":\"failed\",\"code\":-2147023673,\"reason\":\"timeout\",\"device\":\"Jonathan's AirPods Pro\",\"address\":\"0A1B2C3D4E8C\"}",
              "{\"time\":1760000100,\"step\":\"connect\",\"outcome\":\"failed\",\"reason\":\"Jonathan's AirPods Pro would not answer at 0A:1B:2C:3D:4E:8C\"}",
              "not json Jonathan's AirPods 0A1B2C3D4E8C",
              "{\"time\":1760000200,\"step\":\"Connect 0A1B2C3D4E8C\",\"outcome\":true,\"attempt\":2}",
              "{\"time\":1760000300,\"step\":\"connect\",\"outcome\":\"failed\",\"code\":11111822610060,\"attempt\":99999,\"durationMs\":-5}",
              "{\"time\":11111822610060,\"step\":11111822610060,\"outcome\":\"seen 00000A1B2C3D4E8C and 11111822610060\",\"reason\":\"ok\"}",
              "{\"time\":\"2026-10-12T09:15:30.123Z\",\"step\":\"connect\",\"outcome\":\"ok\",\"code\":0,\"attempt\":1,\"durationMs\":850}",
              "{\"time\":\"11111822610060\",\"step\":\"connect\",\"outcome\":\"ok\",\"code\":\"11111822610060\"}"
            ],
            "pairing": [
              { "time": "2026-10-25T20:03:40Z", "outcome": "failed", "status": "AuthenticationFailure", "statusValue": 9, "modelCheck": null, "durationMs": 61000 },
              { "time": "2026-10-25T20:08:22Z", "outcome": "paired", "status": "Paired", "statusValue": 0, "modelCheck": "airpods", "durationMs": 21000 },
              { "time": "Jonathan at 0A1B2C3D4E8C", "outcome": "Jonathan's AirPods Pro", "status": "see 0A:1B:2C:3D:4E:8C", "statusValue": 11111822610060, "modelCheck": "x y", "durationMs": -4 }
            ]
          },
          "lastReadingSaved": true,
          "identity": { "user": "jonathan", "computer": "JONATHAN-PC", "profile": "C:\\Users\\jonathan", "localAppData": "C:\\Users\\jonathan\\AppData\\Local" },
          "unreadable": [ { "part": "tray", "reason": "The event log said: Jonathan's AirPods Pro (0A1B2C3D4E8C) failed on JONATHAN-PC for S-1-5-21-1111111111-2222222222-3333333333-1001 container 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13 at C:\\Users\\jonathan\\AppData\\Local\\Earshot\\x.log" } ]
        }
        """;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    internal static DoctorFacts Facts() =>
        JsonSerializer.Deserialize<DoctorFacts>(Json(), ReadOptions)
        ?? throw new InvalidOperationException("The fixture did not read.");
}

internal sealed class FakeDoctorSource(DoctorFacts facts) : IDoctorSource
{
    public int Reads { get; private set; }

    public DoctorFacts Read()
    {
        Reads++;
        return facts;
    }
}
