using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DropKitWindows
{
    public sealed class RecycleResult
    {
        public readonly HashSet<string> RemovedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> RemovedImageHashes = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> CompletedRecords = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public readonly Dictionary<string, string> Receipts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Removed(string path)
        {
            try { path = Path.GetFullPath(path); return RemovedPaths.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase) || path.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }
    }
    public static class RecycleWork
    {
        public static Task<RecycleResult> Run(Clip[] clips, string[] files, string exportFolder, string staging, string[] protectedPaths, bool cleanOrphans)
        {
            var completion = new TaskCompletionSource<RecycleResult>();
            var thread = new Thread(() => { try { completion.SetResult(Execute(clips, files, exportFolder, staging, protectedPaths, cleanOrphans)); } catch (Exception ex) { completion.SetException(ex); } });
            thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return completion.Task;
        }
        public static RecycleResult Execute(Clip[] clips, string[] files, string exportFolder, string staging, string[] protectedPaths, bool cleanOrphans)
        {
            var result = new RecycleResult();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recordPaths = new Dictionary<string, string[]>();
            foreach (string file in files) try { paths.Add(Path.GetFullPath(file)); } catch { result.Errors.Add("无效路径"); }
            foreach (var clip in clips)
            {
                try
                {
                    string[] targets;
                    if (clip.Kind == "Files") targets = (clip.Files ?? new string[0]).Select(Path.GetFullPath).ToArray();
                    else if (clip.Kind == "Image")
                    {
                        targets = ClipTransfer.Prepare(new[] { clip }, exportFolder).Paths;
                        string digest; using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(clip.Image)).Replace("-", "").ToLowerInvariant();
                        var extra = new List<string>(targets);
                        for (int length = 24; length <= 64; length += 20)
                        { string name = Path.Combine(exportFolder, "DK_" + digest.Substring(0, length) + ".png"); if (File.Exists(name)) extra.Add(name); }
                        targets = extra.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    else
                    {
                        Directory.CreateDirectory(staging);
                        string name = Path.Combine(staging, "Clipboard_" + clip.Created.ToLocalTime().ToString("yyyyMMdd_HHmmss") + "_" + clip.Id + ".txt");
                        File.WriteAllText(name, clip.Text ?? "", new UTF8Encoding(true)); targets = new[] { name };
                    }
                    recordPaths[clip.Id] = targets;
                    foreach (string target in targets) paths.Add(target);
                }
                catch (Exception ex) { result.Errors.Add("准备回收失败：" + ex.Message); }
            }
            Func<string, bool> underExport = path => Path.GetFullPath(path).StartsWith(Path.GetFullPath(exportFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            if (cleanOrphans && Directory.Exists(exportFolder))
            {
                var keep = protectedPaths.Select(Path.GetFullPath).ToArray();
                Func<string, bool> retained = path => keep.Any(x => path.Equals(x, StringComparison.OrdinalIgnoreCase) || Directory.Exists(x) && path.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                Func<string, bool> managed = name => Regex.IsMatch(name, @"^DK_[a-f0-9]{24,64}\.(png|jpg|gif|webp)$", RegexOptions.IgnoreCase) || Regex.IsMatch(name, @"^Pinterest_(\d+|media)_[a-f0-9]{12}(_[a-f0-9]{8})?\.(mp4|gif|jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase);
                foreach (string folder in new[] { exportFolder, Path.Combine(exportFolder, "Pinterest") }.Where(Directory.Exists))
                    foreach (string path in Directory.GetFiles(folder))
                    {
                        if (managed(Path.GetFileName(path)) && !retained(path)) paths.Add(path);
                        foreach (string suffix in new[] { ".source.json", ".preview.jpg" })
                            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            { string body = path.Substring(0, path.Length - suffix.Length); if (managed(Path.GetFileName(body)) && !File.Exists(body) && !retained(body)) paths.Add(path); }
                    }
            }
            foreach (string path in paths.ToArray())
                if (underExport(path)) foreach (string suffix in new[] { ".source.json", ".preview.jpg" }) if (File.Exists(path + suffix)) paths.Add(path + suffix);
            var directories = paths.Where(Directory.Exists).ToArray();
            var top = paths.Where(path => !directories.Any(parent => !parent.Equals(path, StringComparison.OrdinalIgnoreCase) && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
            foreach (string path in top)
            {
                try
                {
                    if (!File.Exists(path) && !Directory.Exists(path)) { result.RemovedPaths.Add(path); continue; }
                    string imageHash = null;
                    if (File.Exists(path) && ClipTransfer.IsImagePath(path))
                        using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) imageHash = Convert.ToBase64String(sha.ComputeHash(file));
                    result.Receipts[path] = RecycleShell.MoveToBin(path);
                    result.RemovedPaths.Add(path);
                    if (imageHash != null) result.RemovedImageHashes.Add(imageHash);
                }
                catch (Exception ex) { result.Errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
            }
            foreach (var pair in recordPaths) if (pair.Value.All(result.Removed)) result.CompletedRecords.Add(pair.Key);
            return result;
        }
    }
}
