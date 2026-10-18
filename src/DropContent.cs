using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace DropKitWindows
{
    public static class DropContent
    {
        public static bool HasSupportedFormat(IDataObject data)
        {
            try
            {
                if (data == null || ClipboardService.Sensitive(data)) return false;
                // Drag-over checks advertised formats only; no browser transfer.
                return new[] { DataFormats.FileDrop, DataFormats.UnicodeText, DataFormats.Text, DataFormats.Html, "text/uri-list", "UniformResourceLocatorW", "UniformResourceLocator", "text/x-moz-url", DataFormats.Bitmap, "PNG", "image/png", "DIBV5", DataFormats.Dib }
                    .Any(format => data.GetDataPresent(format));
            }
            catch { return false; }
        }
        public static Clip Read(IDataObject data, string source)
        {
            if (!HasSupportedFormat(data)) return null;
            Uri website;
            string mediaHint;
            if (PinterestMedia.TryDropSource(data, out website, out mediaHint)) return new Clip { Kind = "Text", Text = website.AbsoluteUri, Source = source, DragMediaHint = mediaHint };
            Clip clip = ClipboardService.Read(data, source);
            if (clip == null && data.GetDataPresent(DataFormats.Text))
            {
                string text = data.GetData(DataFormats.Text) as string;
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 1024 * 1024) clip = new Clip { Kind = "Text", Text = text, Source = source };
            }
            if (clip != null && clip.Kind == "Files")
            {
                clip.Files = clip.Files.Where(x => !string.IsNullOrWhiteSpace(x) && (File.Exists(x) || Directory.Exists(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (clip.Files.Length == 0) return null;
            }
            return clip;
        }
        public static Clip Preview(IDataObject data, string source)
        {
            if (!HasSupportedFormat(data)) return null;
            Uri website; string hint;
            if (PinterestMedia.TryDropSource(data, out website, out hint)) return new Clip { Kind = "Text", Text = website.AbsoluteUri, Source = source, DragMediaHint = hint };
            if (data.GetDataPresent(DataFormats.FileDrop)) return Read(data, source);
            // A shake only needs the advertised type, not PNG encoding or bitmap
            // decoding. Materialize pixels once the user actually drops them.
            if (new[] { DataFormats.Bitmap, "PNG", "image/png", "DIBV5", DataFormats.Dib }.Any(x => data.GetDataPresent(x))) return new Clip { Kind = "Image", Source = source };
            return Read(data, source);
        }
    }
}
