using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DBsync.Tray.Icons;
using DBsync.Tray.Theming;

// DBsync.Tray.Icons.Icon is the WPF glyph control; this file needs GDI+'s Icon, which is a
// Win32 HICON wrapper. Aliasing keeps both readable.
using DrawingIcon = System.Drawing.Icon;

namespace DBsync.Tray.Tray;

/// <summary>
/// Draws the brand glyph into Win32 icons at a set of rotations.
/// <para>
/// A tray icon is an HICON, not a visual tree, so the rotating glyph the design asks for cannot
/// be animated declaratively — it has to be a sequence of pre-rendered frames swapped on a timer.
/// Rendering them once up front keeps the animation off the hot path.
/// </para>
/// </summary>
public sealed class GlyphIconFrames : IDisposable
{
    /// <summary>
    /// 20 frames at 160ms each is one turn per 3.2s, matching the design's rotation period
    /// exactly while staying cheap — each frame is a 32px bitmap.
    /// </summary>
    public const int FrameCount = 20;

    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(160);

    private readonly List<DrawingIcon> _frames = new(FrameCount);
    private bool _disposed;

    private GlyphIconFrames(ThemeVariant shellTheme) => ShellTheme = shellTheme;

    /// <summary>Which taskbar ground these frames were drawn for.</summary>
    public ThemeVariant ShellTheme { get; }

    public DrawingIcon this[int index] => _frames[((index % FrameCount) + FrameCount) % FrameCount];

    /// <summary>The upright frame, for the static (not syncing) state.</summary>
    public DrawingIcon Still => _frames[0];

    /// <summary>
    /// Renders every frame for the given taskbar ground. The glyph keeps the brand hue but takes
    /// the ramp step that contrasts with the taskbar — accent-400 on a dark one, accent-700 on a
    /// light one — which is what the design means by swapping the light/dark variant.
    /// </summary>
    public static GlyphIconFrames Render(ThemeVariant shellTheme)
    {
        var frames = new GlyphIconFrames(shellTheme);
        var colour = shellTheme == ThemeVariant.Light
            ? Color.FromArgb(0xFF, 0x5D, 0x52, 0x94)   // accent-700
            : Color.FromArgb(0xFF, 0xB5, 0xAB, 0xFC);  // accent-400

        var glyph = IconGlyphs.Glyph(IconKey.ShuffleSimple);

        for (var i = 0; i < FrameCount; i++)
        {
            var angle = 360f * i / FrameCount;
            frames._frames.Add(RenderFrame(glyph, colour, angle));
        }

        return frames;
    }

    private static DrawingIcon RenderFrame(string glyph, Color colour, float angle)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // Rotate about the centre so the glyph spins in place rather than orbiting.
            graphics.TranslateTransform(size / 2f, size / 2f);
            graphics.RotateTransform(angle);
            graphics.TranslateTransform(-size / 2f, -size / 2f);

            // 26px in a 32px bitmap. A rotating glyph needs the corners clear so it does not
            // clip at 45°, but much smaller than this and the icon reads as undersized next to
            // the shell's own tray icons, which fill their box.
            using var font = new Font("Segoe Fluent Icons", 26f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(colour);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
        }

        // GetHicon hands back a handle we own; copy it into a managed Icon and release it, or the
        // handle leaks for every frame of every re-render.
        var handle = bitmap.GetHicon();
        try
        {
            using var unowned = DrawingIcon.FromHandle(handle);
            return (DrawingIcon)unowned.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var frame in _frames) frame.Dispose();
        _frames.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
