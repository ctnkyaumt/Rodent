using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Rodent.App.Controls;

/// <summary>Where a button's callout label attaches, in canvas coordinates.</summary>
public sealed record Anchor(int Index, double X, double Y, bool RightSide);

/// <summary>Rendered device art plus a canvas coordinate system shared with overlay labels.</summary>
public sealed record Art(DrawingImage Image, double Width, double Height, IReadOnlyList<Anchor> Anchors);

/// <summary>
/// Loads a libratbag/Piper device SVG. Strips the "LEDs" group so its baked leader
/// lines (which have no label here — lighting lives on its own tab) don't dangle,
/// keeps the button leaders, widens the viewBox for label room, renders, and reports
/// each button's leader anchor so labels can be overlaid on the leader endpoints.
/// </summary>
public static class SvgArt
{
    private const double LabelRoom = 155;

    private static readonly Regex LeaderRect =
        new(@"<rect\b[^>]*?id=""button(\d+)-leader""[^>]*?/>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrX = new(@"\sx=""(-?[\d.]+)""", RegexOptions.Compiled);
    private static readonly Regex AttrY = new(@"\sy=""(-?[\d.]+)""", RegexOptions.Compiled);
    private static readonly Regex Flip = new(@"transform=""[^""]*scale\(-1\)", RegexOptions.Compiled);
    private static readonly Regex ViewBox =
        new(@"viewBox=""([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)""", RegexOptions.Compiled);
    private static readonly Regex WidthAttr = new(@"\swidth=""[\d.]+""", RegexOptions.Compiled);
    private static readonly Regex HeightAttr = new(@"\sheight=""[\d.]+""", RegexOptions.Compiled);
    private static readonly Regex AnyRect = new(@"<rect\b[^>]*?/>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrW = new(@"\swidth=""([\d.]+)""", RegexOptions.Compiled);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    private static string S(double d) => d.ToString(CultureInfo.InvariantCulture);

    public static Art? Load(ushort vendorId, ushort productId)
    {
        uint key = ((uint)vendorId << 16) | productId;
        if (!DeviceArt.SvgByVidPid.TryGetValue(key, out var asset))
            return null;

        string svg = ReadAsset(asset);
        var vb = ViewBox.Match(svg);
        if (!vb.Success) return null;
        double minX = D(vb.Groups[1].Value), minY = D(vb.Groups[2].Value);
        double w = D(vb.Groups[3].Value), h = D(vb.Groups[4].Value);
        double centerX = minX + w / 2;

        var raw = new List<Anchor>();
        foreach (Match m in LeaderRect.Matches(svg))
        {
            var mx = AttrX.Match(m.Value); var my = AttrY.Match(m.Value);
            if (!mx.Success || !my.Success) continue;
            double x = D(mx.Groups[1].Value), y = D(my.Groups[1].Value);
            if (Flip.IsMatch(m.Value)) { x = -x; y = -y; }
            raw.Add(new Anchor(int.Parse(m.Groups[1].Value) + 1, x, y, x >= centerX));
        }

        double loX = minX, hiX = minX + w;
        foreach (var a in raw)
        {
            if (a.RightSide) hiX = Math.Max(hiX, a.X + LabelRoom);
            else loX = Math.Min(loX, a.X - LabelRoom);
        }
        double w2 = hiX - loX;

        svg = ViewBox.Replace(svg, $"viewBox=\"{S(loX)} {S(minY)} {S(w2)} {S(h)}\"", 1);
        svg = WidthAttr.Replace(svg, $" width=\"{S(w2)}\"", 1);
        svg = HeightAttr.Replace(svg, $" height=\"{S(h)}\"", 1);

        // Remove the LEDs group so its label-less leader lines don't dangle.
        svg = StripGroup(svg, "LEDs");

        // The small square leader-start markers are near-black (#2e3436) and read
        // as specks on the light mouse body; recolor them to the accent.
        svg = AnyRect.Replace(svg, m =>
        {
            if (!m.Value.Contains("#2e3436")) return m.Value;
            var wm = AttrW.Match(m.Value);
            return wm.Success &&
                   double.TryParse(wm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double wv) &&
                   wv is >= 4 and <= 10
                ? m.Value.Replace("#2e3436", "#3584e4")
                : m.Value;
        });

        var settings = new WpfDrawingSettings { IncludeRuntime = true, TextAsGeometry = false };
        using var reader = new FileSvgReader(settings);
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
        DrawingGroup? drawing = reader.Read(ms);
        if (drawing == null) return null;

        var anchors = raw.Select(a => new Anchor(a.Index, a.X - loX, a.Y - minY, a.RightSide)).ToList();
        return new Art(new DrawingImage(drawing), w2, h, anchors);
    }

    /// <summary>Remove a &lt;g id="name"&gt;…&lt;/g&gt; group, handling nested groups.</summary>
    private static string StripGroup(string svg, string id)
    {
        var m = Regex.Match(svg, $@"<g\b[^>]*id=""{id}""[^>]*>");
        if (!m.Success) return svg;
        int start = m.Index, pos = m.Index + m.Length, depth = 1;
        var tag = new Regex(@"<g\b|</g>");
        while (depth > 0)
        {
            var t = tag.Match(svg, pos);
            if (!t.Success) return svg;
            depth += t.Value == "</g>" ? -1 : 1;
            pos = t.Index + t.Length;
        }
        return svg.Remove(start, pos - start);
    }

    private static string ReadAsset(string file)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)!.Stream;
        using var sr = new StreamReader(stream);
        return sr.ReadToEnd();
    }
}
