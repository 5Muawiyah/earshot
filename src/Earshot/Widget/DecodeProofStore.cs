using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Earshot.Contracts;

namespace Earshot.Widget;

// Where the decode table lives at run time. Built once from every set-up record on disk, rebuilt after each
// new one, and the only source of a ProximityDecodeTable anywhere in the widget: there is no constant to read.
// Also keeps the one observation that is not derived from records, whether the AirPods keep broadcasting
// while this PC plays to them, in widget\proof.json.
//
// Table and Result are safe to read from any thread. AddRecord and NoteOwnedWhileThisPcRenders are for the
// UI thread.
internal sealed class DecodeProofStore
{
    // Twenty owned messages over at least a minute, with no gap longer than BroadcastGapReset between two of
    // them, while this PC renders to the AirPods. The figures are design choices: an observation that the
    // broadcast continues, not a fact about the device.
    public const int BroadcastProofMessages = 20;
    public static readonly TimeSpan BroadcastProofSpan = TimeSpan.FromSeconds(60);

    // A longer gap between two counted messages ends the run and starts a new count: twenty messages spread
    // over days would say nothing about the broadcast continuing during one stretch of playing.
    public static readonly TimeSpan BroadcastGapReset = TimeSpan.FromSeconds(20);

    // Between the first note and the proof the observation is written at most this often.
    private static readonly TimeSpan ObservationWriteInterval = TimeSpan.FromSeconds(30);

    private readonly BatterySetupStore _setups;
    private readonly string _proofFile;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly List<BatterySetupRecord> _records;

    private volatile DecodeProofResult _result;
    private int _ownedMessages;
    private DateTimeOffset? _firstAt;
    private DateTimeOffset? _lastAt;
    private volatile bool _broadcastProved;
    private DateTimeOffset? _lastObservationWriteAt;

    public DecodeProofStore(string setupFolder, string proofFile, ILog log, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(proofFile);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);

        _setups = new BatterySetupStore(setupFolder, log);
        _proofFile = proofFile;
        _log = log;
        _time = time;
        _records = new List<BatterySetupRecord>(_setups.LoadAll());
        _result = DecodeProof.Evaluate(_records);
        ReadObservation();
        WriteProofFile();
    }

    public ProximityDecodeTable Table => _result.Table;

    public DecodeProofResult Result => _result;

    // The same two reads as the properties above, as methods, for a caller that takes a function.
    public ProximityDecodeTable ReadTable() => _result.Table;

    public bool? ReadBroadcast() => BroadcastsWhilePlayingFromThisPc;

    public BatterySetupStore Setups => _setups;

    public IReadOnlyList<BatterySetupRecord> Records => _records;

    public BatterySetupRecord? Newest => _records.Count == 0 ? null : _records[^1];

    // True once proved; null before. Never false: an unproved observation is not a negative one.
    public bool? BroadcastsWhilePlayingFromThisPc => _broadcastProved ? true : null;

    // How many owned messages the current run of observation has counted, for tests.
    internal int OwnedMessagesCounted => _ownedMessages;

    public void AddRecord(BatterySetupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        DecodeProofResult before = _result;
        _records.Add(record);
        DecodeProofResult after = DecodeProof.Evaluate(_records);
        _result = after;

        foreach (DecodeField field in Enum.GetValues<DecodeField>())
        {
            FieldProof was = before.Fields[field];
            FieldProof now = after.Fields[field];
            if (was.Status == now.Status)
            {
                continue;
            }

            string line = "Decode proof: " + field + " " + now.Status + " (" +
                now.Agree.ToString(CultureInfo.InvariantCulture) + " agree, " + now.Disagree.ToString(CultureInfo.InvariantCulture) + " disagree).";
            if (now.Status == FieldProofStatus.Withdrawn)
            {
                _log.Warn(line);
            }
            else
            {
                _log.Info(line);
            }
        }

        if (before.Table.HighNibbleIsRight is not null && after.Fields[DecodeField.HighNibbleIsRight].Status == FieldProofStatus.Withdrawn)
        {
            _log.Warn("Decode proof: bud order withdrawn; " + record.FileName + " disagrees with the proved order.");
        }

        WriteProofFile();
    }

    // One owned message arrived while Core Audio says this PC renders to the AirPods.
    public void NoteOwnedWhileThisPcRenders(DateTimeOffset at)
    {
        if (_broadcastProved)
        {
            return;
        }

        if (_lastAt is DateTimeOffset last && (at - last > BroadcastGapReset || at < last))
        {
            _ownedMessages = 0;
            _firstAt = null;
        }

        _firstAt ??= at;
        _lastAt = at;
        _ownedMessages++;

        bool firstNote = _ownedMessages == 1;
        if (_ownedMessages >= BroadcastProofMessages && at - _firstAt.Value >= BroadcastProofSpan)
        {
            _broadcastProved = true;
            _log.Info(
                "Broadcast while playing from this PC: " + _ownedMessages.ToString(CultureInfo.InvariantCulture) + " owned messages over " +
                ((int)(at - _firstAt.Value).TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s; proved.");
            WriteProofFile();
            return;
        }

        if (firstNote || _lastObservationWriteAt is not DateTimeOffset written || at - written >= ObservationWriteInterval)
        {
            WriteProofFile();
            _lastObservationWriteAt = at;
        }
    }

    private void ReadObservation()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_proofFile);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (IOException ex)
        {
            _log.Warn("The decode proof summary could not be read, so the broadcast observation starts again (0x" + ex.HResult.ToString("X8") + "): " + _proofFile);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The decode proof summary could not be read, so the broadcast observation starts again (0x" + ex.HResult.ToString("X8") + "): " + _proofFile);
            return;
        }

        DecodeProofFile? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, SetupJsonContext.Default.DecodeProofFile);
        }
        catch (JsonException ex)
        {
            _log.Warn("The decode proof summary is not valid, so the broadcast observation starts again (" + ex.Message + "): " + _proofFile);
            return;
        }

        BroadcastObservation? observation = file?.BroadcastWhilePlayingFromThisPc;
        if (file is null || file.SchemaVersion != 1 || observation is null || observation.OwnedMessages < 0)
        {
            _log.Warn("The decode proof summary holds no usable observation, so it starts again: " + _proofFile);
            return;
        }

        _ownedMessages = observation.OwnedMessages;
        _firstAt = observation.FirstAtUtc;
        _lastAt = observation.LastAtUtc;
        _broadcastProved = observation.Proved;
    }

    private void WriteProofFile()
    {
        DecodeProofResult result = _result;
        var fields = new Dictionary<string, ProofFileField>();
        foreach (DecodeField field in Enum.GetValues<DecodeField>())
        {
            FieldProof proof = result.Fields[field];
            fields[CamelCase(field.ToString())] = new ProofFileField(proof.Status, ValueOf(field, result.Table), proof.Agree, proof.Disagree);
        }

        var file = new DecodeProofFile(
            1,
            _time.GetUtcNow(),
            _records.Count,
            fields,
            new BroadcastObservation(_ownedMessages, _firstAt, _lastAt, _broadcastProved),
            result.Notes);

        string? folder = Path.GetDirectoryName(_proofFile);
        string temp = _proofFile + ".tmp";
        try
        {
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, SetupJsonContext.Default.DecodeProofFile);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _proofFile, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.Warn("The decode proof summary could not be saved (0x" + ex.HResult.ToString("X8") + "): " + _proofFile);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The decode proof summary could not be saved (0x" + ex.HResult.ToString("X8") + "): " + _proofFile);
        }
    }

    private static JsonValue? ValueOf(DecodeField field, ProximityDecodeTable table) => field switch
    {
        DecodeField.HighNibbleIsRight => table.HighNibbleIsRight is bool b ? JsonValue.Create(b) : null,
        DecodeField.FlipBit => table.FlipBit is int f ? JsonValue.Create(f) : null,
        DecodeField.CaseChargingBit => table.CaseChargingBit is int c ? JsonValue.Create(c) : null,
        DecodeField.RightChargingBit => table.RightChargingBit is int r ? JsonValue.Create(r) : null,
        DecodeField.LeftChargingBit => table.LeftChargingBit is int l ? JsonValue.Create(l) : null,
        _ => null,
    };

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
