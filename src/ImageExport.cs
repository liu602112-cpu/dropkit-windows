using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropKitWindows
{
    public static class ImageExport
    {
        public static string[] Save(IEnumerable<Clip> clips, string folder)
        {
            var images = ClipTransfer.OnlyImages(clips).OrderBy(x => x.Created).ToArray();
            if (images.Length == 0) return new string[0];
            Directory.CreateDirectory(folder); var saved = new List<string>();
            foreach (var clip in images)
            {
                if (clip.Kind == "Files")
                {
                    foreach (var file in clip.Files)
                    {
                        if (!File.Exists(file)) throw new IOException("原图片已移动或删除：" + Path.GetFileName(file));
                        string stem = Path.GetFileNameWithoutExtension(file), extension = Path.GetExtension(file); int number = 0;
                        while (true)
                        {
                            string target = Path.Combine(folder, stem + (number == 0 ? "" : "_" + number) + extension);
                            try { File.Copy(file, target, false); saved.Add(target); break; }
                            catch (IOException) { if (!File.Exists(target)) throw; number++; }
                        }
                    }
                    continue;
                }
                ImageTransfer.Decode(clip.Image, 0);
                string name = "DropKit_" + clip.Created.ToLocalTime().ToString("yyyyMMdd_HHmmss") + "_" + (saved.Count + 1).ToString("D3");
                int suffix = 0;
                while (true)
                {
                    string path = Path.Combine(folder, name + (suffix == 0 ? "" : "_" + suffix) + ".png");
                    FileStream output;
                    try { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write); }
                    catch (IOException) { if (!File.Exists(path)) throw; suffix++; continue; }
                    using (output) output.Write(clip.Image, 0, clip.Image.Length);
                    saved.Add(path); break;
                }
            }
            return saved.ToArray();
        }
    }
}
