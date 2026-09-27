using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;

namespace Earshot.AdvertProbe;

// Phase 0 capture: listens passively for Bluetooth LE advertisements and logs every Apple proximity
// pairing message it sees, with step marks the owner makes while handling the case and buds.
//
// Read-only. A passive watcher sends no scan requests and nothing here connects, pairs or writes to
// any device.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementwatcher
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothlescanningmode
//
// Apple's manufacturer data (company identifier 0x004C) is a run of type, length, value items; type
// 0x07 is the proximity pairing message. Protocol facts from the furiousMAC Continuity notes,
// github.com/furiousMAC/continuity (messages/proximity_pairing.md), and Celosia and Cunche,
// "Discontinued Privacy", PETS 2020, petsymposium.org/popets/2020/popets-2020-0003.pdf.
//
// Every advertisement is counted, Apple or not, so an empty capture can be told apart from a watcher
// that saw nothing at all. No Bluetooth address is written: each is replaced by a tag keyed with a
// random value made for this run and never stored, so lines from one sender can be grouped within
// the capture and the tag means nothing outside it.
//
// The capture goes to <local data folder>\phase0\, which is %LOCALAPPDATA%\Earshot\phase0 or, with
// EARSHOT_DATA_ROOT set, <root>\Local\Earshot\phase0 (the same layout as Infra\Paths.cs). It stays on
// this PC and out of the repository.
internal static class Program
{
    private const ushort AppleCompanyId = 0x004C;
    private const byte ProximityPairingType = 0x07;

    private static readonly object Gate = new();
    private static readonly long[] AppleTypeCounts = new long[256];
    private static readonly byte[] TagKey = RandomNumberGenerator.GetBytes(32);
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static StreamWriter? writer;
    private static long totalCount;
    private static long appleCount;
    private static long proximityCount;
    private static long malformedAppleCount;

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.Error.WriteLine("This probe needs Windows 10 version 2004 or later.");
            return 2;
        }

        // No argument: the guided sitting. --free: notes typed at will. --seconds N: listen N seconds
        // with no input at all, which is how the watcher is checked on a PC with nobody at it.
        int seconds = 0;
        int at = Array.FindIndex(args, a => string.Equals(a, "--seconds", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && (at + 1 >= args.Length || !int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 3600))
        {
            Console.Error.WriteLine("--seconds needs a whole number from 1 to 3600.");
            return 2;
        }

        bool unguided = args.Contains("--free", StringComparer.OrdinalIgnoreCase);
        return Run(unguided, seconds);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static int Run(bool unguided, int seconds)
    {
        string folder = Path.Combine(LocalDataFolder(), "phase0");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "capture-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".log");
        writer = new StreamWriter(file, append: false, new UTF8Encoding(false)) { AutoFlush = true };

        Console.WriteLine("Earshot phase 0 capture. Listening only; nothing is sent to any device.");
        Console.WriteLine("Capture file: " + file);
        string mode = seconds > 0 ? "timed" : unguided ? "free" : "guided";
        Write("START", "probe=1 mode=" + mode + " os=" + Environment.OSVersion.Version);

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += OnReceived;
        watcher.Stopped += (_, e) => Write("STOPPED", "error=" + e.Error);
        watcher.Start();
        Thread.Sleep(2000);
        Write("STATUS", "watcher=" + watcher.Status);
        if (watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
        {
            Console.Error.WriteLine("The watcher did not start (status " + watcher.Status + "). Is Bluetooth on?");
            Finish(watcher);
            return 1;
        }

        using var counter = new Timer(_ => WriteCounts(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        if (seconds > 0)
        {
            Hold(seconds);
        }
        else if (unguided)
        {
            Console.WriteLine("Free mode: type a note and press Enter to mark the log; an empty line ends the capture.");
            string? line;
            while (!string.IsNullOrEmpty(line = Console.ReadLine()))
            {
                Mark("note", Clean(line));
            }
        }
        else
        {
            Guided();
        }

        Finish(watcher);
        Console.WriteLine();
        Console.WriteLine("Done. The capture is at " + file);
        return 0;
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void Finish(BluetoothLEAdvertisementWatcher watcher)
    {
        watcher.Stop();
        WriteCounts();
        var types = new StringBuilder();
        for (int t = 0; t < AppleTypeCounts.Length; t++)
        {
            long n = Interlocked.Read(ref AppleTypeCounts[t]);
            if (n > 0)
            {
                types.Append(CultureInfo.InvariantCulture, $" 0x{t:X2}={n}");
            }
        }

        Write("APPLE-TYPES", types.Length == 0 ? "none" : types.ToString().Trim());
        Write("END", "elapsed_ms=" + Clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        lock (Gate)
        {
            writer?.Dispose();
            writer = null;
        }
    }

    private static void Guided()
    {
        Step("1", "Put both AirPods in the case, close the lid, and set the case next to the PC.", 30);
        Step("2", "Open the case lid next to the PC.", 20);
        Ask("2", "Read the iPhone battery widget. Type left, right and case, with spaces (for example 80 80 60):");
        Hold(20);
        Step("3a", "Close the lid.", 20);
        Step("3b", "Open the lid again.", 20);
        Ask("4", "Take one bud out of the case. Type L or R for the bud you took:");
        Hold(20);
        Step("4b", "Put that bud back in the case.", 20);
        Step("5", "Put both buds in your ears and play audio from the iPhone. Wait until it is playing.", 120);
        Step("6", "Stop the iPhone. Connect the AirPods to this PC as you usually do, and play audio from this PC. Wait until it is playing.", 120);
        Ask("6b", "Take one bud out of your ear. Type L or R for the bud you took:");
        Hold(20);
        Step("6c", "Put that bud back in your ear.", 20);
        Console.WriteLine("All steps done. Stop playing and disconnect the AirPods from this PC as you usually do.");
    }

    private static void Step(string id, string instruction, int holdSeconds)
    {
        Console.WriteLine();
        Console.WriteLine("Step " + id + ": " + instruction);
        Console.Write("Press Enter when done. ");
        Console.ReadLine();
        Mark(id, instruction);
        Hold(holdSeconds);
    }

    private static void Ask(string id, string question)
    {
        Console.WriteLine();
        Console.WriteLine("Step " + id + ": " + question);
        string answer = Clean(Console.ReadLine() ?? string.Empty);
        Mark(id, "answer=" + answer);
    }

    private static void Hold(int seconds)
    {
        for (int left = seconds; left > 0; left--)
        {
            Console.Write(string.Create(CultureInfo.InvariantCulture,
                $"\r  Hold still: {left,3} s. Seen so far: {Interlocked.Read(ref totalCount)} adverts, {Interlocked.Read(ref appleCount)} Apple, {Interlocked.Read(ref proximityCount)} proximity.   "));
            Thread.Sleep(1000);
        }

        Console.WriteLine();
    }

    private static void Mark(string id, string text) => Write("MARK", id + "\t" + text);

    // Keeps a typed note to letters, digits and spaces, so nothing typed can break a log line.
    private static string Clean(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (sb.Length >= 60)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c == ' ')
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        Interlocked.Increment(ref totalCount);
        foreach (BluetoothLEManufacturerData md in e.Advertisement.ManufacturerData)
        {
            if (md.CompanyId != AppleCompanyId)
            {
                continue;
            }

            Interlocked.Increment(ref appleCount);
            byte[] data = new byte[md.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(md.Data))
            {
                reader.ReadBytes(data);
            }

            int i = 0;
            while (i < data.Length)
            {
                if (i + 2 > data.Length || i + 2 + data[i + 1] > data.Length)
                {
                    Interlocked.Increment(ref malformedAppleCount);
                    break;
                }

                byte type = data[i];
                int length = data[i + 1];
                Interlocked.Increment(ref AppleTypeCounts[type]);
                if (type == ProximityPairingType)
                {
                    Interlocked.Increment(ref proximityCount);
                    Write("ADV", string.Create(CultureInfo.InvariantCulture,
                        $"rssi={e.RawSignalStrengthInDBm}\ttag={Tag(e.BluetoothAddress)}\tkind={e.AdvertisementType}\tlen={length}\t{Convert.ToHexString(data, i + 2, length)}"));
                }

                i += 2 + length;
            }
        }
    }

    private static string Tag(ulong address)
    {
        byte[] bytes = BitConverter.GetBytes(address);
        return Convert.ToHexString(HMACSHA256.HashData(TagKey, bytes), 0, 4);
    }

    private static void WriteCounts() => Write("COUNT", string.Create(CultureInfo.InvariantCulture,
        $"all={Interlocked.Read(ref totalCount)} apple={Interlocked.Read(ref appleCount)} proximity={Interlocked.Read(ref proximityCount)} apple_malformed={Interlocked.Read(ref malformedAppleCount)}"));

    private static void Write(string kind, string text)
    {
        string line = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "\t"
            + Clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "\t" + kind + "\t" + text;
        lock (Gate)
        {
            writer?.WriteLine(line);
        }
    }

    private static string LocalDataFolder()
    {
        string? root = Environment.GetEnvironmentVariable("EARSHOT_DATA_ROOT");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot")
            : Path.Combine(root, "Local", "Earshot");
    }
}
