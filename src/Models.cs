using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DropKitWindows
{
    [DataContract]
    public class Preferences
    {
        [DataMember] public bool Capture = true;
        [DataMember] public bool Shake = true;
        [DataMember] public bool Topmost = true;
        [DataMember] public int MaxItems = 200;
        [DataMember] public int Days = 30;
        [DataMember] public string Excluded = "1password;bitwarden;keepass;keepassxc;dashlane;enpass;lastpass;roboform";
        [DataMember] public string WatchFolder = "";
        [DataMember] public bool WatchEnabled;
        public Preferences Copy() { return (Preferences)MemberwiseClone(); }
    }

    [DataContract]
    public class Clip : System.ComponentModel.INotifyPropertyChanged
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Kind;
        [DataMember] public string Text;
        [DataMember] public string[] Files;
        public byte[] Image;
        // Read legacy numeric-byte arrays, but write compact base64 for new
        // saves. Old history remains readable and PNG bytes stay identical.
        [DataMember(Name = "Image", EmitDefaultValue = false)] byte[] LegacyImage { get { return null; } set { if (value != null) Image = value; } }
        [DataMember(Name = "ImageBase64", EmitDefaultValue = false)] string ImageBase64 { get { return Image == null ? null : Convert.ToBase64String(Image); } set { if (value != null) Image = Convert.FromBase64String(value); } }
        [DataMember] public int ImageVersion;
        [DataMember] public DateTime Created = DateTime.UtcNow;
        [DataMember] public bool Pinned;
        [DataMember] public string Source;
        [DataMember] public string Hash;
        [DataMember] public string OriginUrl;
        public string DragMediaHint;
        public Clip Copy() { return (Clip)MemberwiseClone(); }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        public void NotifyChanged() { if (PropertyChanged != null) PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(null)); }
        public void ResetThumbnail() { thumb = null; NotifyChanged(); }
        public string Title { get { return Kind == "Image" ? "图片 / 截图" : Kind == "Files" ? System.IO.Path.GetFileName(Files[0]) + (Files.Length > 1 ? "  +" + (Files.Length - 1) : "") : (Text ?? "").Trim(); } }
        public string Detail { get { return Kind == "Files" ? string.Join("\n", Files) : Kind == "Image" ? "PNG 图片 · " + Math.Max(1, Image.Length / 1024) + " KB" : Text; } }
        public string PreviewText { get { string s = Kind == "Files" && !string.IsNullOrEmpty(OriginUrl) ? Title + " · " + Tag : Detail ?? ""; return s.Length > 900 ? s.Substring(0, 900) + "…" : s; } }
        public string Tag { get { return IsVideo ? "视频" : IsImage ? "图片" : Kind == "Files" ? "文件" : IsUrl ? "链接" : "文本"; } }
        public bool IsVideo { get { return Kind == "Files" && Files != null && Files.Any(ClipTransfer.IsVideoPath); } }
        public bool HasMediaPreview { get { return IsImage || IsVideo; } }
        public bool IsImage { get { return ClipTransfer.ImageCount(this) > 0; } }
        public string DateKey { get { return Created.ToLocalTime().ToString("yyyy-MM-dd"); } }
        public bool IsUrl { get { Uri u; return Kind == "Text" && Uri.TryCreate((Text ?? "").Trim(), UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https"); } }
        public string Glyph { get { return IsVideo ? "\uE714" : Kind == "Image" ? "\uE91B" : Kind == "Files" ? "\uE8B7" : IsUrl ? "\uE71B" : "\uE8A5"; } }
        public string Meta { get { int count = ClipTransfer.ImageCount(this); return (Pinned ? "已固定 · " : "") + Tag + (count > 1 ? " × " + count : "") + " · " + Created.ToLocalTime().ToString("MM-dd HH:mm") + (string.IsNullOrEmpty(Source) ? "" : " · " + Source); } }
        public long Weight { get { return Image != null ? Image.Length : Encoding.UTF8.GetByteCount(Detail ?? "") + 256; } }
        private BitmapSource thumb;
        public BitmapSource Thumbnail
        {
            get
            {
                if (thumb == null)
                {
                    try
                    {
                        if (Image != null) thumb = Decode(Image, 320);
                        else if (Files != null) { string path = Files.FirstOrDefault(x => ClipTransfer.IsImagePath(x) || ClipTransfer.IsVideoPath(x)); if (path != null) thumb = new ShelfFile { Path = path }.Thumbnail; }
                    }
                    catch { }
                }
                return thumb;
            }
        }
        public static BitmapSource Decode(byte[] bytes, int width)
        {
            return ImageTransfer.Decode(bytes, width);
        }
        public void ComputeHash()
        {
            byte[] value = Image ?? Encoding.UTF8.GetBytes(Kind + ":" + (Files != null ? string.Join("\n", Files).ToUpperInvariant() : Text));
            using (var sha = SHA256.Create()) Hash = Convert.ToBase64String(sha.ComputeHash(value));
        }
    }

    public class ShelfFile
    {
        public string Path { get; set; }
        public string Name { get { return System.IO.Path.GetFileName(Path.TrimEnd('\\')); } }
        public bool Exists { get { return File.Exists(Path) || Directory.Exists(Path); } }
        public string Glyph { get { return Directory.Exists(Path) ? "\uE8B7" : ClipTransfer.IsVideoPath(Path) ? "\uE714" : "\uE8A5"; } }
        public string Detail
        {
            get
            {
                try { return Directory.Exists(Path) ? "文件夹" : File.Exists(Path) ? Math.Max(0.1, new FileInfo(Path).Length / 1024.0).ToString("0.#") + " KB · " + System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() : "原文件已移动或删除"; }
                catch { return "无法读取文件"; }
            }
        }
        private BitmapSource thumb;
        private bool loaded;
        public BitmapSource Thumbnail
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    try
                    {
                        var ext = System.IO.Path.GetExtension(Path).ToLowerInvariant();
                        string preview = ClipTransfer.IsVideoPath(Path) && File.Exists(Path + ".preview.jpg") ? Path + ".preview.jpg" : Path;
                        if (ClipTransfer.IsImagePath(preview) && File.Exists(preview))
                        {
                            using (var stream = File.OpenRead(preview))
                            {
                                var img = new BitmapImage(); img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad;
                                img.DecodePixelWidth = 160; img.StreamSource = stream; img.EndInit(); img.Freeze(); thumb = img;
                            }
                        }
                    }
                    catch { }
                }
                return thumb;
            }
        }
    }

    [DataContract]
    public class State
    {
        [DataMember] public Preferences Settings = new Preferences();
        [DataMember] public List<Clip> History = new List<Clip>();
        [DataMember] public List<string> Shelf = new List<string>();
        public State Snapshot() { return new State { Settings = Settings.Copy(), History = History.Select(x => x.Copy()).ToList(), Shelf = new List<string>(Shelf) }; }
    }

    public class Vault
    {
        public readonly string Folder;
        public string Warning;
        readonly object gate = new object();
        public Vault(string folder) { Folder = folder; Directory.CreateDirectory(folder); }
        public string FilePath { get { return System.IO.Path.Combine(Folder, "history.dat"); } }
        public State Load()
        {
            if (!File.Exists(FilePath)) return new State();
            try
            {
                byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
                using (var ms = new MemoryStream(bytes))
                {
                    var state = (State)new DataContractJsonSerializer(typeof(State)).ReadObject(ms);
                    if (state == null || state.Settings == null || state.History == null || state.Shelf == null) throw new InvalidDataException();
                    return state;
                }
            }
            catch
            {
                // Preserve unreadable data instead of silently overwriting the only copy.
                string recovery = FilePath + ".unreadable-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                File.Copy(FilePath, recovery, false);
                Warning = "旧记录无法读取，已保留恢复副本。当前使用新的记录。";
                return new State();
            }
        }
        public byte[] Serialize(State state)
        {
            using (var ms = new MemoryStream()) { new DataContractJsonSerializer(typeof(State)).WriteObject(ms, state); return ms.ToArray(); }
        }
        public void SaveBytes(byte[] bytes)
        {
            lock (gate)
            {
                string temp = FilePath + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
            }
        }
    }

    public static class HistoryLogic
    {
        public static void Add(State state, Clip clip)
        {
            clip.ComputeHash();
            var old = state.History.FirstOrDefault(x => x.Hash == clip.Hash);
            if (old != null)
            {
                // Re-copying a selected item should move that same record, not
                // replace its identity and silently clear the user's selection.
                old.Created = clip.Created; old.Source = clip.Source; old.OriginUrl = clip.OriginUrl ?? old.OriginUrl; old.NotifyChanged();
                state.History.Remove(old); clip = old;
            }
            state.History.Insert(0, clip); Prune(state, DateTime.UtcNow);
        }
        public static void Prune(State state, DateTime now)
        {
            int days = Math.Max(0, state.Settings.Days), limit = Math.Max(0, state.Settings.MaxItems);
            if (days > 0) state.History.RemoveAll(x => !x.Pinned && x.Created < now.AddDays(-days));
            // Pinned items never expire and do not consume the ordinary item allowance.
            int count = 0;
            state.History.RemoveAll(x => !x.Pinned && ++count > limit && limit > 0);
            long used = state.History.Sum(x => x.Weight);
            for (int i = state.History.Count - 1; i >= 0 && used > 64L * 1024 * 1024; i--)
                if (!state.History[i].Pinned) { used -= state.History[i].Weight; state.History.RemoveAt(i); }
        }
        public static IEnumerable<Clip> Search(State state, string query, string filter)
        {
            return state.History.Where(x => (filter == "全部" || filter == "固定" && x.Pinned || filter == x.Tag || filter == "文件" && x.Kind == "Files" || (filter == "文本" || filter == "文字") && x.Kind == "Text") &&
                (string.IsNullOrWhiteSpace(query) || (x.Detail ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(x => x.Created.ToLocalTime().Date).ThenByDescending(x => x.Pinned).ThenByDescending(x => x.Created);
        }
    }

    public class ShakeLogic
    {
        bool tracking, fired;
        int anchor, direction;
        long started;
        readonly Queue<long> turns = new Queue<long>();
        public bool Feed(int x, bool down, long ms)
        {
            if (!down) { tracking = false; fired = false; turns.Clear(); direction = 0; return false; }
            if (!tracking) { tracking = true; anchor = x; started = ms; return false; }
            int delta = x - anchor;
            if (Math.Abs(delta) < 56) return false;
            int next = Math.Sign(delta); anchor = x;
            while (turns.Count > 0 && ms - turns.Peek() > 1000) turns.Dequeue();
            if (direction != 0 && next != direction) turns.Enqueue(ms);
            direction = next;
            if (!fired && turns.Count >= 4 && ms - started >= 280) { fired = true; return true; }
            return false;
        }
    }
}
