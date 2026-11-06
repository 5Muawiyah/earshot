using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Earshot.Tests.Icons;

// Writes src\Earshot\Icon\MingCuteIcons.g.cs from the vendored SVG files listed in third_party\MingCute\manifest.json: each file's
// path data is converted by SvgPathConverter and written as constant arrays of grid coordinates (24 unit grid, four decimals)
// and GDI+ point types. The AirPods icon is also cut into its left and right bud. Nothing here ships; the product carries the
// output only.
internal static class MingCuteIconGenerator
{
    // The icon whose two earbuds are cut apart, and the vertical line down the middle of its 24 unit grid that no figure may cross.
    private const string PairIcon = "media/airpods";
    private const double Middle = 12.0;
    private const double MiddleTolerance = 0.01;

    internal const string Header =
        "// Derived from MingCute Icons 3.1.0, https://github.com/mingcute-design/mingcute-icons, commit\n" +
        "// 9dc7d3ad074fb16477096a6a8109a6467d0e2fe3, licensed under the Apache License, Version 2.0 (licences\\MingCute\\LICENSE in\n" +
        "// the release, third_party\\MingCute\\LICENSE in the source).\n" +
        "// Changed by Earshot: the SVG path data of each file named below is converted to GDI+ path points (elliptical arcs to cubic\n" +
        "// Bezier curves per the SVG implementation notes, quadratic to cubic); the AirPods icon is also split into its left and right\n" +
        "// buds. No shape is otherwise changed. The source files and their hashes are listed in third_party\\MingCute\\manifest.json.\n" +
        "// Written by tests\\Earshot.Tests\\Icons\\MingCuteIconGenerator.cs; edit the generator, not this file.\n";

    internal sealed record Outline(string Name, string Property, ConvertedPath Path);

    // "regular/system/settings_3" is the property RegularSystemSettings3.
    internal static string PropertyName(string name)
    {
        var sb = new StringBuilder();
        foreach (string part in name.Split('/', '_', ':'))
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
            }
        }

        return sb.ToString();
    }

    // Every outline of the manifest in the order the file lists them (sorted by name), with the two buds after the pair.
    internal static List<Outline> Outlines(JsonElement manifest, Func<string, string> readSvg)
    {
        string[] names = manifest.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString()!)
            .Select(p => p["assets/svg/core/".Length..^".svg".Length])
            .Order(StringComparer.Ordinal)
            .ToArray();
        var outlines = new List<Outline>();
        foreach (string name in names)
        {
            ConvertedPath path = SvgPathConverter.Convert(SvgPathConverter.PathData(readSvg(name)));
            outlines.Add(new Outline(name, PropertyName(name), path));
            if (name.EndsWith(PairIcon, StringComparison.Ordinal))
            {
                string style = name[..name.IndexOf('/', StringComparison.Ordinal)];
                (ConvertedPath left, ConvertedPath right) = SplitBuds(path, name);
                outlines.Add(new Outline(style + "/media/airpods:left", PropertyName(style + "/media/airpods/left"), left));
                outlines.Add(new Outline(style + "/media/airpods:right", PropertyName(style + "/media/airpods/right"), right));
            }
        }

        return outlines;
    }

    // The figures wholly left of the middle make the left bud, those wholly right of it the right bud, each in file order.
    // A figure across the middle would mean the cut is not clean, so it is refused.
    internal static (ConvertedPath Left, ConvertedPath Right) SplitBuds(ConvertedPath pair, string name)
    {
        var left = new ConvertedPath();
        var right = new ConvertedPath();
        for (int i = 0; i < pair.Subpaths.Count; i++)
        {
            ConvertedSubpath figure = pair.Subpaths[i];
            (double minX, _, double maxX, _) = SvgPathConverter.Bounds([figure]);
            if (minX < Middle - MiddleTolerance && maxX > Middle + MiddleTolerance)
            {
                throw new InvalidOperationException(name + ": figure " + (i + 1) + " runs from " + minX + " to " + maxX + ", across x = " + Middle + ".");
            }

            (maxX <= Middle + MiddleTolerance ? left : right).Subpaths.Add(figure);
        }

        if (left.Subpaths.Count == 0 || right.Subpaths.Count == 0)
        {
            throw new InvalidOperationException(name + ": a bud has no figure.");
        }

        return (left, right);
    }

    internal static string Generate(JsonElement manifest, Func<string, string> readSvg)
    {
        List<Outline> outlines = Outlines(manifest, readSvg);
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.Append('\n');
        sb.Append("namespace Earshot.Icons;\n\n");
        sb.Append("internal static partial class MingCuteIcons\n{\n");
        for (int i = 0; i < outlines.Count; i++)
        {
            Outline outline = outlines[i];
            (List<float> points, List<byte> types) = Flatten(outline.Path);
            sb.Append("    // ").Append(outline.Name).Append('\n');
            sb.Append("    internal static IconOutline ").Append(outline.Property)
                .Append(" => new(\"").Append(outline.Name).Append("\", ").Append(outline.Property).Append("Points, ").Append(outline.Property).Append("Types);\n\n");
            sb.Append("    private static ReadOnlySpan<float> ").Append(outline.Property).Append("Points =>\n    [\n");
            AppendRows(sb, points.Select(Literal).ToList(), 8);
            sb.Append("    ];\n\n");
            sb.Append("    private static ReadOnlySpan<byte> ").Append(outline.Property).Append("Types =>\n    [\n");
            AppendRows(sb, types.Select(t => t.ToString(CultureInfo.InvariantCulture)).ToList(), 24);
            sb.Append("    ];\n");
            if (i + 1 < outlines.Count)
            {
                sb.Append('\n');
            }
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    // The outline's points as x, y, x, y ... and one type per point, as GraphicsPath(PointF[], byte[]) takes them.
    internal static (List<float> Points, List<byte> Types) Flatten(ConvertedPath path)
    {
        var points = new List<float>();
        var types = new List<byte>();
        foreach (ConvertedSubpath figure in path.Subpaths)
        {
            foreach (PathPoint p in figure.Points)
            {
                points.Add(Rounded(p.X));
                points.Add(Rounded(p.Y));
            }

            types.AddRange(figure.Types);
        }

        return (points, types);
    }

    // Four decimals, and no negative zero.
    internal static float Rounded(double value)
    {
        double r = Math.Round(value, 4, MidpointRounding.ToEven);
        return r == 0 ? 0f : (float)r;
    }

    private static string Literal(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";

    private static void AppendRows(StringBuilder sb, List<string> items, int perRow)
    {
        for (int i = 0; i < items.Count; i += perRow)
        {
            sb.Append("        ").Append(string.Join(", ", items.Skip(i).Take(perRow)));
            sb.Append(",\n");
        }
    }
}
