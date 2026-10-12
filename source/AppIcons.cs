using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Patchwork
{
    internal static class AppIcons
    {
        internal static BitmapSource Render(Color background, Color foreground, int size)
        {
            if (size < 16 || size > 256) throw new ArgumentOutOfRangeException("size");
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                drawing.DrawRoundedRectangle(new SolidColorBrush(background), null, new Rect(0, 0, 32, 32), 9, 9);
                var ink = new SolidColorBrush(foreground);
                drawing.DrawRectangle(ink, null, new Rect(8, 6.5, 7, 19));
                drawing.DrawRectangle(ink, null, new Rect(12, 6, 11, 7));
                drawing.DrawRectangle(ink, null, new Rect(19, 19, 7, 7));
                drawing.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual); bitmap.Freeze();
            return bitmap;
        }
    }
}
