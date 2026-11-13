using System.Collections;
using System.Globalization;
using System.Text;
using QRCoder;

namespace MetroDom.Conductor.Api;

/// <summary>
/// Pairing links as QR codes (ADR 0003: a QR code with a per-booking token): an SVG for the
/// web remote's invite panel, and half-block text for the terminal. Medium error correction, so a
/// code on a scratched phone screen or a printed table card still scans.
/// </summary>
internal static class PairingQr
{
    /// <summary>The code's modules, quiet zone included: true is dark.</summary>
    public static IReadOnlyList<BitArray> Modules(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return [.. data.ModuleMatrix.Select(row => new BitArray(row))];
    }

    /// <summary>A standalone SVG, one unit per module, dark on white (scanners want contrast whatever the theme).</summary>
    public static string Svg(string text)
    {
        var modules = Modules(text);
        var size = modules.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!modules[y][x])
                {
                    continue;
                }

                var run = 1;
                while (x + run < size && modules[y][x + run])
                {
                    run++;
                }

                path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h{run}v1h-{run}z");
                x += run - 1;
            }
        }

        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" shape-rendering="crispEdges"><rect width="{size}" height="{size}" fill="#fff"/><path d="{path}" fill="#000"/></svg>""";
    }

    /// <summary>The code as text for a terminal, two module rows per line with half blocks.</summary>
    public static string Text(string text)
    {
        var modules = Modules(text);
        var size = modules.Count;
        var lines = new StringBuilder();
        for (var y = 0; y < size; y += 2)
        {
            for (var x = 0; x < size; x++)
            {
                var top = modules[y][x];
                var bottom = y + 1 < size && modules[y + 1][x];

                // Light modules are drawn, dark ones left blank: reads right on a dark terminal.
                lines.Append((top, bottom) switch
                {
                    (false, false) => '█',
                    (false, true) => '▀',
                    (true, false) => '▄',
                    _ => ' ',
                });
            }

            lines.Append('\n');
        }

        return lines.ToString();
    }
}
