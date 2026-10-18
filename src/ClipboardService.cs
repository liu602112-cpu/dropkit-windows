using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DropKitWindows
{
    public static class ClipboardService
    {
        public const string OwnFormat = "DropKit.Windows.Internal";
        public static bool ExcludedSource(string source, string exclusions)
        {
            return (exclusions ?? "").Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(x => string.Equals(x.Trim().Replace(".exe", ""), source, StringComparison.OrdinalIgnoreCase));
        }
        public static bool Sensitive(IDataObject data)
        {
            if (data.GetDataPresent(OwnFormat) || data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") ||
                data.GetDataPresent("org.nspasteboard.ConcealedType") || data.GetDataPresent("Clipboard Viewer Ignore")) return true;
            if (data.GetDataPresent("CanIncludeInClipboardHistory"))
            {
                var value = data.GetData("CanIncludeInClipboardHistory");
                var stream = value as MemoryStream;
                var bytes = value as byte[];
                if (stream != null) bytes = stream.ToArray();
                if (bytes != null && bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 0) return true;
                if (value is int && (int)value == 0 || value is uint && (uint)value == 0) return true;
            }
            return false;
        }
        public static Clip Read(IDataObject data, string source)
        { return Read(data, source, null); }
        public static Clip ReadCurrent(IDataObject data, string source)
        { return Read(data, source, NativeClipboard.Read); }
        static Clip Read(IDataObject data, string source, Func<string, byte[]> nativeRead)
        {
            if (data == null || Sensitive(data)) return null;
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0 && files.Length <= 1000)
                    return new Clip { Kind = "Files", Files = files, Source = source };
            }
            foreach (string format in new[] { "PNG", "image/png", "DIBV5", "DIB" })
            {
                try
                {
                    string name = format == "DIB" ? DataFormats.Dib : format;
                    byte[] bytes = nativeRead != null ? nativeRead(format) : data.GetDataPresent(name, false) ? ImageTransfer.Bytes(data.GetData(name, false)) : null;
                    if (bytes == null || bytes.Length == 0) continue;
                    BitmapSource image = format.StartsWith("DIB") ? ImageTransfer.DecodeDib(bytes) : ImageTransfer.Decode(bytes, 0);
                    return ImageClip(image, source);
                }
                catch (System.Runtime.InteropServices.ExternalException) { throw; }
                catch (NotSupportedException) { }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (FormatException) { }
                catch (OverflowException) { }
                catch (InvalidOperationException) { }
            }
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                object value = data.GetData(DataFormats.Bitmap);
                var bitmap = value as BitmapSource;
                if (bitmap != null) return ImageClip(ImageTransfer.Materialize(bitmap, true), source);
                var drawing = value as System.Drawing.Image;
                if (drawing != null)
                {
                    using (var ms = new MemoryStream()) { drawing.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ImageClip(ImageTransfer.Materialize(ImageTransfer.Decode(ms.ToArray(), 0), true), source); }
                }
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                string value = data.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 1024 * 1024) return new Clip { Kind = "Text", Text = value, Source = source };
            }
            return null;
        }
        static Clip ImageClip(BitmapSource image, string source)
        { return new Clip { Kind = "Image", Image = ImageTransfer.Encode(image), ImageVersion = 1, Source = source }; }
        public static void Write(Clip clip, IntPtr owner)
        {
            if (clip.Kind == "Image") NativeClipboard.WriteImage(clip.Image, owner);
            else if (clip.Kind == "Files")
            {
                if (clip.Files.Any(x => !File.Exists(x) && !Directory.Exists(x))) throw new IOException("部分原文件已移动或删除，请先检查文件位置。");
                NativeClipboard.WriteTransfer(clip.Files, null, owner);
            }
            else Clipboard.SetDataObject(ToData(clip), true);
        }
        public static DataObject ToData(Clip clip)
        {
            var data = new DataObject(); data.SetData(OwnFormat, "1");
            // Reusing a saved item should not re-upload it to Windows cloud clipboard.
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            if (clip.Kind == "Files")
            {
                var paths = clip.Files.Where(x => File.Exists(x) || Directory.Exists(x)).ToArray();
                if (paths.Length != clip.Files.Length) throw new IOException("部分原文件已移动或删除，请先检查文件位置。");
                data.SetData(DataFormats.FileDrop, paths);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            }
            else if (clip.Kind == "Image") { data.SetImage(Clip.Decode(clip.Image, 0)); data.SetData("PNG", new MemoryStream(clip.Image)); }
            else data.SetText(clip.Text ?? "");
            return data;
        }
    }
}
