using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DropKitWindows
{
    public static class ImageTransfer
    {
        public const int MaxEncodedBytes = 32 * 1024 * 1024;
        public const int MaxPixels = 20000000;
        public static void ValidateSize(int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) throw new InvalidDataException("图片超过 2000 万像素限制。");
        }
        public static byte[] Bytes(object value)
        {
            var bytes = value as byte[]; if (bytes != null) return bytes;
            var ms = value as MemoryStream; if (ms != null) return ms.ToArray();
            var stream = value as Stream;
            if (stream == null || !stream.CanRead) return null;
            long position = stream.CanSeek ? stream.Position : 0;
            try
            {
                if (stream.CanSeek) stream.Position = 0;
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192]; int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    { if (output.Length + read > 96L * 1024 * 1024) throw new InvalidDataException(); output.Write(buffer, 0, read); }
                    return output.ToArray();
                }
            }
            finally { if (stream.CanSeek) stream.Position = position; }
        }
        public static BitmapSource Decode(byte[] bytes, int maxWidth)
        {
            if (bytes == null || bytes.Length == 0) throw new InvalidDataException("图片内容为空。");
            using (var ms = new MemoryStream(bytes, false))
            {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0]; ValidateSize(frame.PixelWidth, frame.PixelHeight);
                BitmapSource source = frame;
                if (maxWidth > 0 && source.PixelWidth > maxWidth)
                    source = new TransformedBitmap(source, new ScaleTransform((double)maxWidth / source.PixelWidth, (double)maxWidth / source.PixelWidth));
                // Copy actual pixels: never retain a lazy clipboard bitmap, decoder or native handle.
                return Materialize(source, false);
            }
        }
        public static BitmapSource Materialize(BitmapSource source, bool opaque)
        {
            ValidateSize(source.PixelWidth, source.PixelHeight);
            BitmapSource input = source;
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
                input = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(source.PixelWidth * 4); byte[] pixels = new byte[checked(stride * source.PixelHeight)];
            input.CopyPixels(pixels, stride, 0);
            if (opaque || source.Format == PixelFormats.Bgr32) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }
        public static byte[] Encode(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var ms = new MemoryStream()) { encoder.Save(ms); if (ms.Length > MaxEncodedBytes) throw new InvalidDataException("图片编码超过 32 MB 限制。"); return ms.ToArray(); }
        }
        static uint U32(byte[] data, int offset) { return BitConverter.ToUInt32(data, offset); }
        static byte Component(uint value, uint mask)
        {
            if (mask == 0) return 0;
            while ((mask & 1) == 0) { mask >>= 1; value >>= 1; }
            return (byte)(((ulong)(value & mask) * 255 + mask / 2) / mask);
        }
        public static BitmapSource DecodeDib(byte[] dib)
        {
            if (dib == null || dib.Length < 40) throw new InvalidDataException("DIB header is missing.");
            int header = checked((int)U32(dib, 0));
            if (header != 40 && header != 52 && header != 56 && header != 108 && header != 124 || header > dib.Length) throw new InvalidDataException("Unsupported DIB header.");
            int width = BitConverter.ToInt32(dib, 4), signedHeight = BitConverter.ToInt32(dib, 8);
            if (signedHeight == int.MinValue) throw new InvalidDataException();
            int height = Math.Abs(signedHeight), bits = BitConverter.ToUInt16(dib, 14); uint compression = U32(dib, 16);
            ValidateSize(width, height);
            if (BitConverter.ToUInt16(dib, 12) != 1 || !(bits == 1 || bits == 4 || bits == 8 || bits == 16 || bits == 24 || bits == 32)) throw new InvalidDataException("Unsupported DIB pixel format.");
            if (compression != 0 && compression != 3 && compression != 6) throw new InvalidDataException("Compressed DIB is not supported.");
            int extraMasks = header == 40 && (compression == 3 || compression == 6) ? (compression == 6 ? 16 : 12) : 0;
            uint colors = U32(dib, 32); if (bits <= 8 && colors == 0) colors = (uint)(1 << bits);
            if (colors > 256) throw new InvalidDataException();
            int offset = checked(header + extraMasks + (int)colors * 4);
            int stride = checked((int)(((long)width * bits + 31) / 32 * 4));
            if ((long)offset + (long)stride * height > dib.Length) throw new InvalidDataException("DIB pixels are truncated.");
            uint red = bits == 16 ? 0x7C00u : 0x00FF0000u, green = bits == 16 ? 0x03E0u : 0x0000FF00u, blue = bits == 16 ? 0x001Fu : 0x000000FFu, alpha = 0;
            if (compression == 3 || compression == 6)
            {
                red = U32(dib, 40); green = U32(dib, 44); blue = U32(dib, 48);
                if (header >= 56 || extraMasks == 16) alpha = U32(dib, 52);
                if (red == 0 || green == 0 || blue == 0) throw new InvalidDataException("Invalid DIB masks.");
            }
            else if (header >= 108 && bits == 32) alpha = U32(dib, 52);
            byte[] pixels = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++)
            {
                int row = offset + (signedHeight < 0 ? y : height - 1 - y) * stride;
                for (int x = 0; x < width; x++)
                {
                    int dest = (y * width + x) * 4;
                    if (bits <= 8)
                    {
                        int index = bits == 8 ? dib[row + x] : bits == 4 ? (dib[row + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 15 : (dib[row + x / 8] >> (7 - x % 8)) & 1;
                        if (index >= colors) throw new InvalidDataException();
                        int color = header + extraMasks + index * 4;
                        pixels[dest] = dib[color]; pixels[dest + 1] = dib[color + 1]; pixels[dest + 2] = dib[color + 2]; pixels[dest + 3] = 255;
                    }
                    else if (bits == 24)
                    { int src = row + x * 3; pixels[dest] = dib[src]; pixels[dest + 1] = dib[src + 1]; pixels[dest + 2] = dib[src + 2]; pixels[dest + 3] = 255; }
                    else
                    {
                        uint value = bits == 32 ? U32(dib, row + x * 4) : BitConverter.ToUInt16(dib, row + x * 2);
                        pixels[dest] = Component(value, blue); pixels[dest + 1] = Component(value, green); pixels[dest + 2] = Component(value, red);
                        // BI_RGB's fourth byte is reserved, not an alpha channel.
                        pixels[dest + 3] = alpha == 0 ? (byte)255 : Component(value, alpha);
                    }
                }
            }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); result.Freeze(); return result;
        }
        public static byte[] EncodeDib(BitmapSource source, bool v5)
        {
            var image = Materialize(source, false); int w = image.PixelWidth, h = image.PixelHeight;
            byte[] pixels = new byte[w * h * 4]; image.CopyPixels(pixels, w * 4, 0);
            int header = v5 ? 124 : 40, bits = v5 ? 32 : 24, stride = (w * bits + 31) / 32 * 4;
            using (var ms = new MemoryStream()) using (var writer = new BinaryWriter(ms))
            {
                writer.Write(header); writer.Write(w); writer.Write(v5 ? -h : h); writer.Write((ushort)1); writer.Write((ushort)bits);
                writer.Write(v5 ? 3 : 0); writer.Write(stride * h); writer.Write(3780); writer.Write(3780); writer.Write(0); writer.Write(0);
                if (v5)
                { writer.Write(0x00FF0000u); writer.Write(0x0000FF00u); writer.Write(0x000000FFu); writer.Write(0xFF000000u); writer.Write(0x73524742u); writer.Write(new byte[48]); writer.Write(4); writer.Write(0); writer.Write(0); writer.Write(0); }
                for (int y = 0; y < h; y++)
                {
                    byte[] row = new byte[stride]; int sy = v5 ? y : h - 1 - y;
                    for (int x = 0; x < w; x++)
                    {
                        int src = (sy * w + x) * 4, dest = x * bits / 8;
                        if (v5) Buffer.BlockCopy(pixels, src, row, dest, 4);
                        else for (int channel = 0; channel < 3; channel++) row[dest + channel] = (byte)((pixels[src + channel] * pixels[src + 3] + 255 * (255 - pixels[src + 3]) + 127) / 255);
                    }
                    writer.Write(row);
                }
                return ms.ToArray();
            }
        }
        public static byte[] RepairLegacy(byte[] png)
        {
            var bitmap = Decode(png, 0); var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            bool colored = false;
            for (int i = 0; i < pixels.Length; i += 4)
            { if (pixels[i + 3] != 0) return null; if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) colored = true; }
            return colored ? Encode(Materialize(bitmap, true)) : null;
        }
    }

    public static class NativeClipboard
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr count);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
        public static byte[] Read(string format)
        {
            uint id = format == "DIB" ? 8u : format == "DIBV5" ? 17u : RegisterClipboardFormat(format);
            if (!OpenClipboard(IntPtr.Zero)) throw new ExternalException("剪贴板正在被其他应用占用。");
            try
            {
                IntPtr memory = GetClipboardData(id); if (memory == IntPtr.Zero) return null;
                ulong size = GlobalSize(memory).ToUInt64(); if (size == 0 || size > 96UL * 1024 * 1024) return null;
                IntPtr pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) return null;
                try { var result = new byte[(int)size]; Marshal.Copy(pointer, result, 0, result.Length); return result; }
                finally { GlobalUnlock(memory); }
            }
            finally { CloseClipboard(); }
        }
        static IntPtr Allocate(byte[] data)
        {
            IntPtr memory = GlobalAlloc(2, new UIntPtr((uint)data.Length)); if (memory == IntPtr.Zero) throw new OutOfMemoryException();
            IntPtr pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new OutOfMemoryException(); }
            try { Marshal.Copy(data, 0, pointer, data.Length); } finally { GlobalUnlock(memory); }
            return memory;
        }
        public static void WriteFormats(IntPtr owner, uint[] formats, byte[][] values)
        {
            var handles = new IntPtr[formats.Length]; bool opened = false;
            try
            {
                for (int i = 0; i < formats.Length; i++) handles[i] = Allocate(values[i]);
                if (!OpenClipboard(owner)) throw new ExternalException("剪贴板正在被其他应用占用，请稍后重试。");
                opened = true; if (!EmptyClipboard()) throw new ExternalException("无法写入剪贴板。");
                for (int i = 0; i < formats.Length; i++)
                { if (SetClipboardData(formats[i], handles[i]) == IntPtr.Zero) throw new ExternalException("写入图片格式失败。"); handles[i] = IntPtr.Zero; }
            }
            finally { if (opened) CloseClipboard(); foreach (var memory in handles) if (memory != IntPtr.Zero) GlobalFree(memory); }
        }
        public static void WriteImage(byte[] png, IntPtr owner)
        {
            var bitmap = ImageTransfer.Decode(png, 0);
            WriteFormats(owner, new[] { RegisterClipboardFormat(ClipboardService.OwnFormat), RegisterClipboardFormat("PNG"), 17u, 8u, RegisterClipboardFormat("CanUploadToCloudClipboard") },
                new[] { new byte[] { 1 }, png, ImageTransfer.EncodeDib(bitmap, true), ImageTransfer.EncodeDib(bitmap, false), new byte[4] });
        }
        public static byte[] FileDropBytes(string[] paths)
        {
            // DROPFILES has a fixed 20-byte header on both 32- and 64-bit Windows.
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(20); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(1);
                writer.Write(System.Text.Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0"));
                return stream.ToArray();
            }
        }
        public static void WriteTransfer(string[] paths, byte[] png, IntPtr owner)
        {
            var formats = new System.Collections.Generic.List<uint> { RegisterClipboardFormat(ClipboardService.OwnFormat), RegisterClipboardFormat("CanUploadToCloudClipboard") };
            var values = new System.Collections.Generic.List<byte[]> { new byte[] { 1 }, new byte[4] };
            if (png != null)
            {
                var bitmap = ImageTransfer.Decode(png, 0);
                formats.AddRange(new[] { RegisterClipboardFormat("PNG"), 17u, 8u });
                values.AddRange(new[] { png, ImageTransfer.EncodeDib(bitmap, true), ImageTransfer.EncodeDib(bitmap, false) });
            }
            formats.Add(15); values.Add(FileDropBytes(paths));
            formats.Add(RegisterClipboardFormat("Preferred DropEffect")); values.Add(BitConverter.GetBytes(1));
            WriteFormats(owner, formats.ToArray(), values.ToArray());
        }
    }
}
