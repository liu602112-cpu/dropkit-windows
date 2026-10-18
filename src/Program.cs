using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("DropKit Windows")]
[assembly: AssemblyDescription("DropKit file shelf and clipboard history - community Windows port")]
[assembly: AssemblyVersion("1.5.0.0")]

namespace DropKitWindows
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Native.SetProcessDPIAware();
            if (args.Contains("--exit-all")) F4Launcher.Stop();
            if (args.Contains("--exit") || args.Contains("--exit-all")) { Native.PostMessage(new IntPtr(0xFFFF), Native.RegisterWindowMessage("DropKitWindows.Quit"), IntPtr.Zero, IntPtr.Zero); return 0; }
            if (args.Contains("--f4-launcher")) return F4Launcher.Run(args.Contains("--background"));
            bool test = args.Contains("--self-test");
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropKitWindows");
            int dataArg = Array.IndexOf(args, "--data-dir");
            if (dataArg >= 0 && dataArg + 1 < args.Length) folder = Path.GetFullPath(args[dataArg + 1]);
            if (test) return SelfTests.Run(folder);
            F4Launcher.EnsureRunning();
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            using (var launch = new LaunchBroker("Local\\DropKitWindows-" + sid))
            {
                if (!launch.IsPrimary)
                {
                    if (args.Contains("--toggle")) { Native.AllowExistingActivation(); launch.RequestToggle(); return 0; }
                    if (!args.Contains("--background") || args.Contains("--history"))
                    {
                        bool found = Native.AllowExistingActivation(); launch.RequestOpen();
                        if (!found) Native.PostMessage(new IntPtr(0xFFFF), Native.RegisterWindowMessage("DropKitWindows.Show"), IntPtr.Zero, IntPtr.Zero);
                    }
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var controller = new Controller(folder, false);
                    if (args.Contains("--trace-startup"))
                    {
                        Action<string> trace = phase => File.AppendAllText(Path.Combine(folder, "startup-trace.txt"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + phase + " visible=" + controller.Window.IsVisible + " state=" + controller.Window.WindowState + " bounds=" + controller.Window.Left + "," + controller.Window.Top + "," + controller.Window.Width + "," + controller.Window.Height + Environment.NewLine);
                        controller.Window.IsVisibleChanged += delegate { trace("visibility"); };
                        controller.Window.Loaded += delegate { trace("loaded"); };
                        controller.Window.ContentRendered += delegate { trace("rendered"); };
                        controller.Window.Closed += delegate { trace("closed"); };
                        trace("constructed");
                    }
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        controller.Status("操作未完成：" + e.Exception.Message); e.Handled = true;
                    };
                    app.SessionEnding += delegate { controller.SaveNow(); };
                    controller.Start(true);
                    int importArg = Array.IndexOf(args, "--pinterest");
                    if (importArg >= 0 && importArg + 1 < args.Length) controller.BeginWebsiteImport(args[importArg + 1]);
                    if (args.Contains("--toggle")) app.Dispatcher.BeginInvoke(new Action(controller.ToggleWindow), DispatcherPriority.ApplicationIdle);
                    else if (!args.Contains("--background") || args.Contains("--history"))
                        app.Dispatcher.BeginInvoke(new Action(() => controller.ShowPage("History", false, false)), DispatcherPriority.ApplicationIdle);
                    var reopen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    reopen.Tick += delegate { if (launch.ConsumeOpen()) controller.ShowPage("History", false, false); while (launch.ConsumeToggle()) controller.ToggleWindow(); };
                    reopen.Start();
                    try { app.Run(); } finally { reopen.Stop(); controller.Dispose(); }
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("DropKit 未能启动。\n" + ex.Message, "DropKit", MessageBoxButton.OK, MessageBoxImage.Error); return 1;
                }
            }
        }
    }

    public class Controller : IDisposable
    {
        public readonly Window Window;
        public readonly State State;
        public readonly Vault Vault;
        public bool HotkeyShelf, HotkeyHistory, ClipboardListening;
        readonly bool testing;
        readonly StartupRegistration startupRegistration;
        bool testingStartupEnabled;
        public bool AcrylicEnabled { get; private set; }
        readonly bool captureNativeEvents;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly ShakeLogic shake = new ShakeLogic();
        DragRevealProbe dragReveal;
        readonly DispatcherTimer mouseTimer, saveTimer, clipTimer, housekeeping;
        readonly List<ShelfFile> shelf = new List<ShelfFile>();
        readonly ObservableCollection<Clip> visibleHistory = new ObservableCollection<Clip>();
        readonly object pendingLock = new object();
        readonly Dictionary<string, PendingFile> pending = new Dictionary<string, PendingFile>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher watcher;
        Forms.NotifyIcon tray;
        HwndSource source;
        IntPtr handle, previousWindow;
        uint lastSequence, pendingSequence, showMessage, quitMessage;
        int clipAttempts;
        string clipboardSource = "";
        string page = "Shelf";
        bool gridView = true, compact, closing, dragging, dirty, pendingActivation;
        Task pendingSave = Task.FromResult(0);
        public bool HasPendingSave { get { return dirty || !pendingSave.IsCompleted; } }
        int transferGeneration;
        CancellationTokenSource websiteCancellation = new CancellationTokenSource();
        int websiteGeneration;
        readonly HashSet<string> websitePending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim websiteSlots = new SemaphoreSlim(2);
        bool recycling;
        public bool IsRecycling { get { return recycling; } }
        public Controller(string folder, bool test, bool liveClipboardTest = false, StartupRegistration startupOverride = null)
        {
            testing = test;
            startupRegistration = startupOverride ?? (test ? null : new StartupRegistration());
            captureNativeEvents = !test || liveClipboardTest;
            Vault = new Vault(folder); State = Vault.Load();
            int repairedImages = RepairLegacyImages();
            int loadedCount = State.History.Count; HistoryLogic.Prune(State, DateTime.UtcNow); dirty = repairedImages > 0 || loadedCount != State.History.Count;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml")) Window = (Window)XamlReader.Load(stream);
            WindowAppearance.Configure(Window);
            Window.Topmost = State.Settings.Topmost;
            UI<ListBox>("ClipList").ItemsSource = visibleHistory;
            CollectionViewSource.GetDefaultView(visibleHistory).GroupDescriptions.Add(new PropertyGroupDescription("DateKey"));
            foreach (string path in State.Shelf.Distinct(StringComparer.OrdinalIgnoreCase)) shelf.Add(new ShelfFile { Path = path });
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            saveTimer.Tick += delegate { saveTimer.Stop(); QueueSave(); };
            clipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            clipTimer.Tick += delegate { CapturePending(); };
            mouseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            mouseTimer.Tick += delegate { PollMouse(); };
            housekeeping = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            housekeeping.Tick += delegate { ProcessPendingFiles(); };
            Wire(); RefreshShelf(); RefreshHistory(); LoadSettings(); SelectPage("Shelf");
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!closing) { e.Cancel = true; Window.Hide(); SaveNow(); } };
            Window.SourceInitialized += delegate { InitializeNative(); };
            Window.ContentRendered += delegate
            {
                // WPF/WindowChrome can apply the inherited startup show state
                // after Show() returns. Complete an explicit open after render.
                if (!testing && pendingActivation && Window.IsVisible && !closing)
                { Native.ShowWindow(handle, Window.WindowState == WindowState.Maximized ? 3 : 5); Window.Activate(); Native.SetForegroundWindow(handle); }
                pendingActivation = false;
            };
            Window.PreviewKeyDown += WindowKeyDown;
            if (repairedImages > 0) Status("已恢复 " + repairedImages + " 张旧版透明度异常的图片，原记录已加密备份。");
        }
        int RepairLegacyImages()
        {
            int repaired = 0;
            foreach (var clip in State.History.Where(x => x.Kind == "Image" && x.ImageVersion == 0))
            {
                try
                {
                    byte[] fixedImage = ImageTransfer.RepairLegacy(clip.Image);
                    if (fixedImage == null) continue;
                    string backup = Vault.FilePath + ".before-image-fix.bak";
                    if (repaired == 0 && File.Exists(Vault.FilePath) && !File.Exists(backup)) File.Copy(Vault.FilePath, backup, false);
                    clip.Image = fixedImage; clip.ImageVersion = 1; clip.ComputeHash(); repaired++;
                }
                catch { }
            }
            return repaired;
        }
        public T UI<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        public void Status(string message) { UI<TextBlock>("StatusText").Text = message; }
        void Click(string name, Action action) { UI<Button>(name).Click += delegate { try { action(); } catch (Exception ex) { Status("操作未完成：" + ex.Message); } }; }
        void Wire()
        {
            Click("ShelfTab", () => SelectPage("Shelf")); Click("HistoryTab", () => SelectPage("History")); Click("SettingsTab", () => { LoadSettings(); SelectPage("Settings"); });
            Click("HideButton", () => Window.Hide());
            UI<FrameworkElement>("Header").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.Handled || UI<Button>("HideButton").IsMouseOver || e.ButtonState != MouseButtonState.Pressed) return;
                try { Window.DragMove(); } catch (InvalidOperationException) { }
            };
            UI<CheckBox>("StartupCheck").Click += delegate { SaveStartupImmediately(); };
            foreach (string switchName in new[] { "ShakeCheck", "TopmostCheck", "CaptureCheck", "WatchCheck" })
                UI<CheckBox>(switchName).Click += delegate { SaveSwitchesImmediately(); };
            Click("AddButton", AddDialog); Click("CopyFilesButton", CopyFiles); Click("RemoveFilesButton", RemoveFiles);
            Click("SelectFilesButton", () => UI<ListBox>("FileList").SelectAll());
            Click("DeleteFilesButton", () => RecycleSelection(new Clip[0], UI<ListBox>("FileList").SelectedItems.Cast<ShelfFile>().Select(x => x.Path).ToArray(), false));
            Click("DeleteAllFilesButton", () => ConfirmRecycle(new Clip[0], shelf.Select(x => x.Path).ToArray(), false, "全部暂存文件"));
            Click("DeleteAllClipsButton", () => ConfirmRecycle(State.History.ToArray(), new string[0], true, "全部剪贴板记录（包含固定）"));
            Click("AddLinkButton", AddWebsiteDialog);
            Click("ViewButton", () => { gridView = !gridView; RefreshShelf(); });
            Click("CompactButton", ToggleCompact);
            Click("CopyClipButton", () => CopyClip(false)); Click("PasteButton", () => CopyClip(true));
            Click("SelectImagesButton", SelectImages); Click("CopyAllImagesButton", () => CopyImageBatch(ClipTransfer.OnlyImages(visibleHistory)));
            Click("SaveImagesButton", SaveImages);
            Click("PreviewButton", Preview); Click("PinButton", Pin); Click("DeleteClipButton", DeleteClip);
            Click("PauseButton", () => { State.Settings.Capture = !State.Settings.Capture; lastSequence = Native.GetClipboardSequenceNumber(); clipTimer.Stop(); RefreshHistory(); Changed(); Status(State.Settings.Capture ? "已恢复记录 · 从下一次复制开始" : "已暂停剪贴板记录"); });
            Click("SaveSettingsButton", SaveSettings);
            Click("UndoDeleteButton", () => Process.Start("explorer.exe", "shell:RecycleBinFolder"));
            Click("AllFilterButton", () => SetHistoryFilter("全部"));
            Click("ImageFilterButton", () => SetHistoryFilter("图片"));
            Click("TextFilterButton", () => SetHistoryFilter("文字"));
            Click("FileFilterButton", () => SetHistoryFilter("文件"));
            Click("PinnedFilterButton", () => SetHistoryFilter("固定"));
            Click("ChooseFolderButton", ChooseFolder);
            Click("DataFolderButton", () => Process.Start("explorer.exe", "\"" + Vault.Folder + "\""));
            Click("ExportFolderButton", () => { Directory.CreateDirectory(TransferCache); Process.Start("explorer.exe", "\"" + TransferCache + "\""); });
            Click("WebsiteFolderButton", () => { Directory.CreateDirectory(PinterestMedia.DownloadFolder); Process.Start("explorer.exe", "\"" + PinterestMedia.DownloadFolder + "\""); });
            Click("ClearHistoryButton", () => ConfirmRecycle(State.History.ToArray(), new string[0], true, "全部剪贴板记录（包含固定）"));
            UI<TextBox>("SearchBox").TextChanged += delegate { RefreshHistory(); };
            UI<TextBox>("SearchBox").PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                var list = UI<ListBox>("ClipList");
                if ((e.Key == Key.Down || e.Key == Key.Enter) && list.Items.Count > 0)
                {
                    if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                    if (e.Key == Key.Enter) CopyClip(false); else { list.Focus(); var entry = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem; if (entry != null) entry.Focus(); }
                    e.Handled = true;
                }
            };
            UI<ComboBox>("FilterBox").SelectionChanged += delegate { RefreshHistory(); };
            var files = UI<ListBox>("FileList");
            WireDrag(files, () => new[] { new Clip { Kind = "Files", Files = files.SelectedItems.Cast<ShelfFile>().Select(x => x.Path).ToArray() } });
            files.MouseDoubleClick += delegate { RevealFile(); };
            var menu = new ContextMenu();
            Menu(menu, "复制文件", CopyFiles); Menu(menu, "打开文件", () => { var item = UI<ListBox>("FileList").SelectedItem as ShelfFile; if (item != null && item.Exists) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }); Menu(menu, "在文件夹中显示", RevealFile); Menu(menu, "从暂存移除", RemoveFiles);
            files.ContextMenu = menu;
            var clips = UI<ListBox>("ClipList");
            WireDrag(clips, () => ForCurrentCategory(clips.SelectedItems.Cast<Clip>().ToArray()));
            clips.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(HistoryAction));
            clips.SelectionChanged += delegate { UpdateImageCount(); };
            clips.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) { if (!FromButton(e.OriginalSource as DependencyObject)) CopyClip(false); };
            var clipMenu = new ContextMenu(); Menu(clipMenu, "复制", () => CopyClip(false)); Menu(clipMenu, "粘贴到原窗口", () => CopyClip(true));
            Menu(clipMenu, "固定 / 取消固定", Pin); Menu(clipMenu, "预览", Preview); Menu(clipMenu, "删除记录", DeleteClip); clips.ContextMenu = clipMenu;
            Menu(clipMenu, "打开原文件", () => { var item = SelectedClip(); if (item != null && item.Kind == "Files" && File.Exists(item.Files[0])) Process.Start(new ProcessStartInfo(item.Files[0]) { UseShellExecute = true }); });
            Menu(clipMenu, "打开来源网页", () => { var item = SelectedClip(); Uri uri; if (item != null && Uri.TryCreate(item.OriginUrl, UriKind.Absolute, out uri) && uri.Scheme == "https") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
            files.PreviewMouseRightButtonDown += SelectRightClick; clips.PreviewMouseRightButtonDown += SelectRightClick;
            Window.PreviewDragOver += DragOver; Window.PreviewDrop += Drop;
            Window.DragLeave += delegate { UI<Border>("DropOverlay").Visibility = Visibility.Collapsed; };
        }
        string TransferCache { get { return testing ? Path.Combine(Vault.Folder, "ExportedImages", "TransferCache") : ClipTransfer.ExportFolder; } }
        static bool FromButton(DependencyObject element)
        {
            while (element != null)
            {
                if (element is ButtonBase) return true;
                element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
            }
            return false;
        }
        void WireDrag(ListBox list, Func<Clip[]> selection)
        {
            Point start = new Point(); object anchor = null, deferred = null;
            list.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (FromButton(e.OriginalSource as DependencyObject)) { anchor = deferred = null; return; }
                start = e.GetPosition(list); deferred = null;
                var hit = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
                anchor = hit == null ? null : hit.DataContext;
                if (hit != null && hit.IsSelected && list.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None && e.ClickCount == 1)
                { deferred = anchor; list.Focus(); e.Handled = true; }
            };
            list.PreviewMouseLeftButtonUp += delegate
            {
                if (deferred != null) { list.SelectedItems.Clear(); list.SelectedItem = deferred; }
                anchor = deferred = null;
            };
            list.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) { anchor = deferred = null; return; }
                if (anchor == null || dragging || !list.SelectedItems.Contains(anchor)) return;
                Point point = e.GetPosition(list);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                bool topmost = Window.Topmost; dragging = true;
                try
                {
                    var payload = ClipTransfer.Prepare(selection(), TransferCache);
                    Window.Topmost = false;
                    Status("正在拖出 " + payload.Count + " 项 · 松开放入目标，按 Esc 取消");
                    var effect = DragDrop.DoDragDrop(list, payload.ToData(), DragDropEffects.Copy);
                    Status(effect == DragDropEffects.None ? "拖出已取消或目标不支持接收；也可以复制后粘贴。" : "已交给目标应用 " + payload.Count + " 项 · 请在目标应用确认导入结果");
                }
                catch (Exception ex) { Status("拖出未完成：" + ex.Message); }
                finally { dragging = false; anchor = deferred = null; Window.Topmost = topmost; UI<Border>("DropOverlay").Visibility = Visibility.Collapsed; }
                e.Handled = true;
            };
        }
        void SelectRightClick(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement((ListBox)sender, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item != null) item.IsSelected = true;
        }
        void Menu(ContextMenu menu, string title, Action action) { var item = new MenuItem { Header = title }; item.Click += delegate { try { action(); } catch (Exception ex) { Status(ex.Message); } }; menu.Items.Add(item); }
        void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Window.Hide(); e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox) return;
            if (page == "History")
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { UI<ListBox>("ClipList").SelectAll(); e.Handled = true; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { CopyClip(false); e.Handled = true; }
                if (e.Key == Key.Space) { Preview(); e.Handled = true; }
                if (e.Key == Key.Enter) { CopyClip(false); e.Handled = true; }
                if (e.Key == Key.Delete) { DeleteClip(); e.Handled = true; }
            }
            if (page == "Shelf" && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { UI<ListBox>("FileList").SelectAll(); e.Handled = true; }
            if (page == "Shelf" && e.Key == Key.Delete) { RecycleSelection(new Clip[0], UI<ListBox>("FileList").SelectedItems.Cast<ShelfFile>().Select(x => x.Path).ToArray(), false); e.Handled = true; }
        }
        void InitializeNative()
        {
            handle = new WindowInteropHelper(Window).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(WindowMessage);
            AcrylicEnabled = WindowAppearance.Apply(Window, source);
            PlaceBottomRight();
            showMessage = Native.RegisterWindowMessage("DropKitWindows.Show");
            quitMessage = Native.RegisterWindowMessage("DropKitWindows.Quit");
            if (!testing) Native.SetProp(handle, Native.MainWindowMarker, new IntPtr(1));
            HotkeyShelf = Native.RegisterHotKey(handle, 11, testing ? 0x4007u : 0x4006u, 0x53);
            HotkeyHistory = (!testing && F4Launcher.IsReady()) || Native.RegisterHotKey(handle, 12, testing ? 0x4007u : 0x4000u, 0x73);
            ClipboardListening = Native.AddClipboardFormatListener(handle); lastSequence = Native.GetClipboardSequenceNumber();
            string warning = (!HotkeyShelf ? "Ctrl+Shift+S 已被占用，可用托盘打开。 " : "") + (!HotkeyHistory ? "F4 已被占用，可双击程序或托盘打开。" : "");
            UI<TextBlock>("HotkeyStatus").Text = warning + (!testing && F4Launcher.IsReady() ? " F4 启动器已启用，退出剪贴板后仍可按 F4 打开。" : "");
            if (!string.IsNullOrEmpty(warning)) Status(warning);
            if (!ClipboardListening) Status("剪贴板监听未能启动，请退出后重新打开程序。");
        }
        IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (quitMessage != 0 && (uint)msg == quitMessage) { Quit(); handled = true; return IntPtr.Zero; }
            if (msg == 0x0312) { if (wp.ToInt32() == 11) ShowPage("Shelf", false, false); else ToggleWindow(); handled = true; }
            else if ((uint)msg == showMessage) { ShowPage("History", false, false); handled = true; }
            else if (msg == 0x031D && captureNativeEvents)
            {
                uint seq = Native.GetClipboardSequenceNumber();
                if (seq != lastSequence)
                {
                    lastSequence = seq; clipTimer.Stop();
                    if (State.Settings.Capture)
                    {
                        pendingSequence = seq; clipAttempts = 0;
                        clipboardSource = Native.ProcessName(Native.GetClipboardOwner());
                        string foreground = Native.ProcessName(Native.GetForegroundWindow());
                        if (ClipboardService.ExcludedSource(clipboardSource, State.Settings.Excluded) || ClipboardService.ExcludedSource(foreground, State.Settings.Excluded)) return IntPtr.Zero;
                        clipTimer.Start();
                    }
                }
            }
            return IntPtr.Zero;
        }
        void CapturePending()
        {
            clipTimer.Stop();
            if (!State.Settings.Capture || Native.GetClipboardSequenceNumber() != pendingSequence) return;
            try
            {
                var data = Clipboard.GetDataObject(); var item = ClipboardService.ReadCurrent(data, clipboardSource);
                if (Native.GetClipboardSequenceNumber() != pendingSequence) return;
                if (item != null) { HistoryLogic.Add(State, item); RefreshHistory(); Changed(); }
            }
            catch { if (++clipAttempts < 12) clipTimer.Start(); else Status("本次剪贴板正被占用，未记录；后续复制会继续监听。"); }
        }
        public void Start(bool background)
        {
            new WindowInteropHelper(Window).EnsureHandle();
            if (!testing)
            {
                dragReveal = new DragRevealProbe(clip => { if (State.Settings.Shake && !dragging) ShowPage(clip.Kind == "Files" ? "Shelf" : "History", true, false); });
                CreateTray(); mouseTimer.Start(); housekeeping.Start(); SetupWatcher();
            }
            if (!background) Window.Show();
            if (dirty) Changed();
            if (Vault.Warning != null) Status(Vault.Warning);
        }
        void CreateTray()
        {
            var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(iconStream), Text = "DropKit · 文件暂存与剪贴板", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("文件暂存    Ctrl+Shift+S", null, delegate { ShowPage("Shelf", false, false); });
            menu.Items.Add("剪贴板历史    F4", null, delegate { ShowPage("History", false, false); });
            menu.Items.Add("暂停 / 恢复记录", null, delegate { State.Settings.Capture = !State.Settings.Capture; lastSequence = Native.GetClipboardSequenceNumber(); clipTimer.Stop(); RefreshHistory(); Changed(); });
            menu.Items.Add("设置", null, delegate { LoadSettings(); ShowPage("Settings", false, false); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出剪贴板（F4 可重新打开）", null, delegate { Quit(); });
            menu.Items.Add("退出全部（停用 F4）", null, delegate { F4Launcher.Stop(); Quit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowPage("History", false, false); };
        }
        public void SelectPage(string selected)
        {
            page = selected;
            foreach (string name in new[] { "Shelf", "History", "Settings" })
            {
                UI<FrameworkElement>(name + "Page").Visibility = selected == name ? Visibility.Visible : Visibility.Collapsed;
                UI<Button>(name + "Tab").Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected == name ? "#EEFFFFFF" : "#00FFFFFF"));
                UI<Button>(name + "Tab").Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected == name ? "#3459C5" : "#66728A"));
            }
            if (selected == "History") RefreshHistory();
        }
        public void ShowPage(string selected, bool nearMouse, bool toggle)
        {
            if (toggle && Window.IsVisible && Window.WindowState != WindowState.Minimized && page == selected && Native.OwnWindow(Native.GetForegroundWindow())) { Window.Hide(); return; }
            IntPtr active = Native.GetForegroundWindow();
            if (active != IntPtr.Zero && !Native.OwnWindow(active)) previousWindow = active;
            SelectPage(selected);
            if (nearMouse)
            {
                Native.POINT p; Native.GetCursorPos(out p);
                var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea;
                var transform = source.CompositionTarget.TransformFromDevice;
                var pointer = transform.Transform(new Point(p.X, p.Y));
                var tl = transform.Transform(new Point(screen.Left, screen.Top));
                var br = transform.Transform(new Point(screen.Right, screen.Bottom));
                Window.Left = Math.Max(tl.X, Math.Min(pointer.X + 22, br.X - Window.Width));
                Window.Top = Math.Max(tl.Y, Math.Min(pointer.Y - 70, br.Y - Window.Height));
                Window.ShowActivated = false; Window.Show(); Window.ShowActivated = true;
            }
            else
            {
                bool restorePosition = !Window.IsVisible || Window.WindowState == WindowState.Minimized;
                pendingActivation = true;
                Window.ShowActivated = true;
                if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
                if (restorePosition) PlaceBottomRight();
                Window.Show();
                // Override an inherited minimized/hidden startup show state after
                // the first Show; checking WPF state before Show is too early.
                Native.ShowWindow(handle, Window.WindowState == WindowState.Maximized ? 3 : 9);
                KeepWindowOnScreen(); Window.Activate(); Native.SetForegroundWindow(handle);
                if (selected == "History") UI<TextBox>("SearchBox").Focus();
            }
        }
        public void ToggleWindow()
        {
            if (Window.IsVisible && Window.WindowState != WindowState.Minimized) { pendingActivation = false; Window.Hide(); }
            else ShowPage("History", false, false);
        }
        void KeepWindowOnScreen()
        {
            if (Window.WindowState != WindowState.Normal || source == null || source.CompositionTarget == null) return;
            double width = Window.ActualWidth > 0 ? Window.ActualWidth : Window.Width;
            double height = Window.ActualHeight > 0 ? Window.ActualHeight : Window.Height;
            var transform = source.CompositionTarget.TransformToDevice;
            var origin = transform.Transform(new Point(Window.Left, Window.Top));
            var edge = transform.Transform(new Point(Window.Left + width, Window.Top + height));
            if (double.IsNaN(origin.X) || double.IsNaN(origin.Y)) return;
            var bounds = new System.Drawing.Rectangle((int)origin.X, (int)origin.Y, (int)(edge.X - origin.X), (int)(edge.Y - origin.Y));
            if (Forms.Screen.AllScreens.Any(x => { var visible = System.Drawing.Rectangle.Intersect(x.WorkingArea, bounds); return visible.Width >= 100 && visible.Height >= 60; })) return;
            PlaceBottomRight();
        }
        void PlaceBottomRight()
        {
            if (source == null || source.CompositionTarget == null || Window.WindowState == WindowState.Maximized) return;
            // Use the desktop working area, not full screen bounds: leave the taskbar free.
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            var inverse = source.CompositionTarget.TransformFromDevice;
            var top = inverse.Transform(new Point(area.Left, area.Top)); var bottom = inverse.Transform(new Point(area.Right, area.Bottom));
            var bounds = WindowAppearance.BottomRight(new Rect(top, bottom), new Size(Window.Width, Window.Height), 16);
            Window.MinWidth = Math.Min(360, bounds.Width); Window.MinHeight = Math.Min(360, bounds.Height);
            Window.Width = bounds.Width; Window.Height = bounds.Height;
            Window.Left = bounds.Left; Window.Top = bounds.Top;
        }
        void PollMouse()
        {
            bool down = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            if (!State.Settings.Shake || dragging || !down || (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0)
            { shake.Feed(0, false, clock.ElapsedMilliseconds); if (dragReveal != null) dragReveal.Cancel(); return; }
            Native.POINT p; if (!Native.GetCursorPos(out p)) return;
            if (shake.Feed(p.X, down, clock.ElapsedMilliseconds) && !Native.OwnWindow(Native.GetForegroundWindow()) && dragReveal != null)
                dragReveal.Begin(new Point(p.X, p.Y));
        }
        void ToggleCompact()
        {
            compact = !compact;
            Window.Width = compact ? 380 : 420; Window.Height = compact ? 440 : 560;
            UI<Button>("CompactButton").Content = compact ? "展开" : "紧凑";
            if (compact) { gridView = false; RefreshShelf(); }
            PlaceBottomRight();
        }
        void DragOver(object sender, DragEventArgs e)
        {
            bool accepted = !dragging && DropContent.HasSupportedFormat(e.Data);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
            UI<Border>("DropOverlay").Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
            UI<TextBlock>("DropHint").Text = e.Data.GetDataPresent(DataFormats.FileDrop) ? "松开鼠标，加入暂存" : e.Data.GetDataPresent(DataFormats.Html, false) ? "松开鼠标，保存网站素材" : "松开鼠标，保存到剪贴板";
        }
        void Drop(object sender, DragEventArgs e)
        {
            UI<Border>("DropOverlay").Visibility = Visibility.Collapsed;
            e.Handled = true; e.Effects = DragDropEffects.None;
            if (dragging) return;
            try
            {
                if (CollectDrop(e.Data, Native.ProcessName(Native.GetForegroundWindow()))) e.Effects = DragDropEffects.Copy;
            }
            catch (Exception ex) { Status("未能保存拖入内容：" + ex.Message); }
        }
        public bool CollectDrop(IDataObject data, string sourceName)
        {
            Clip clip = DropContent.Read(data, sourceName);
            if (clip == null) { Status("这次拖入没有可保存的图片、文件或文字"); return false; }
            Uri website;
            if (clip.Kind == "Text" && PinterestMedia.TrySource(clip.Text, out website)) { BeginWebsiteImport(website.AbsoluteUri, clip.DragMediaHint); return true; }
            if (clip.Kind == "Files") { AddFiles(clip.Files); SelectPage("Shelf"); }
            else { HistoryLogic.Add(State, clip); RefreshHistory(); Changed(); SelectPage("History"); Status("已保存拖入的" + clip.Tag); }
            QueueSave(); return true;
        }
        public void BeginWebsiteImport(string value, string mediaHint = null)
        {
            if (recycling) { Status("请等待回收站操作完成后再导入"); return; }
            Uri uri;
            if (!PinterestMedia.TrySource(value, out uri)) { Status("请拖入或粘贴 Pinterest 单条素材链接"); return; }
            string requestKey = uri.AbsoluteUri + "|" + (mediaHint ?? "");
            if (!websitePending.Add(requestKey)) { Status("这条素材正在保存，请稍候…"); return; }
            SelectPage("Shelf"); Status("正在保存 Pinterest 素材…");
            CancellationToken cancellation = websiteCancellation.Token;
            int generation = websiteGeneration;
            string folder = testing ? Path.Combine(Vault.Folder, "Pinterest") : PinterestMedia.DownloadFolder;
            Task.Run(async delegate
            {
                await websiteSlots.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    return PinterestMedia.Download(uri, folder, cancellation, message =>
                        Window.Dispatcher.BeginInvoke(new Action(() => { if (!closing && generation == websiteGeneration) Status(message); })), mediaHint);
                }
                finally { websiteSlots.Release(); }
            }, cancellation).ContinueWith(task =>
            {
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != websiteGeneration) return;
                    websitePending.Remove(requestKey);
                    if (closing) return;
                    if (task.IsCanceled) { Status("网站素材保存已取消"); return; }
                    if (task.IsFaulted)
                    {
                        HistoryLogic.Add(State, new Clip { Kind = "Text", Text = uri.AbsoluteUri, Source = "Pinterest · 未下载" });
                        RefreshHistory(); Changed(); QueueSave(); Status("未能保存素材，链接已保留：" + task.Exception.GetBaseException().Message); return;
                    }
                    RegisterWebsiteAsset(task.Result);
                }));
            }, TaskScheduler.Default);
        }
        public void RegisterWebsiteAsset(PinterestAsset asset)
        {
            var oldCover = asset.Kind == "视频" && !string.IsNullOrEmpty(asset.ReplacedCoverPath) ? State.History.FirstOrDefault(x => x.Kind == "Files" && x.OriginUrl == asset.OriginUrl && x.Files != null && x.Files.Length == 1 && string.Equals(x.Files[0], asset.ReplacedCoverPath, StringComparison.OrdinalIgnoreCase)) : null;
            if (oldCover != null)
            {
                oldCover.Files = new[] { asset.FilePath }; oldCover.Source = "Pinterest · 视频"; oldCover.ComputeHash(); oldCover.ResetThumbnail();
                shelf.RemoveAll(x => string.Equals(x.Path, asset.ReplacedCoverPath, StringComparison.OrdinalIgnoreCase));
                State.Shelf = shelf.Select(x => x.Path).ToList();
            }
            AddFiles(new[] { asset.FilePath });
            if (oldCover == null) HistoryLogic.Add(State, new Clip { Kind = "Files", Files = new[] { asset.FilePath }, Source = "Pinterest · " + asset.Kind, OriginUrl = asset.OriginUrl });
            RefreshHistory(); Changed(); QueueSave(); SelectPage("Shelf");
            Status("已保存 Pinterest " + asset.Kind + " · 可复制或拖到 Eagle 等应用");
        }
        void AddWebsiteDialog()
        {
            var dialog = new Window { Title = "保存 Pinterest 素材", Owner = Window, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 410, Height = 195, ResizeMode = ResizeMode.NoResize, Background = Brushes.White, FontFamily = Window.FontFamily };
            var panel = new StackPanel { Margin = new Thickness(18) }; dialog.Content = panel;
            panel.Children.Add(new TextBlock { Text = "粘贴 Pinterest 视频、动图或图片链接", Margin = new Thickness(0, 0, 0, 12) });
            var input = new TextBox { Padding = new Thickness(8), MinHeight = 36 }; panel.Children.Add(input);
            var save = new Button { Content = "保存到工具", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8) }; panel.Children.Add(save);
            save.Click += delegate { Uri uri; if (!PinterestMedia.TrySource(input.Text, out uri)) { input.ToolTip = "请输入有效的 Pinterest 素材链接"; return; } dialog.Close(); BeginWebsiteImport(uri.AbsoluteUri); };
            dialog.Loaded += delegate { input.Focus(); }; dialog.ShowDialog();
        }
        public void AddFiles(IEnumerable<string> paths)
        {
            int count = 0;
            foreach (string value in paths)
            {
                try
                {
                    string path = Path.GetFullPath(value);
                    if (!(File.Exists(path) || Directory.Exists(path)) || shelf.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    shelf.Add(new ShelfFile { Path = path }); count++;
                }
                catch { }
            }
            State.Shelf = shelf.Select(x => x.Path).ToList(); RefreshShelf(); Changed(); Status("已加入 " + count + " 项 · 原文件保持在原处");
        }
        void AddDialog()
        {
            var dialog = new OpenFileDialog { Title = "选择要暂存的文件", Multiselect = true };
            if (dialog.ShowDialog(Window) == true) AddFiles(dialog.FileNames);
        }
        public void RefreshShelf()
        {
            var list = UI<ListBox>("FileList");
            list.ItemsSource = null; list.ItemsSource = shelf;
            list.ItemTemplate = (DataTemplate)Window.Resources[gridView ? "FileGrid" : "FileList"];
            var factory = new FrameworkElementFactory(gridView ? typeof(WrapPanel) : typeof(VirtualizingStackPanel));
            list.ItemsPanel = new ItemsPanelTemplate(factory);
            UI<Button>("ViewButton").Content = gridView ? "列表" : "网格";
            UI<TextBlock>("ShelfCount").Text = shelf.Count == 0 ? "文件暂存" : "文件暂存 · " + shelf.Count;
            UI<FrameworkElement>("ShelfEmpty").Visibility = shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void RemoveFiles()
        {
            var selected = UI<ListBox>("FileList").SelectedItems.Cast<ShelfFile>().ToArray();
            foreach (var item in selected) shelf.Remove(item);
            State.Shelf = shelf.Select(x => x.Path).ToList(); RefreshShelf(); Changed(); Status("已从暂存移除，原文件未改动");
        }
        void CopyFiles()
        {
            var paths = UI<ListBox>("FileList").SelectedItems.Cast<ShelfFile>().Select(x => x.Path).ToArray();
            if (paths.Length == 0) { Status("请先选择文件；按住 Ctrl 可多选"); return; }
            CopySelection(new[] { new Clip { Kind = "Files", Files = paths } }, false);
        }
        void RevealFile()
        {
            var item = UI<ListBox>("FileList").SelectedItem as ShelfFile;
            if (item == null) return;
            if (!item.Exists) { Status("原文件已移动或删除"); return; }
            Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
        }
        public void RefreshHistory()
        {
            var list = UI<ListBox>("ClipList");
            var selectedIds = new HashSet<string>(list.SelectedItems.Cast<Clip>().Select(x => x.Id));
            // Re-copying a record can move it to today's group. Refresh its old
            // membership before ObservableCollection.Move, otherwise WPF keeps
            // stale group indexes and can throw while moving the selected item.
            CollectionViewSource.GetDefaultView(visibleHistory).Refresh();
            var filter = UI<ComboBox>("FilterBox").SelectedItem as ComboBoxItem;
            var results = HistoryLogic.Search(State, UI<TextBox>("SearchBox").Text, filter == null ? "全部" : (string)filter.Content).ToArray();
            var wanted = new HashSet<string>(results.Select(x => x.Id));
            for (int i = visibleHistory.Count - 1; i >= 0; i--) if (!wanted.Contains(visibleHistory[i].Id)) visibleHistory.RemoveAt(i);
            for (int i = 0; i < results.Length; i++)
            {
                int current = visibleHistory.IndexOf(results[i]);
                if (current < 0) visibleHistory.Insert(i, results[i]); else if (current != i) visibleHistory.Move(current, i);
            }
            CollectionViewSource.GetDefaultView(visibleHistory).Refresh();
            foreach (var item in results.Where(x => selectedIds.Contains(x.Id))) if (!list.SelectedItems.Contains(item)) list.SelectedItems.Add(item);
            UI<TextBlock>("HistoryCount").Text = results.Length + " 条";
            string category = CurrentCategory;
            var categories = new[] { "全部", "图片", "文字", "文件", "固定" };
            var controls = new[] { "AllFilterButton", "ImageFilterButton", "TextFilterButton", "FileFilterButton", "PinnedFilterButton" };
            for (int i = 0; i < categories.Length; i++)
            {
                UI<Button>(controls[i]).Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category == categories[i] ? "#B8DFE8FF" : "#50FFFFFF"));
                UI<Button>(controls[i]).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(category == categories[i] ? "#3154BD" : "#70819C"));
            }
            UI<FrameworkElement>("ImageActions").Visibility = category == "文字" || category == "链接" ? Visibility.Collapsed : Visibility.Visible;
            UI<FrameworkElement>("HistoryEmpty").Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UI<TextBlock>("EmptyHistoryTitle").Text = State.History.Count == 0 ? "从下一次复制开始" : "没有匹配的记录";
            UI<Button>("PauseButton").Content = State.Settings.Capture ? "暂停" : "恢复";
            UpdateImageCount();
            if (tray != null) tray.Text = State.Settings.Capture ? "DropKit · 正在记录剪贴板" : "DropKit · 剪贴板已暂停";
        }
        string CurrentCategory { get { var item = UI<ComboBox>("FilterBox").SelectedItem as ComboBoxItem; return item == null ? "全部" : (string)item.Content; } }
        void SetHistoryFilter(string category)
        {
            UI<ComboBox>("FilterBox").SelectedItem = UI<ComboBox>("FilterBox").Items.Cast<ComboBoxItem>().First(x => (string)x.Content == category);
        }
        Clip[] ForCurrentCategory(Clip[] clips) { return CurrentCategory == "图片" ? ClipTransfer.OnlyImages(clips) : clips; }
        public Clip[] VisibleDay(string date) { return visibleHistory.Where(x => x.DateKey == date).ToArray(); }
        void HistoryAction(object sender, RoutedEventArgs e)
        {
            var button = e.Source as Button;
            if (button == null) return;
            var clip = button.DataContext as Clip;
            if (button.Uid == "CopyEntry" && clip != null) CopySelection(ForCurrentCategory(new[] { clip }), false);
            else if (button.Uid == "DeleteEntry" && clip != null) RecycleSelection(new[] { clip }, new string[0], false);
            else if (button.Uid == "CopyDay")
            {
                var entries = ForCurrentCategory(VisibleDay(button.Tag as string));
                if (entries.Any(x => x.Kind == "Text") && entries.Any(x => x.Kind != "Text"))
                {
                    var menu = new ContextMenu();
                    Menu(menu, "复制这天的文字", () => CopySelection(entries.Where(x => x.Kind == "Text").ToArray(), false));
                    Menu(menu, "复制这天的图片与文件", () => CopySelection(entries.Where(x => x.Kind != "Text").ToArray(), false));
                    menu.PlacementTarget = button; menu.IsOpen = true;
                }
                else CopySelection(entries, false);
            }
            else if (button.Uid == "DeleteDay")
            {
                string day = button.Tag as string; var entries = VisibleDay(day);
                ConfirmRecycle(entries, new string[0], false, day + " 当前分类的记录");
            }
            else return;
            e.Handled = true;
        }
        Clip SelectedClip() { return UI<ListBox>("ClipList").SelectedItem as Clip; }
        void UpdateImageCount()
        {
            int total = visibleHistory.Sum(x => ClipTransfer.ImageCount(x)), selected = UI<ListBox>("ClipList").SelectedItems.Cast<Clip>().Sum(x => ClipTransfer.ImageCount(x));
            UI<TextBlock>("ImageCount").Text = total + " 张图片" + (selected == 0 ? "" : " · 已选 " + selected);
            UI<Button>("SaveImagesButton").Content = selected > 0 ? "保存选中…" : "保存图片…";
            UI<Button>("CopyAllImagesButton").IsEnabled = total > 0;
            UI<Button>("SaveImagesButton").IsEnabled = total > 0;
        }
        void SelectImages()
        {
            var list = UI<ListBox>("ClipList"); list.SelectedItems.Clear();
            foreach (var clip in visibleHistory.Where(x => x.IsImage)) list.SelectedItems.Add(clip);
            UpdateImageCount();
        }
        void CopyImageBatch(Clip[] images)
        {
            if (images.Length == 0) { Status("还没有图片，从网页右键复制图片即可逐张收集。"); return; }
            CopySelection(images, false);
        }
        void SaveImages()
        {
            Clip[] images = ClipTransfer.OnlyImages(UI<ListBox>("ClipList").SelectedItems.Cast<Clip>());
            if (images.Length == 0) images = ClipTransfer.OnlyImages(visibleHistory);
            if (images.Length == 0) return;
            using (var dialog = new Forms.FolderBrowserDialog { Description = "将 " + images.Sum(x => ClipTransfer.ImageCount(x)) + " 张图片保存到文件夹", ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                string[] saved = ImageExport.Save(images, dialog.SelectedPath);
                Status("已保存 " + saved.Length + " 张图片到：" + dialog.SelectedPath);
            }
        }
        void CopyClip(bool paste)
        {
            CopySelection(ForCurrentCategory(UI<ListBox>("ClipList").SelectedItems.Cast<Clip>().ToArray()), paste);
        }
        void CopySelection(Clip[] selection, bool paste)
        {
            int generation = ++transferGeneration;
            ClipTransfer payload;
            try { payload = ClipTransfer.Prepare(selection, TransferCache); }
            catch (Exception ex) { Status("复制失败：" + ex.Message); return; }
            IntPtr target = previousWindow;
            uint sequence = Native.GetClipboardSequenceNumber(); int retries = 0;
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            Action write = null;
            write = delegate
            {
                retry.Stop();
                if (generation != transferGeneration || closing) return;
                if (retries > 0 && sequence != Native.GetClipboardSequenceNumber()) { Status("剪贴板已有新内容，本次复制已取消；请重新点击复制。"); return; }
                try
                {
                    payload.Write(handle);
                    Status("已复制 " + payload.Count + " 项 · 到目标位置按 Ctrl+V，也可直接拖出选中项");
                    if (paste) PasteTo(target, generation);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    if (++retries < 9) { Status("剪贴板正忙，正在重试…"); retry.Start(); }
                    else Status("剪贴板仍被其他软件占用，请稍后再复制；也可以直接拖出图片。");
                }
                catch (Exception ex) { Status("复制失败：" + ex.Message); }
            };
            retry.Tick += delegate { write(); }; write();
        }
        void PasteTo(IntPtr target, int generation)
        {
            if (target == IntPtr.Zero || !Native.IsWindow(target)) { Status("已复制，请切换到目标窗口按 Ctrl+V"); return; }
            uint sequence = Native.GetClipboardSequenceNumber();
            Window.Hide(); Native.SetForegroundWindow(target);
            int attempts = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += delegate
            {
                if (++attempts > 12 || generation != transferGeneration || sequence != Native.GetClipboardSequenceNumber() || closing) { timer.Stop(); return; }
                if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0) return;
                timer.Stop();
                if (Native.GetForegroundWindow() == target) Native.Paste();
            };
            timer.Start();
        }
        void Pin()
        {
            var items = UI<ListBox>("ClipList").SelectedItems.Cast<Clip>().ToArray(); if (items.Length == 0) return;
            bool pin = items.Any(x => !x.Pinned); foreach (var item in items) { item.Pinned = pin; item.NotifyChanged(); }
            RefreshHistory(); Changed();
        }
        void DeleteClip()
        {
            RecycleSelection(UI<ListBox>("ClipList").SelectedItems.Cast<Clip>().ToArray(), new string[0], false);
        }
        void ConfirmRecycle(Clip[] clips, string[] files, bool all, string label)
        {
            if (clips.Length + files.Length == 0) { Status("没有可删除的项目"); return; }
            string message = "将 " + label + " 共 " + (clips.Length + files.Length) + " 项移到回收站？\n\n图片、视频和文件会移动原文件；文字/链接生成文本副本后移入回收站。可从回收站恢复。";
            if (MessageBox.Show(Window, message, "移到回收站", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) RecycleSelection(clips, files, all);
        }
        public void RecycleSelection(Clip[] clips, string[] files, bool all)
        {
            if (recycling) { Status("正在移到回收站，请稍候…"); return; }
            if (clips.Length + files.Length == 0) { Status("请先选择要删除的项目"); return; }
            recycling = true; Status("正在移到回收站…");
            if (all) { websiteGeneration++; websiteCancellation.Cancel(); websiteCancellation = new CancellationTokenSource(); websitePending.Clear(); }
            foreach (string name in new[] { "DeleteFilesButton", "DeleteAllFilesButton", "DeleteClipButton", "DeleteAllClipsButton", "ClearHistoryButton" }) UI<Button>(name).IsEnabled = false;
            var selectedIds = new HashSet<string>(clips.Select(x => x.Id));
            var selectedPaths = new HashSet<string>(files.Concat(clips.Where(x => x.Kind == "Files").SelectMany(x => x.Files ?? new string[0])), StringComparer.OrdinalIgnoreCase);
            var retained = State.Shelf.Concat(State.History.Where(x => !selectedIds.Contains(x.Id) && x.Kind == "Files").SelectMany(x => x.Files ?? new string[0])).Where(x => !selectedPaths.Contains(x)).ToArray();
            RecycleWork.Run(clips.Select(x => x.Copy()).ToArray(), files, TransferCache, Path.Combine(Vault.Folder, "RecyclePending"), retained, all).ContinueWith(task =>
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    recycling = false;
                    foreach (string name in new[] { "DeleteFilesButton", "DeleteAllFilesButton", "DeleteClipButton", "DeleteAllClipsButton", "ClearHistoryButton" }) UI<Button>(name).IsEnabled = true;
                    if (task.IsFaulted) { Status("回收失败，记录已保留：" + task.Exception.GetBaseException().Message); return; }
                    ApplyRecycleResult(task.Result);
                })), TaskScheduler.Default);
        }
        public void ApplyRecycleResult(RecycleResult result)
        {
            int before = State.History.Count;
            foreach (var clip in State.History.ToArray())
            {
                if (result.CompletedRecords.Contains(clip.Id) || clip.Kind == "Image" && result.RemovedImageHashes.Contains(clip.Hash)) { State.History.Remove(clip); continue; }
                if (clip.Kind == "Files" && clip.Files != null)
                {
                    string[] remaining = clip.Files.Where(x => !result.Removed(x)).ToArray();
                    if (remaining.Length == 0) State.History.Remove(clip);
                    else if (remaining.Length != clip.Files.Length) { clip.Files = remaining; clip.ComputeHash(); clip.ResetThumbnail(); }
                }
            }
            shelf.RemoveAll(x => result.Removed(x.Path)); State.Shelf = shelf.Select(x => x.Path).ToList();
            RefreshShelf(); RefreshHistory(); Changed(); QueueSave();
            UI<Button>("UndoDeleteButton").Visibility = result.Receipts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Status("已移入回收站 " + result.Receipts.Count + " 个文件/文件夹 · 删除 " + (before - State.History.Count) + " 条记录" + (result.Errors.Count == 0 ? "" : " · " + result.Errors.Count + " 项未回收，已保留"));
        }
        public void DeleteRecords(Clip[] items)
        {
            RecycleSelection(items, new string[0], false);
        }
        void Preview()
        {
            var item = SelectedClip(); if (item == null) return;
            var preview = new Window { Title = "预览 · " + item.Tag, Owner = Window, Topmost = Window.Topmost, Width = 760, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, FontFamily = Window.FontFamily };
            if (item.IsVideo)
            {
                string path = item.Files.First(ClipTransfer.IsVideoPath);
                if (!File.Exists(path)) { Status("视频文件已移动或删除"); return; }
                var panel = new DockPanel(); var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
                DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
                var media = new MediaElement { Source = new Uri(path), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, Stretch = Stretch.Uniform, Volume = 0 };
                var play = new Button { Content = "播放", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(3) }; play.Click += delegate { media.Play(); }; buttons.Children.Add(play);
                var pause = new Button { Content = "暂停", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(3) }; pause.Click += delegate { media.Pause(); }; buttons.Children.Add(pause);
                var original = new Button { Content = "用系统播放器打开", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(3) }; original.Click += delegate { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }; buttons.Children.Add(original);
                panel.Children.Add(media); preview.Content = panel;
                preview.Loaded += delegate { media.Play(); };
                media.MediaEnded += delegate { media.Position = TimeSpan.Zero; media.Play(); };
                media.MediaFailed += delegate { Status("内置预览无法播放这个格式，可用系统播放器打开"); };
                preview.Closed += delegate { media.Stop(); media.Close(); };
                preview.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) preview.Close(); };
                preview.ShowDialog(); return;
            }
            if (item.IsImage)
            {
                var images = new List<Func<System.Windows.Media.Imaging.BitmapSource>>();
                foreach (var clip in UI<ListBox>("ClipList").SelectedItems.Cast<Clip>().OrderBy(x => x.Created))
                {
                    if (clip.Kind == "Image") images.Add(() => Clip.Decode(clip.Image, 0));
                    else if (clip.Kind == "Files") foreach (string path in clip.Files.Where(ClipTransfer.IsImagePath)) images.Add(() => Clip.Decode(File.ReadAllBytes(path), 0));
                }
                int index = 0;
                var panel = new DockPanel(); var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
                var back = new Button { Content = "← 上一张", Padding = new Thickness(12, 6, 12, 6) };
                var next = new Button { Content = "下一张 →", Padding = new Thickness(12, 6, 12, 6) };
                var count = new TextBlock { Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                controls.Children.Add(back); controls.Children.Add(count); controls.Children.Add(next); DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
                var content = new ContentControl(); panel.Children.Add(content); preview.Content = panel;
                Action show = delegate
                {
                    count.Text = (index + 1) + " / " + images.Count; back.IsEnabled = index > 0; next.IsEnabled = index + 1 < images.Count;
                    try { content.Content = new Image { Source = images[index](), Stretch = Stretch.Uniform, Margin = new Thickness(20) }; }
                    catch { content.Content = new TextBlock { Text = "这张图片暂时无法预览；请检查原文件是否存在或系统是否支持此格式。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) }; }
                };
                back.Click += delegate { if (index > 0) { index--; show(); } };
                next.Click += delegate { if (index + 1 < images.Count) { index++; show(); } };
                preview.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Left && index > 0) { index--; show(); e.Handled = true; }
                    if (e.Key == Key.Right && index + 1 < images.Count) { index++; show(); e.Handled = true; }
                };
                show();
            }
            else preview.Content = new TextBox { Text = item.Detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Margin = new Thickness(20), FontSize = 14 };
            preview.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) preview.Close(); };
            preview.ShowDialog();
        }
        bool IsStartupEnabled()
        {
            return startupRegistration == null ? testingStartupEnabled : startupRegistration.Enabled;
        }
        void RefreshStartupSetting()
        {
            try
            {
                bool enabled = IsStartupEnabled();
                UI<CheckBox>("StartupCheck").IsChecked = enabled;
                UI<TextBlock>("StartupStatus").Text = enabled ? "已启用 · 登录后后台记录，按 F4 打开" : "未启用 · 点击开关即刻保存";
            }
            catch (Exception ex) { UI<TextBlock>("StartupStatus").Text = "无法读取自启设置：" + ex.Message; }
        }
        void SaveStartupImmediately()
        {
            bool requested = UI<CheckBox>("StartupCheck").IsChecked == true;
            try
            {
                if (startupRegistration == null) testingStartupEnabled = requested;
                else startupRegistration.SetEnabled(requested);
                RefreshStartupSetting();
                Status(requested ? "登录自启已开启并保存" : "登录自启已关闭并保存");
            }
            catch (Exception ex)
            {
                RefreshStartupSetting();
                UI<TextBlock>("StartupStatus").Text = "保存失败：" + ex.Message;
                Status("自启设置保存失败：" + ex.Message);
            }
        }
        void LoadSettings()
        {
            UI<CheckBox>("ShakeCheck").IsChecked = State.Settings.Shake; UI<CheckBox>("TopmostCheck").IsChecked = State.Settings.Topmost;
            UI<CheckBox>("CaptureCheck").IsChecked = State.Settings.Capture; RefreshStartupSetting();
            UI<TextBox>("LimitBox").Text = State.Settings.MaxItems.ToString(); UI<TextBox>("DaysBox").Text = State.Settings.Days.ToString();
            UI<TextBox>("ExcludedBox").Text = State.Settings.Excluded; UI<CheckBox>("WatchCheck").IsChecked = State.Settings.WatchEnabled;
            UI<TextBox>("WatchPathBox").Text = State.Settings.WatchFolder;
        }
        void SaveSwitchesImmediately()
        {
            bool watch = UI<CheckBox>("WatchCheck").IsChecked == true;
            string folder = UI<TextBox>("WatchPathBox").Text;
            if (watch && !Directory.Exists(folder))
            { UI<CheckBox>("WatchCheck").IsChecked = State.Settings.WatchEnabled; Status("请先选择有效的收件箱文件夹"); return; }
            State.Settings.Shake = UI<CheckBox>("ShakeCheck").IsChecked == true;
            State.Settings.Topmost = UI<CheckBox>("TopmostCheck").IsChecked == true;
            State.Settings.Capture = UI<CheckBox>("CaptureCheck").IsChecked == true;
            State.Settings.WatchEnabled = watch; State.Settings.WatchFolder = folder;
            clipTimer.Stop(); lastSequence = Native.GetClipboardSequenceNumber();
            Window.Topmost = State.Settings.Topmost;
            if (!testing) SetupWatcher();
            RefreshHistory(); Changed(); SaveNow(); Status("开关设置已保存");
        }
        void SaveSettings()
        {
            int limit, days;
            if (!int.TryParse(UI<TextBox>("LimitBox").Text, out limit) || limit < 0 || limit > 10000 || !int.TryParse(UI<TextBox>("DaysBox").Text, out days) || days < 0 || days > 36500)
            { Status("请输入有效数字：条数 0–10000，天数 0–36500"); return; }
            string folder = UI<TextBox>("WatchPathBox").Text;
            bool watch = UI<CheckBox>("WatchCheck").IsChecked == true;
            if (watch && !Directory.Exists(folder)) { Status("请先选择有效的监控文件夹"); return; }
            State.Settings.Shake = UI<CheckBox>("ShakeCheck").IsChecked == true; State.Settings.Topmost = UI<CheckBox>("TopmostCheck").IsChecked == true;
            State.Settings.Capture = UI<CheckBox>("CaptureCheck").IsChecked == true; clipTimer.Stop(); lastSequence = Native.GetClipboardSequenceNumber();
            State.Settings.MaxItems = limit; State.Settings.Days = days; State.Settings.Excluded = UI<TextBox>("ExcludedBox").Text;
            State.Settings.WatchEnabled = watch; State.Settings.WatchFolder = folder;
            Window.Topmost = State.Settings.Topmost; HistoryLogic.Prune(State, DateTime.UtcNow); RefreshHistory();
            if (!testing) SetupWatcher(); Changed(); SaveNow(); Status("设置已保存");
        }
        void ChooseFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = "选择收集新文件的文件夹（不包含子文件夹）", ShowNewFolderButton = false })
                if (dialog.ShowDialog() == Forms.DialogResult.OK) { UI<TextBox>("WatchPathBox").Text = dialog.SelectedPath; SaveSwitchesImmediately(); }
        }
        public void SetupWatcher()
        {
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            lock (pendingLock) pending.Clear();
            if (!State.Settings.WatchEnabled) return;
            if (!Directory.Exists(State.Settings.WatchFolder)) { Status("监控文件夹不可用，请在设置中重新选择"); return; }
            try
            {
                watcher = new FileSystemWatcher(State.Settings.WatchFolder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
                watcher.Created += delegate(object sender, FileSystemEventArgs e) { QueueFile(e.FullPath); };
                watcher.Renamed += delegate(object sender, RenamedEventArgs e) { QueueFile(e.FullPath); };
                watcher.Error += delegate { Window.Dispatcher.BeginInvoke(new Action(() => Status("文件夹监控暂时中断，请重新保存设置以恢复。"))); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Status("文件夹监控未启动：" + ex.Message); }
        }
        void QueueFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (new[] { ".tmp", ".part", ".crdownload", ".download" }.Contains(ext) || Path.GetFileName(path).StartsWith("~$")) return;
            lock (pendingLock) pending[path] = new PendingFile { Path = path, Started = DateTime.UtcNow };
        }
        class PendingFile { public string Path; public DateTime Started; public long Size = -1; public int Stable; }
        public void ProcessPendingFiles()
        {
            var ready = new List<string>();
            lock (pendingLock)
            {
                foreach (var item in pending.Values.ToArray())
                {
                    if (DateTime.UtcNow - item.Started > TimeSpan.FromMinutes(30)) { pending.Remove(item.Path); continue; }
                    try
                    {
                        if (!File.Exists(item.Path)) continue;
                        long length = new FileInfo(item.Path).Length;
                        if (item.Size == length) item.Stable++; else { item.Size = length; item.Stable = 0; }
                        if (item.Stable < 1) continue;
                        using (File.Open(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        ready.Add(item.Path); pending.Remove(item.Path);
                    }
                    catch { }
                }
            }
            if (ready.Count > 0) { AddFiles(ready); if (!testing) ShowPage("Shelf", false, false); }
            int before = State.History.Count; HistoryLogic.Prune(State, DateTime.UtcNow);
            if (before != State.History.Count) { RefreshHistory(); Changed(); }
        }
        public void Changed() { dirty = true; saveTimer.Stop(); saveTimer.Start(); }
        void QueueSave()
        {
            if (!dirty) return;
            State snapshot = State.Snapshot(); dirty = false;
            // Serialize immutable snapshots off the UI thread; chain writes in capture order.
            pendingSave = pendingSave.ContinueWith(delegate { Vault.SaveBytes(Vault.Serialize(snapshot)); }, TaskScheduler.Default);
            pendingSave.ContinueWith(delegate(Task failed)
            {
                Exception error = failed.Exception.GetBaseException();
                Window.Dispatcher.BeginInvoke(new Action(() => { dirty = true; Status("保存失败：" + error.Message); }));
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public void SaveNow()
        {
            saveTimer.Stop();
            try { pendingSave.Wait(); } catch { dirty = true; }
            if (!dirty) return;
            try { Vault.SaveBytes(Vault.Serialize(State)); dirty = false; }
            catch (Exception ex) { Status("保存失败，请检查数据文件夹权限或剩余空间：" + ex.Message); }
        }
        public void Quit() { if (recycling) { Status("文件正在移入回收站，请等待完成后再退出"); return; } SaveNow(); closing = true; Dispose(); Window.Close(); if (Application.Current != null) Application.Current.Shutdown(); }
        public void Dispose()
        {
            mouseTimer.Stop(); clipTimer.Stop(); housekeeping.Stop(); saveTimer.Stop();
            websiteCancellation.Cancel();
            if (dragReveal != null) { dragReveal.Dispose(); dragReveal = null; }
            if (watcher != null) { watcher.Dispose(); watcher = null; }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (handle != IntPtr.Zero) { Native.UnregisterHotKey(handle, 11); Native.UnregisterHotKey(handle, 12); Native.RemoveClipboardFormatListener(handle); handle = IntPtr.Zero; }
        }
    }
}
