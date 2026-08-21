using System.Drawing;
using System.Runtime.InteropServices;

namespace CCAutoApprove.App.Services;

internal static class TrayStatusIconPalette
{
    private static readonly Icon Running = Create(Color.SeaGreen, "✓");
    private static readonly Icon Paused = Create(Color.DimGray, "Ⅱ");
    private static readonly Icon Error = Create(Color.Firebrick, "!");

    public static Icon Get(TrayIconKind kind) => kind switch
    {
        TrayIconKind.Running => Running,
        TrayIconKind.Paused => Paused,
        _ => Error
    };

    private static Icon Create(Color color, string glyph)
    {
        using var bitmap = new Bitmap(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var fill = new SolidBrush(color))
        using (var text = new SolidBrush(Color.White))
        using (var font = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.FillEllipse(fill, 1, 1, 14, 14);
            SizeF size = graphics.MeasureString(glyph, font);
            graphics.DrawString(glyph, font, text, (16 - size.Width) / 2, (16 - size.Height) / 2);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
