using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Icons;
using Earshot.Interop;

namespace Earshot;

// probe icon --out <folder>: renders the tray glyph in each of its four states at 96, 144 and 192 dpi, in white
// ink (for a dark taskbar) and in black ink (for a light one), and writes each as a PNG to the folder, so the
// icon can be checked by eye at every size without a tray. Each image is also loaded through the same
// single-frame ICO path the tray uses, which proves the icon it would get loads. It reads the system metric for
// the size and changes nothing but the files it writes.
//
// The size at each dpi is GetSystemMetricsForDpi(SM_CXSMICON, dpi), as the tray measures it.
// https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi
internal static partial class Program
{
    internal static readonly IReadOnlyList<uint> ProbeIconDpis = [96, 144, 192];

    internal static readonly IReadOnlyList<(string Name, Color Ink)> ProbeIconInks =
    [
        ("white", Color.White),
        ("black", Color.Black),
    ];

    internal sealed record ProbeIconFile(GlyphState State, uint Dpi, int Pixels, string Ink, string Path, int Bytes, bool IcoLoads, string? Problem);

    static partial void ProbeIcon(ProbeContext ctx, string? folder)
    {
        ctx.Handled = true;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ctx.Out.WriteLine("probe icon needs --out <folder>.");
            ctx.ExitCode = ExitCodes.Usage;
            return;
        }

        string target;
        try
        {
            target = Path.GetFullPath(folder);
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Out.WriteLine("The folder " + folder + " could not be made: " + ex.Message);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        IReadOnlyList<ProbeIconFile> files = RenderProbeIcons(target, dpi => Shell.GetSystemMetricsForDpi(Shell.SM_CXSMICON, dpi));
        WriteProbeIcons(ctx, target, files);
        ctx.ExitCode = files.Any(f => f.Bytes == 0) ? ExitCodes.IoError
            : files.Any(f => !f.IcoLoads) ? ExitCodes.Software
            : ExitCodes.Ok;
    }

    // Renders every state, dpi and ink into folder. metricFor reads SM_CXSMICON for a dpi; 0 or less falls back to
    // 16 px scaled by the dpi, as the tray does. A file that could not be written has Bytes 0 and a Problem.
    internal static IReadOnlyList<ProbeIconFile> RenderProbeIcons(string folder, Func<uint, int> metricFor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(metricFor);
        var files = new List<ProbeIconFile>();
        foreach (uint dpi in ProbeIconDpis)
        {
            int px = TrayIconFactory.SizeFor(metricFor(dpi), dpi);
            foreach (GlyphState state in Enum.GetValues<GlyphState>())
            {
                foreach ((string inkName, Color ink) in ProbeIconInks)
                {
                    string name = string.Create(CultureInfo.InvariantCulture,
                        $"earbud-{state.ToString().ToLowerInvariant()}-{dpi}dpi-{px}px-{inkName}.png");
                    string path = Path.Combine(folder, name);
                    files.Add(RenderProbeIcon(state, dpi, px, inkName, ink, path));
                }
            }
        }

        return files;
    }

    private static ProbeIconFile RenderProbeIcon(GlyphState state, uint dpi, int px, string inkName, Color ink, string path)
    {
        byte[] png;
        try
        {
            png = EarbudGlyph.RenderPng(px, state, ink);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return new ProbeIconFile(state, dpi, px, inkName, path, 0, false, "The glyph could not be drawn: " + ex.Message);
        }

        try
        {
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProbeIconFile(state, dpi, px, inkName, path, 0, false, "The file could not be written: " + ex.Message);
        }

        try
        {
            using Icon icon = IcoWriter.Load(IcoWriter.SingleFramePng(png, px), px);
            bool sized = icon.Width == px && icon.Height == px;
            return new ProbeIconFile(state, dpi, px, inkName, path, png.Length, sized,
                sized ? null : "The icon loaded at " + icon.Width + " x " + icon.Height + " px.");
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            return new ProbeIconFile(state, dpi, px, inkName, path, png.Length, false, "The icon did not load: " + ex.Message);
        }
    }

    private static void WriteProbeIcons(ProbeContext ctx, string folder, IReadOnlyList<ProbeIconFile> files)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", ProbeIconTarget);
                w.WriteString("folder", folder);
                w.WriteStartArray("files");
                foreach (ProbeIconFile file in files)
                {
                    w.WriteStartObject();
                    w.WriteString("state", file.State.ToString());
                    w.WriteNumber("dpi", file.Dpi);
                    w.WriteNumber("pixels", file.Pixels);
                    w.WriteString("ink", file.Ink);
                    w.WriteString("file", file.Path);
                    w.WriteNumber("bytes", file.Bytes);
                    w.WriteBoolean("icoLoads", file.IcoLoads);
                    w.WriteString("problem", file.Problem);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
            return;
        }

        ctx.Out.WriteLine("Folder: " + folder);
        foreach (ProbeIconFile file in files)
        {
            ctx.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {file.State,-12} {file.Dpi} dpi  {file.Pixels,2} px  {file.Ink,-5}  {Path.GetFileName(file.Path)}") +
                (file.Problem is null ? "" : "  " + file.Problem));
        }
    }
}
