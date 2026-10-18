using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DropKitWindows
{
    public static class SelfTests
    {
        sealed class FormatOnlyData : IDataObject
        {
            public object GetData(string format) { throw new Exception("Drag-over requested content"); }
            public object GetData(string format, bool autoConvert) { return GetData(format); }
            public object GetData(Type format) { return GetData(format.FullName); }
            public bool GetDataPresent(string format) { return format == DataFormats.Html; }
            public bool GetDataPresent(string format, bool autoConvert) { return GetDataPresent(format); }
            public bool GetDataPresent(Type format) { return false; }
            public string[] GetFormats() { return new[] { DataFormats.Html }; }
            public string[] GetFormats(bool autoConvert) { return GetFormats(); }
            public void SetData(object data) { throw new NotSupportedException(); }
            public void SetData(string format, object data) { throw new NotSupportedException(); }
            public void SetData(string format, object data, bool autoConvert) { throw new NotSupportedException(); }
            public void SetData(Type format, object data) { throw new NotSupportedException(); }
        }
        static readonly List<string> results = new List<string>();
        static void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
        }
        public static void Snapshot(Window window, string file)
        {
            window.UpdateLayout(); Pump(120);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
        static void Click(Controller c, string name) { c.UI<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(30); }
        static Clip ColorImage(int width, int height, byte red, byte green, byte blue)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            { int i = (y * width + x) * 4; pixels[i] = (byte)(blue * (x + 1) / width); pixels[i + 1] = green; pixels[i + 2] = (byte)(red * (y + 1) / height); pixels[i + 3] = 255; }
            return new Clip { Kind = "Image", Source = "图片验证", ImageVersion = 1, Image = ImageTransfer.Encode(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)) };
        }
        public static int Run(string folder)
        {
            Directory.CreateDirectory(folder);
            Controller c = null;
            string startupTestRoot = "Software\\DropKitWindows\\QA\\" + Guid.NewGuid().ToString("N");
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Uri pinSource;
                Check("Pinterest pin links normalize without tracking query", PinterestMedia.TrySource("https://www.pinterest.com/pin/751608625355364752/?tracking=1", out pinSource) && pinSource.AbsoluteUri == "https://www.pinterest.com/pin/751608625355364752/");
                Check("lookalike hosts and embedded credentials cannot become download sources", !PinterestMedia.TrySource("https://pinterest.com.example.com/pin/1/", out pinSource) && !PinterestMedia.TrySource("https://user:password@www.pinterest.com/pin/1/", out pinSource));
                var htmlDrag = new DataObject(); htmlDrag.SetData(DataFormats.Html, "SourceURL:https://www.pinterest.com/pin/751608625355364752/\r\n<a href=\"https://www.pinterest.com/pin/751608625355364752/\"><img src=\"https://i.pinimg.com/236x/a.jpg\"></a>");
                Check("browser HTML-only drag identifies pin rather than thumbnail", DropContent.Read(htmlDrag, "browser").Text == "https://www.pinterest.com/pin/751608625355364752/");
                Check("drag-over does not request or materialize browser data", DropContent.HasSupportedFormat(new FormatOnlyData()));
                string pinJson = "{\"resource_response\":{\"data\":{\"id\":\"123\",\"images\":{\"orig\":{\"url\":\"https://i.pinimg.com/originals/a.jpg\"}},\"videos\":{\"video_list\":{\"low\":{\"url\":\"https://v.pinimg.com/low.mp4\",\"width\":320,\"height\":240},\"high\":{\"url\":\"https://v.pinimg.com/high.mp4\",\"width\":1080,\"height\":1920}}}}}}";
                Check("public pin parser prefers largest MP4 to cover image", PinterestMedia.ParsePin(pinJson, "123").MediaUrl == "https://v.pinimg.com/high.mp4");
                Check("original GIF is preserved as animation instead of a PNG frame", PinterestMedia.ParsePin("{\"id\":\"321\",\"images\":{\"orig\":{\"url\":\"https://i.pinimg.com/originals/a.gif\"}}}", "321").Kind == "动图");
                Check("story pin video is read from its own blocks", PinterestMedia.ParsePin("{\"id\":\"456\",\"images\":{},\"story_pin_data\":{\"pages\":[{\"blocks\":[{\"video\":{\"video_list\":{\"x\":{\"url\":\"https://v.pinimg.com/story.mp4\"}}}}]}]}}", "456").Kind == "视频");
                bool streamBlocked = false;
                try { PinterestMedia.ParsePin("{\"id\":\"789\",\"images\":{\"orig\":{\"url\":\"https://i.pinimg.com/cover.jpg\"}},\"story_pin_data\":{\"video\":{\"url\":\"https://v.pinimg.com/stream.m3u8\"}}}", "789"); } catch (NotSupportedException) { streamBlocked = true; }
                Check("stream-only story video cannot silently import its cover as video", streamBlocked);
                string carouselJson = "{\"id\":\"888\",\"images\":{\"orig\":{\"url\":\"https://i.pinimg.com/11111111111111111111111111111111.jpg\"}},\"carousel_data\":{\"index\":1,\"carousel_slots\":[{\"images\":{\"x\":{\"url\":\"https://i.pinimg.com/00000000000000000000000000000000.jpg\"}},\"videos\":{\"x\":{\"url\":\"https://v.pinimg.com/other.mp4\"}}},{\"images\":{\"x\":{\"url\":\"https://i.pinimg.com/11111111111111111111111111111111.jpg\"}},\"videos\":{\"x\":{\"url\":\"https://v.pinimg.com/selected.mp4\"}}},{\"images\":{\"orig\":{\"url\":\"https://i.pinimg.com/22222222222222222222222222222222.jpg\"}},\"videos\":null}]}}";
                Check("carousel chooses current item's video even when root videos is null", PinterestMedia.ParsePin(carouselJson, "888").MediaUrl == "https://v.pinimg.com/selected.mp4");
                Check("dragged carousel poster selects its own slot", PinterestMedia.ParsePin(carouselJson, "888", "https://i.pinimg.com/236x/00000000000000000000000000000000.jpg").MediaUrl == "https://v.pinimg.com/other.mp4");
                Check("static carousel slot cannot import a neighbour's video", PinterestMedia.ParsePin(carouselJson, "888", "https://i.pinimg.com/22222222222222222222222222222222.jpg").Kind == "图片");
                bool unrelatedBlocked = false;
                try { PinterestMedia.ParsePin(pinJson, "999"); } catch (InvalidDataException) { unrelatedBlocked = true; }
                Check("parser cannot import recommendation media from a different pin", unrelatedBlocked);
                string badMedia = Path.Combine(folder, "not-a-video.mp4"); File.WriteAllText(badMedia, "<html>this is not a video</html>");
                Check("HTML response cannot be accepted as saved MP4", !PinterestMedia.IsMediaFile(badMedia));
                string launchName = "Local\\DropKit-LaunchQA-" + Guid.NewGuid().ToString("N");
                using (var broker = new LaunchBroker(launchName))
                {
                    bool secondary = false;
                    var launchThread = new Thread(delegate() { using (var secondBroker = new LaunchBroker(launchName)) { secondary = !secondBroker.IsPrimary; secondBroker.RequestOpen(); secondBroker.RequestToggle(); secondBroker.RequestToggle(); } });
                    launchThread.Start(); launchThread.Join();
                    Check("second launch cannot become a second primary", broker.IsPrimary && secondary);
                    Check("open request before UI startup is retained after sender exits", broker.ConsumeOpen());
                    Check("retained open request is consumed exactly once", !broker.ConsumeOpen());
                    Check("two F4 toggles during loading are retained separately", broker.ConsumeToggle() && broker.ConsumeToggle() && !broker.ConsumeToggle());
                }
                using (var nextLaunch = new LaunchBroker(launchName)) Check("normal shutdown permits immediate restart", nextLaunch.IsPrimary);
                // Keep a second handle alive while the owner thread terminates without releasing its mutex.
                string abandonedName = launchName + "-abandoned"; var ownerReady = new ManualResetEvent(false); var terminateOwner = new ManualResetEvent(false);
                var ownerThread = new Thread(delegate() { var abandoned = new Mutex(true, abandonedName); ownerReady.Set(); terminateOwner.WaitOne(); GC.KeepAlive(abandoned); });
                ownerThread.Start(); ownerReady.WaitOne();
                using (var retained = new Mutex(false, abandonedName))
                {
                    terminateOwner.Set(); ownerThread.Join();
                    using (var recovered = new LaunchBroker(abandonedName)) Check("abandoned single-instance lock recovers after owner exits", recovered.IsPrimary);
                }
                ownerReady.Dispose(); terminateOwner.Dispose();
                var s = new State(); s.Settings.MaxItems = 2; s.Settings.Days = 10;
                var pinned = new Clip { Kind = "Text", Text = "keep", Pinned = true, Created = DateTime.UtcNow.AddDays(-100) };
                HistoryLogic.Add(s, pinned);
                HistoryLogic.Add(s, new Clip { Kind = "Text", Text = "old", Created = DateTime.UtcNow.AddDays(-20) });
                HistoryLogic.Add(s, new Clip { Kind = "Text", Text = "one" }); HistoryLogic.Add(s, new Clip { Kind = "Text", Text = "two" }); HistoryLogic.Add(s, new Clip { Kind = "Text", Text = "three" });
                Check("retention, count limit and pinned exemption", s.History.Count == 3 && s.History.Contains(pinned) && !s.History.Any(x => x.Text == "old" || x.Text == "one"));
                HistoryLogic.Add(s, new Clip { Kind = "Text", Text = "keep" });
                Check("dedup preserves pinned status", s.History.Count(x => x.Text == "keep") == 1 && s.History.First(x => x.Text == "keep").Pinned);
                Check("case-insensitive search and pin filter", HistoryLogic.Search(s, "KEEP", "固定").Count() == 1);
                var snapshot = s.Snapshot(); s.History[0].Pinned = false; s.Settings.Days = 11;
                Check("background save snapshot isolates mutable state", snapshot.History[0].Pinned && snapshot.Settings.Days == 10);
                s.History[0].Pinned = true;
                var budget = new State(); budget.Settings.MaxItems = 0; budget.Settings.Days = 0;
                for (int i = 0; i < 10; i++) budget.History.Add(new Clip { Kind = "Image", Image = new byte[8 * 1024 * 1024], Pinned = i == 0 });
                HistoryLogic.Prune(budget, DateTime.UtcNow);
                Check("storage budget prunes oldest ordinary items", budget.History.Sum(x => x.Weight) <= 64L * 1024 * 1024 && budget.History[0].Pinned);
                budget = null;
                var detector = new ShakeLogic();
                Check("mouse-up does not trigger", !detector.Feed(100, false, 0));
                detector.Feed(100, true, 0); detector.Feed(140, true, 100); detector.Feed(100, true, 200); detector.Feed(140, true, 300);
                Check("old small-amplitude gesture no longer triggers", !detector.Feed(100, true, 400));
                detector.Feed(0, false, 450); detector.Feed(100, true, 500); detector.Feed(180, true, 600); detector.Feed(100, true, 700); detector.Feed(180, true, 800); detector.Feed(100, true, 900);
                Check("four deliberate reversals request drag validation", detector.Feed(180, true, 1000));
                Check("one validation request per held drag", !detector.Feed(100, true, 1100));
                detector.Feed(0, false, 600); detector.Feed(0, true, 700); detector.Feed(3, true, 800);
                Check("tiny movements do not summon", !detector.Feed(0, true, 900));
                detector = new ShakeLogic(); detector.Feed(0, true, 0); detector.Feed(80, true, 1200); detector.Feed(0, true, 2400); detector.Feed(80, true, 3600); detector.Feed(0, true, 4800);
                Check("slow movements do not request validation", !detector.Feed(80, true, 6000));
                detector = new ShakeLogic(); detector.Feed(0, true, 0); detector.Feed(80, true, 20); detector.Feed(0, true, 40); detector.Feed(80, true, 60); detector.Feed(0, true, 80);
                Check("brief mouse jitter does not request validation", !detector.Feed(80, true, 100));
                var unsupportedDrag = new DataObject("custom application object", "opaque");
                Check("unsupported drag cannot reveal or save", !DropContent.HasSupportedFormat(unsupportedDrag) && DropContent.Read(unsupportedDrag, "qa") == null);
                var emptyDrag = new DataObject(); emptyDrag.SetText("  ");
                Check("empty text drag is rejected", DropContent.Read(emptyDrag, "qa") == null);
                var concealedDrag = new DataObject(); concealedDrag.SetText("hidden"); concealedDrag.SetData("ExcludeClipboardContentFromMonitorProcessing", "1");
                Check("confidential drag is rejected before reveal", !DropContent.HasSupportedFormat(concealedDrag));
                var data = new DataObject(); data.SetText("中文测试 https://example.com");
                Check("Unicode clipboard parser", ClipboardService.Read(data, "test").Text.Contains("中文"));
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", "1");
                Check("sensitive marker is excluded", ClipboardService.Read(data, "test") == null);
                data = new DataObject(); data.SetText("secret"); data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
                Check("Windows history opt-out is honored", ClipboardService.Read(data, "test") == null);
                Check("application exclusion exact match", ClipboardService.ExcludedSource("KeePassXC", "keepassxc.exe;bitwarden") && !ClipboardService.ExcludedSource("notepad", "keepassxc.exe;bitwarden"));
                var own = ClipboardService.ToData(new Clip { Kind = "Text", Text = "roundtrip" });
                Check("own clipboard writes are not recaptured", ClipboardService.Read(own, "test") == null && own.GetText() == "roundtrip");
                var vault = new Vault(Path.Combine(folder, "vault-test")); vault.SaveBytes(vault.Serialize(s));
                var reload = vault.Load();
                Check("encrypted persistence reload", reload.History.Count == 3 && reload.History.Any(x => x.Pinned));
                Check("no plaintext clipboard on disk", !Encoding.UTF8.GetString(File.ReadAllBytes(vault.FilePath)).Contains("keep"));
                var legacyImageJson = Encoding.UTF8.GetBytes("{\"Kind\":\"Image\",\"Image\":[1,2,3,4],\"ImageVersion\":1}");
                Clip legacyImage;
                using (var stream = new MemoryStream(legacyImageJson)) legacyImage = (Clip)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(Clip)).ReadObject(stream);
                Check("legacy image array remains readable", legacyImage.Image.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
                legacyImage.Created = DateTime.UtcNow; legacyImage.Id = Guid.NewGuid().ToString("N");
                var compactState = new State(); compactState.History.Add(legacyImage);
                string compactJson = Encoding.UTF8.GetString(vault.Serialize(compactState));
                Check("new image storage uses compact base64 without changing bytes", compactJson.Contains("ImageBase64") && !compactJson.Contains("\"Image\":["));
                vault.SaveBytes(vault.Serialize(compactState));
                Check("compact image storage round-trips exact bytes", vault.Load().History[0].Image.SequenceEqual(legacyImage.Image));
                File.WriteAllText(vault.FilePath, "corrupt test");
                Check("corrupt history preserves recovery copy", vault.Load().History.Count == 0 && Directory.GetFiles(vault.Folder, "*.unreadable-*").Length == 1);
                var startup = new StartupRegistration(startupTestRoot + "\\Run", startupTestRoot + "\\Approved", F4Launcher.StartupCommand);
                c = new Controller(Path.Combine(folder, "ui-profile"), true, false, startup); c.Start(false); Pump(120);
                Check("native global hotkeys registered", c.HotkeyShelf && c.HotkeyHistory);
                Check("native clipboard listener registered", c.ClipboardListening);
                int probeReveals = 0;
                using (var idleProbe = new DragRevealProbe(clip => probeReveals++))
                {
                    idleProbe.Begin(new Point(-30000, -30000)); Pump(950);
                    Check("validation without an actual OLE drag never reveals and expires", probeReveals == 0 && !idleProbe.IsChecking);
                }
                Check("default floating panel is compact and borderless", c.Window.Width == 420 && c.Window.Height == 560 && c.Window.WindowStyle == WindowStyle.None);
                var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
                var inverse = PresentationSource.FromVisual(c.Window).CompositionTarget.TransformFromDevice;
                var bottomRight = inverse.Transform(new Point(area.Right, area.Bottom));
                Check("default placement leaves space above taskbar at bottom right", Math.Abs(c.Window.Left + c.Window.Width - bottomRight.X + 16) < 2 && Math.Abs(c.Window.Top + c.Window.Height - bottomRight.Y + 16) < 2);
                var smallArea = new Rect(-800, -100, 320, 340);
                Check("floating bounds fit small or negative-coordinate desktop work areas", smallArea.Contains(WindowAppearance.BottomRight(smallArea, new Size(420, 560), 16)));
                c.UI<CheckBox>("StartupCheck").IsChecked = true;
                c.UI<CheckBox>("StartupCheck").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("startup toggle writes immediately without Save Settings", startup.Enabled);
                Click(c, "HistoryTab"); Click(c, "SettingsTab");
                Check("startup toggle remains checked after page navigation", c.UI<CheckBox>("StartupCheck").IsChecked == true);
                Check("startup enabled state survives a new registration reader", new StartupRegistration(startupTestRoot + "\\Run", startupTestRoot + "\\Approved", F4Launcher.StartupCommand).Enabled);
                using (var approval = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupTestRoot + "\\Approved"))
                { approval.SetValue("DropKitWindows", new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }); approval.SetValue("UnrelatedApp", "keep"); }
                Check("Windows disabled startup is reflected by settings", !startup.Enabled);
                Click(c, "SettingsTab"); c.UI<CheckBox>("StartupCheck").IsChecked = true;
                c.UI<CheckBox>("StartupCheck").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                using (var approval = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(startupTestRoot + "\\Approved"))
                    Check("explicit enabling repairs only this app's disabled startup", startup.Enabled && (string)approval.GetValue("UnrelatedApp") == "keep");
                c.UI<CheckBox>("StartupCheck").IsChecked = false;
                c.UI<CheckBox>("StartupCheck").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check("startup disable also persists immediately", !startup.Enabled);
                c.UI<CheckBox>("TopmostCheck").IsChecked = false;
                c.UI<CheckBox>("TopmostCheck").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Click(c, "HistoryTab"); Click(c, "SettingsTab");
                Check("window switch persists without extra Save Settings click", !c.Vault.Load().Settings.Topmost && c.UI<CheckBox>("TopmostCheck").IsChecked == false);
                c.SelectPage("Shelf");
                Snapshot(c.Window, Path.Combine(folder, "01-shelf-empty.png"));
                string fixtures = Path.Combine(folder, "fixtures"); Directory.CreateDirectory(fixtures);
                string first = Path.Combine(fixtures, "项目说明.txt"); File.WriteAllText(first, "DropKit Windows QA fixture", Encoding.UTF8);
                string second = Path.Combine(fixtures, "设计灵感.md"); File.WriteAllText(second, "test");
                string video = Path.Combine(fixtures, "视频文件验收.mp4"); File.WriteAllBytes(video, new byte[] { 0, 0, 0, 24 });
                var videoData = new DataObject(DataFormats.FileDrop, new[] { video });
                Check("video file drag is recognized as storable content", DropContent.Read(videoData, "qa").Files.Single() == video);
                var goneData = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(fixtures, "does-not-exist.png") });
                Check("missing-file drag cannot reveal", DropContent.Read(goneData, "qa") == null);
                c.AddFiles(new[] { first, second, first, fixtures });
                Check("shelf dedup and directory support", c.State.Shelf.Count == 3 && c.UI<ListBox>("FileList").Items.Count == 3);
                Click(c, "SelectFilesButton"); Check("shelf select-all button selects every staged item", c.UI<ListBox>("FileList").SelectedItems.Count == 3); c.UI<ListBox>("FileList").SelectedItems.Clear();
                var fileData = new DataObject(DataFormats.FileDrop, new[] { first, second });
                var fileClip = ClipboardService.Read(fileData, "explorer");
                Check("multi-file clipboard formats", fileClip.Files.Length == 2 && ((string[])ClipboardService.ToData(fileClip).GetData(DataFormats.FileDrop)).Length == 2);
                Snapshot(c.Window, Path.Combine(folder, "02-shelf-grid.png"));
                Click(c, "ViewButton"); Snapshot(c.Window, Path.Combine(folder, "03-shelf-list.png"));
                Click(c, "CompactButton");
                Check("compact layout reachable with draggable header retained", c.Window.Width == 380 && c.UI<FrameworkElement>("Header").Visibility == Visibility.Visible);
                Snapshot(c.Window, Path.Combine(folder, "04-shelf-compact.png")); Click(c, "CompactButton");
                c.UI<ListBox>("FileList").SelectedIndex = 0; Click(c, "RemoveFilesButton");
                Check("removing shelf entry never deletes file", File.Exists(first) && c.State.Shelf.Count == 2);
                c.RegisterWebsiteAsset(new PinterestAsset { FilePath = video, Kind = "视频", OriginUrl = "https://www.pinterest.com/pin/123/" });
                c.SaveNow();
                Check("completed website media joins shelf and dated file history with origin", c.State.Shelf.Contains(video) && c.Vault.Load().History.Any(x => x.Files != null && x.Files.Contains(video) && x.OriginUrl == "https://www.pinterest.com/pin/123/" && x.IsVideo));
                HistoryLogic.Add(c.State, new Clip { Kind = "Files", Files = new[] { video }, Source = "copied again" });
                Check("re-copying website media keeps the original source link", c.State.History.Single(x => x.IsVideo).OriginUrl == "https://www.pinterest.com/pin/123/");
                c.State.History.Clear(); c.UI<ListBox>("FileList").SelectedItem = c.UI<ListBox>("FileList").Items.Cast<ShelfFile>().First(x => x.Path == video); Click(c, "RemoveFilesButton");
                var image = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)180, 4096).ToArray(), 128);
                var imageData = new DataObject(); imageData.SetImage(image);
                var imageClip = ClipboardService.Read(imageData, "qa");
                Check("bitmap encoded as PNG and restored", imageClip.Image.Length > 0 && ClipboardService.ToData(imageClip).GetImage().PixelWidth == 32);
                var textDrag = new DataObject(); textDrag.SetText("拖入文字保存验收");
                c.State.Settings.Capture = false;
                bool textDropped = c.CollectDrop(textDrag, "拖入验收"); c.SaveNow();
                Check("intentional text drop saves even when automatic capture is paused", textDropped && c.Vault.Load().History.Any(x => x.Text == "拖入文字保存验收"));
                bool imageDropped = c.CollectDrop(imageData, "拖入验收"); c.SaveNow();
                Check("image drop saves a decoded independent PNG", imageDropped && c.Vault.Load().History.Any(x => x.Kind == "Image" && x.Image.SequenceEqual(imageClip.Image)));
                Check("unsupported drop leaves stored history untouched", !c.CollectDrop(unsupportedDrag, "qa") && c.State.History.Count == 2);
                c.State.History.Clear(); c.State.Settings.Capture = true; c.RefreshHistory();
                HistoryLogic.Add(c.State, imageClip); HistoryLogic.Add(c.State, fileClip);
                HistoryLogic.Add(c.State, new Clip { Kind = "Text", Text = "https://github.com/chenyuxiaojin/DropKit", Source = "浏览器" });
                HistoryLogic.Add(c.State, new Clip { Kind = "Text", Text = "让文件与灵感，随时触手可及。\n这是一条验证用的剪贴板记录。", Source = "记事本" });
                c.RefreshHistory(); Click(c, "HistoryTab"); Snapshot(c.Window, Path.Combine(folder, "05-clipboard.png"));
                var yesterday = new Clip { Kind = "Text", Text = "昨天的验收文字", Pinned = true, Created = DateTime.Now.Date.AddDays(-1).AddHours(12).ToUniversalTime(), Source = "日期验收" };
                HistoryLogic.Add(c.State, yesterday); c.RefreshHistory();
                var grouped = System.Windows.Data.CollectionViewSource.GetDefaultView(c.UI<ListBox>("ClipList").ItemsSource);
                Check("history renders separate local-date groups with newest day first", grouped.Groups.Count == 2 && (string)((System.Windows.Data.CollectionViewGroup)grouped.Groups[0]).Name == DateTime.Now.ToString("yyyy-MM-dd"));
                Click(c, "ImageFilterButton");
                Check("image category excludes text and regular files", c.UI<ListBox>("ClipList").Items.Count == 1 && c.UI<ListBox>("ClipList").Items.Cast<Clip>().All(x => x.IsImage));
                Click(c, "TextFilterButton");
                Check("text category contains text plus links without images", c.UI<ListBox>("ClipList").Items.Count == 3 && c.UI<ListBox>("ClipList").Items.Cast<Clip>().All(x => x.Kind == "Text"));
                var todayText = c.VisibleDay(DateTime.Now.ToString("yyyy-MM-dd"));
                var dayPayload = ClipTransfer.Prepare(todayText, Path.Combine(folder, "day-copy"));
                Check("day text copy combines only that date and reports record count", todayText.Length == 2 && dayPayload.Count == 2 && dayPayload.Text.Contains("触手可及") && !dayPayload.Text.Contains("昨天"));
                c.DeleteRecords(c.VisibleDay(yesterday.DateKey));
                var recyclingTimeout = System.Diagnostics.Stopwatch.StartNew(); while (c.IsRecycling && recyclingTimeout.ElapsedMilliseconds < 15000) Pump(50); c.SaveNow();
                Check("day delete is restricted to current date and category", !c.State.History.Contains(yesterday) && todayText.All(x => c.State.History.Contains(x)) && c.Vault.Load().History.All(x => x.Id != yesterday.Id));
                Check("deleted day offers recovery through Windows recycle bin", c.UI<Button>("UndoDeleteButton").Visibility == Visibility.Visible);
                Snapshot(c.Window, Path.Combine(folder, "12-dated-text.png"));
                c.State.History.Remove(yesterday); Click(c, "AllFilterButton"); c.RefreshHistory();
                c.UI<TextBox>("SearchBox").Text = "触手";
                Check("live search updates rendered list", c.UI<ListBox>("ClipList").Items.Count == 1);
                c.UI<ListBox>("ClipList").SelectedIndex = 0; Click(c, "PinButton");
                Check("pin action updates model", ((Clip)c.UI<ListBox>("ClipList").SelectedItem).Pinned);
                Click(c, "PauseButton"); Check("pause setting updates", !c.State.Settings.Capture);
                Click(c, "PauseButton"); c.UI<TextBox>("SearchBox").Clear();
                Click(c, "SettingsTab"); Snapshot(c.Window, Path.Combine(folder, "06-settings.png"));
                c.UI<TextBox>("LimitBox").Text = "bad"; Click(c, "SaveSettingsButton");
                Check("invalid settings do not overwrite", c.State.Settings.MaxItems == 200);
                c.UI<TextBox>("LimitBox").Text = "150"; Click(c, "SaveSettingsButton");
                Check("valid settings persist", c.Vault.Load().Settings.MaxItems == 150);
                string inbox = Path.Combine(folder, "watch-test"); Directory.CreateDirectory(inbox);
                c.State.Settings.WatchEnabled = true; c.State.Settings.WatchFolder = inbox; c.SetupWatcher();
                string incoming = Path.Combine(inbox, "new-file.txt"); File.WriteAllText(incoming, "new"); Pump(150); c.ProcessPendingFiles(); c.ProcessPendingFiles();
                Check("real filesystem watcher imports stable new file", c.State.Shelf.Contains(incoming));
                string temporary = Path.Combine(inbox, "download.crdownload"); File.WriteAllText(temporary, "partial"); Pump(100); c.ProcessPendingFiles(); c.ProcessPendingFiles();
                Check("incomplete download excluded", !c.State.Shelf.Contains(temporary));
                string completed = Path.Combine(inbox, "download.pdf"); File.Move(temporary, completed); Pump(100); c.ProcessPendingFiles(); c.ProcessPendingFiles();
                Check("renamed completed download imported", c.State.Shelf.Contains(completed));
                c.SelectPage("Shelf"); c.Window.Close();
                Check("close hides without exiting", !c.Window.IsVisible);
                Native.PostMessage(new WindowInteropHelper(c.Window).Handle, 0x0312, new IntPtr(12), IntPtr.Zero); Pump(100);
                Check("native hotkey message opens clipboard", c.Window.IsVisible && c.UI<FrameworkElement>("HistoryPage").Visibility == Visibility.Visible);
                c.Window.WindowState = WindowState.Minimized; Pump(60);
                Native.PostMessage(new WindowInteropHelper(c.Window).Handle, 0x0312, new IntPtr(12), IntPtr.Zero); Pump(100);
                Check("open hotkey restores minimized window", c.Window.IsVisible && c.Window.WindowState == WindowState.Normal);
                Native.PostMessage(new WindowInteropHelper(c.Window).Handle, 0x0312, new IntPtr(12), IntPtr.Zero); Pump(100);
                Check("repeated F4 request hides clipboard", !c.Window.IsVisible);
                Native.PostMessage(new WindowInteropHelper(c.Window).Handle, 0x0312, new IntPtr(12), IntPtr.Zero); Pump(100);
                Check("next F4 request opens clipboard again", c.Window.IsVisible);
                c.Window.Left = -30000; c.Window.Top = -30000;
                c.ShowPage("History", false, false); Pump(100);
                Check("reopen brings off-screen window back into view", c.Window.Left > -30000 && c.Window.Top > -30000);
                c.Changed(); Pump(950); c.SaveNow(); Check("background save flush persists shelf", c.Vault.Load().Shelf.Contains(incoming));
                c.Window.Width = 390; c.Window.Height = 520; Snapshot(c.Window, Path.Combine(folder, "07-narrow-clipboard.png"));
                c.SelectPage("Settings"); Snapshot(c.Window, Path.Combine(folder, "08-narrow-settings.png"));
                c.Window.Width = 680; c.Window.Height = 810; c.SelectPage("History");
                c.State.History.Clear();
                var landscape = ColorImage(640, 360, 220, 75, 230);
                var portrait = ColorImage(360, 640, 60, 165, 240);
                var wide = ColorImage(1200, 160, 230, 170, 80);
                landscape.Created = DateTime.Now.Date.AddDays(-1).AddHours(12).ToUniversalTime();
                wide.Created = DateTime.Now.Date.AddDays(-2).AddHours(12).ToUniversalTime();
                HistoryLogic.Add(c.State, landscape); HistoryLogic.Add(c.State, portrait); HistoryLogic.Add(c.State, wide); c.RefreshHistory();
                Snapshot(c.Window, Path.Combine(folder, "09-multiple-images.png"));
                Check("different-aspect images retain independent thumbnails", c.State.History.Select(x => x.Thumbnail.PixelHeight).Distinct().Count() == 3);
                Click(c, "SelectImagesButton"); Check("select all images selects every captured image", c.UI<ListBox>("ClipList").SelectedItems.Count == 3);
                HistoryLogic.Add(c.State, ColorImage(400, 300, 120, 210, 150)); c.RefreshHistory();
                Check("inserting another image preserves batch selection", c.UI<ListBox>("ClipList").SelectedItems.Count == 3);
                HistoryLogic.Add(c.State, new Clip { Kind = "Image", Image = landscape.Image, ImageVersion = 1, Source = "repeat-copy" }); c.RefreshHistory();
                Check("re-copying an existing image preserves its selected identity", c.UI<ListBox>("ClipList").SelectedItems.Count == 3 && c.UI<ListBox>("ClipList").SelectedItems.Contains(landscape));
                c.UI<ListBox>("ClipList").SelectedItems.Clear(); c.UI<ListBox>("ClipList").SelectedItem = landscape;
                bool previewPassed = false; Exception previewError = null;
                var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                previewTimer.Tick += delegate
                {
                    var preview = app.Windows.Cast<Window>().FirstOrDefault(x => x.Title == "预览 · 图片");
                    if (preview == null) return; previewTimer.Stop();
                    try { var content = ((ContentControl)((DockPanel)preview.Content).Children[1]).Content as Image; previewPassed = content != null && ((BitmapSource)content.Source).PixelWidth == 640; Snapshot(preview, Path.Combine(folder, "10-image-preview.png")); }
                    catch (Exception ex) { previewError = ex; }
                    finally { preview.Close(); }
                };
                previewTimer.Start(); Click(c, "PreviewButton");
                Check("selected image opens visible preview with correct pixels", previewPassed && previewError == null);
                c.Window.Width = 390; c.Window.Height = 650; Snapshot(c.Window, Path.Combine(folder, "11-narrow-image-actions.png"));
                c.Quit();
                results.Add("ALL " + results.Count + " CHECKS PASSED"); File.WriteAllLines(Path.Combine(folder, "test-results.txt"), results, Encoding.UTF8); return 0;
            }
            catch (Exception ex)
            {
                results.Add(ex.ToString()); File.WriteAllLines(Path.Combine(folder, "test-results.txt"), results, Encoding.UTF8);
                if (c != null) c.Dispose(); return 1;
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(startupTestRoot, false); }
        }
    }
}
