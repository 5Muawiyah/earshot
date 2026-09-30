using System.Security.Cryptography;
using System.Text;
using Earshot.Boot.Gate;

namespace Earshot.Tests.Update;

// A small install tree in a temporary folder: the files a release lists and the Earshot.files.json that records their
// SHA-256 values, as the release publishes it. The real check and the real reader run against it, so a test that holds a
// file open or changes one meets what Windows really does. Nothing here is Program Files.
internal sealed class TempInstallTree : IDisposable
{
    private readonly bool _ownsTemp;
    private readonly TempFolder _temp;

    public TempInstallTree(TempFolder? temp = null, string name = "Earshot")
    {
        _ownsTemp = temp is null;
        _temp = temp ?? new TempFolder();
        Folder = _temp.File(name);
        Directory.CreateDirectory(Path.Combine(Folder, "runtimes"));
        Write("Earshot.exe", "exe 1.2.0");
        Write("Earshot.dll", "dll 1.2.0");
        Write(Path.Combine("runtimes", "native.txt"), "native 1.2.0");
        string[] listed = ["Earshot.exe", "Earshot.dll", "runtimes/native.txt"];
        IEnumerable<string> entries = listed.Select(relative =>
            "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(File_(relative)))) + "\" }");
        System.IO.File.WriteAllText(
            Path.Combine(Folder, FileManifest.FileName),
            "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n", Encoding.UTF8);
    }

    public string Folder { get; }

    // The full path of a file in the tree, from its relative name.
    public string File(string relative) => File_(relative);

    public void Write(string relative, string text) => System.IO.File.WriteAllText(File_(relative), text);

    private string File_(string relative) => Path.Combine(Folder, relative.Replace('/', '\\'));

    public void Dispose()
    {
        if (_ownsTemp)
        {
            _temp.Dispose();
        }
    }
}
