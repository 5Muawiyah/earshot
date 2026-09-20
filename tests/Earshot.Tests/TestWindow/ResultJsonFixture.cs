using System.Text;
using System.Text.Json.Nodes;

namespace Earshot.Tests.TestWindow;

// Builds result.json text for tests, the same shapes Complete-LiveTestRun writes (and the
// quirks PowerShell 5.1 adds: a list of one becomes a bare object, not a one-element array).
internal sealed class ResultJsonFixture
{
    private readonly JsonObject _root = new();
    private readonly JsonArray _criteria = new();
    private readonly JsonArray _findings = new();
    private bool _criteriaAsSingleObject;
    private bool _findingsAsSingleObject;

    internal ResultJsonFixture(string test, string overall)
    {
        _root["test"] = test;
        _root["overall"] = overall;
    }

    internal ResultJsonFixture WithCriterion(string id, string outcome, string criterion = "criterion text", string detail = "")
    {
        _criteria.Add(new JsonObject { ["id"] = id, ["criterion"] = criterion, ["outcome"] = outcome, ["detail"] = detail, ["utc"] = "2026-09-20T00:00:00.000Z" });
        return this;
    }

    internal ResultJsonFixture WithFinding(string name, string? value, string detail = "")
    {
        _findings.Add(new JsonObject { ["name"] = name, ["value"] = value, ["detail"] = detail, ["utc"] = "2026-09-20T00:00:00.000Z" });
        return this;
    }

    internal ResultJsonFixture WithCriteriaAsSingleObject()
    {
        _criteriaAsSingleObject = true;
        return this;
    }

    internal ResultJsonFixture WithFindingsAsSingleObject()
    {
        _findingsAsSingleObject = true;
        return this;
    }

    internal ResultJsonFixture WithExe(string exe)
    {
        _root["exe"] = exe;
        return this;
    }

    internal ResultJsonFixture WithStartedUtc(string startedUtc)
    {
        _root["startedUtc"] = startedUtc;
        return this;
    }

    internal ResultJsonFixture WithFinishedUtc(string finishedUtc)
    {
        _root["finishedUtc"] = finishedUtc;
        return this;
    }

    internal ResultJsonFixture WithSteps(int count)
    {
        var steps = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            steps.Add(new JsonObject { ["index"] = i + 1 });
        }

        _root["steps"] = steps;
        return this;
    }

    internal string Build()
    {
        if (_criteria.Count > 0)
        {
            _root["criteria"] = _criteriaAsSingleObject && _criteria.Count == 1
                ? _criteria[0]!.DeepClone()
                : _criteria.DeepClone();
        }

        if (_findings.Count > 0)
        {
            _root["findings"] = _findingsAsSingleObject && _findings.Count == 1
                ? _findings[0]!.DeepClone()
                : _findings.DeepClone();
        }

        return _root.ToJsonString();
    }

    internal static void WriteTo(string path, string json, bool byteOrderMark = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Encoding encoding = new UTF8Encoding(byteOrderMark);
        File.WriteAllText(path, json, encoding);
    }
}
