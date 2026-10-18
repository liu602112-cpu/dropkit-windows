using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;

namespace DropKitWindows
{
    // One materialized payload is shared by clipboard and OLE drag operations.
    // Cached images stay available after a drag, a later copy, or application exit.
    public sealed class ClipTransfer
    {
        public string[] Paths;
        public byte[] Png;
        public string Text;
        public int TextCount = 1;
        public int Count { get { return Paths == null ? TextCount : Paths.Length; } }
        public static string ExportFolder
        {
            get
            {
                string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (string.IsNullOrEmpty(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
                return Path.Combine(pictures, "DropKit");
            }
        }
        static bool Under(string path, string folder)
        { return !string.IsNullOrEmpty(folder) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        static bool PrivateImage(string path)
        {
            return IsImagePath(path) && (Under(path, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) || Under(path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));
        }
        static string Store(byte[] bytes, string extension, string cache)
        {
            Directory.CreateDirectory(cache);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            // Short names for other applications; compare bytes before reusing a
            // name, and extend the digest on a collision or damaged cache file.
            for (int length = 24; length <= 64; length += 20)
            {
                string path = Path.Combine(cache, "DK_" + hash.Substring(0, length) + extension.ToLowerInvariant());
                if (File.Exists(path)) { if (File.ReadAllBytes(path).SequenceEqual(bytes)) return path; continue; }
                string temp = Path.Combine(cache, "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temp, bytes);
                    try { File.Move(temp, path); }
                    catch (IOException) { if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes)) throw; }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                return path;
            }
            throw new IOException("图片导出文件发生冲突，请在设置中打开图片导出文件夹检查。");
        }
        public static bool IsImagePath(string path)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".avif", ".heic" }
                .Contains(Path.GetExtension(path ?? "").ToLowerInvariant());
        }
        public static bool IsVideoPath(string path)
        { return new[] { ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi" }.Contains(Path.GetExtension(path ?? "").ToLowerInvariant()); }
        public static int ImageCount(Clip clip)
        { return clip.Kind == "Image" ? 1 : clip.Kind == "Files" && clip.Files != null ? clip.Files.Count(IsImagePath) : 0; }
        public static Clip[] OnlyImages(IEnumerable<Clip> clips)
        {
            return clips.Select(x => x.Kind == "Files" ? new Clip { Kind = "Files", Files = x.Files.Where(IsImagePath).ToArray(), Created = x.Created } : x)
                .Where(x => ImageCount(x) > 0).ToArray();
        }
        public static ClipTransfer Prepare(IEnumerable<Clip> clips, string cache)
        {
            var entries = clips.OrderBy(x => x.Created).ToArray();
            if (entries.Length == 0) throw new InvalidOperationException("请先选择记录；按住 Ctrl 或 Shift 可多选。");
            if (entries.All(x => x.Kind == "Text")) return new ClipTransfer { Text = string.Join(Environment.NewLine + Environment.NewLine, entries.Select(x => x.Text ?? "")), TextCount = entries.Length };
            if (entries.Any(x => x.Kind != "Image" && x.Kind != "Files")) throw new InvalidOperationException("图片和文件可以一起取出；文本请单独选择。");
            var paths = new List<string>();
            foreach (var clip in entries)
            {
                if (clip.Kind == "Image")
                {
                    ImageTransfer.Decode(clip.Image, 0);
                    paths.Add(Store(clip.Image, ".png", cache));
                }
                else foreach (string value in clip.Files ?? new string[0])
                {
                    string path = Path.GetFullPath(value);
                    if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("原文件已移动或删除：" + Path.GetFileName(path));
                    // File groups from earlier versions can point into the old
                    // private cache too. Materialize them in the public export
                    // folder before handing their paths to another application.
                    paths.Add(PrivateImage(path) ? Store(File.ReadAllBytes(path), Path.GetExtension(path), cache) : path);
                }
            }
            if (paths.Count == 0) throw new IOException("这些记录中没有可取出的文件。");
            return new ClipTransfer { Paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Png = entries.Length == 1 && entries[0].Kind == "Image" ? entries[0].Image : null };
        }
        public DataObject ToData()
        {
            var data = new DataObject();
            data.SetData(ClipboardService.OwnFormat, "1");
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
            if (Paths != null)
            {
                var names = new StringCollection(); names.AddRange(Paths); data.SetFileDropList(names);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            }
            else data.SetText(Text ?? "");
            if (Png != null) { data.SetImage(ImageTransfer.Decode(Png, 0)); data.SetData("PNG", new MemoryStream(Png)); }
            return data;
        }
        public void Write(IntPtr owner)
        {
            if (Paths != null) NativeClipboard.WriteTransfer(Paths, Png, owner);
            else Clipboard.SetDataObject(ToData(), true);
        }
    }
}
