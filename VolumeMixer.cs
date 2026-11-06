using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("Volume Mixer")]
[assembly: AssemblyDescription("Per-app audio mixer for Windows")]
[assembly: AssemblyCompany("Yogiero")]
[assembly: AssemblyProduct("Volume Mixer")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace HizliSesKaristirici
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                bool created;
                using (new Mutex(true, "VolumeMixer.Yogiero.Singleton", out created))
                {
                    if (!created) return;
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log("Dispatcher exception: " + e.Exception);
                        MessageBox.Show("Volume Mixer encountered an error. Details were written to its log file.\n\n" + e.Exception.Message, "Volume Mixer", MessageBoxButton.OK, MessageBoxImage.Error);
                        e.Handled = true;
                    };
                    var window = new MixerWindow();
                    app.Run();
                }
            }
            catch (Exception ex) { Log("Startup exception: " + ex); MessageBox.Show("Volume Mixer couldn't start:\n\n" + ex.Message, "Volume Mixer", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        internal static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VolumeMixer.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + text + Environment.NewLine); } catch { }
        }
    }

    internal sealed class MixerWindow : Window
    {
        private readonly Forms.NotifyIcon tray;
        private readonly StackPanel rows;
        private readonly TextBlock status;
        private readonly AppRow masterRow;
        private readonly Audio audio = new Audio();
        private readonly DispatcherTimer timer;
        private readonly Dictionary<string, AppRow> appRows = new Dictionary<string, AppRow>();
        private bool updating;
        private bool popupOpen;
        private bool exiting;
        private bool contextMenuOpen;
        private bool autoSized;
        private bool positioningPopup;
        private readonly Guid eventContext = Guid.NewGuid();

        public MixerWindow()
        {
            Title = "Volume Mixer";
            Width = 420; Height = 150; MinWidth = 380; MinHeight = 150;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
            Background = Brush("#17191C"); Foreground = Brush("#F2F3F5");
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;

            var root = new DockPanel { LastChildFill = true, Margin = new Thickness(14, 12, 14, 12) };
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var titleLine = new Grid();
            titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock { Text = "Volume Mixer", FontSize = 20, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Text = "by Yogiero", FontSize = 10, Foreground = Brush("#F28C28"), Margin = new Thickness(7, 7, 0, 0) });
            Grid.SetColumn(heading, 0); titleLine.Children.Add(heading);
            var closeButton = new Button { Content = "×", FontSize = 22, Width = 30, Height = 28, Padding = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Foreground = Brush("#B9C0C8"), BorderThickness = new Thickness(0), ToolTip = "Close" };
            closeButton.Style = MakeIconButtonStyle();
            closeButton.Click += delegate { popupOpen = false; Hide(); };
            Grid.SetColumn(closeButton, 1); titleLine.Children.Add(closeButton);
            header.Children.Add(titleLine);
            status = new TextBlock { Text = "", Foreground = Brush("#E6B566"), Margin = new Thickness(0, 4, 0, 0), FontSize = 11, Visibility = Visibility.Collapsed };
            header.Children.Add(status);
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

            masterRow = new AppRow("master", "Master", 1f, false, null);
            masterRow.Card.Margin = new Thickness(0, 0, 0, 6);
            masterRow.Slider.ValueChanged += delegate
            {
                masterRow.Value.Text = Math.Round(masterRow.Slider.Value).ToString(CultureInfo.InvariantCulture);
                if (!updating) audio.SetMaster((float)(masterRow.Slider.Value / 100.0), eventContext);
            };
            masterRow.MuteButton.Click += delegate
            {
                masterRow.Muted = !masterRow.Muted;
                audio.SetMasterMute(masterRow.Muted, eventContext);
                masterRow.RefreshMuteGlyph();
            };
            DockPanel.SetDock(masterRow.Card, Dock.Top); root.Children.Add(masterRow.Card);
            var divider = new Border { Height = 1, Background = Brush("#3A3E43"), Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(divider, Dock.Top); root.Children.Add(divider);

            rows = new StackPanel();
            var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(scroll);
            Content = root;

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Volume Mixer", null, delegate { TogglePopup(); });
            menu.Items.Add("Exit", null, delegate { exiting = true; timer.Stop(); tray.Visible = false; Application.Current.Shutdown(); });
            tray = new Forms.NotifyIcon { Text = "Volume Mixer", ContextMenuStrip = menu, Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath) ?? System.Drawing.SystemIcons.Application; } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            tray.Click += delegate { TogglePopup(); };

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += delegate { RefreshMixer(); };
            Loaded += delegate { timer.Start(); };
            StateChanged += delegate { if (WindowState == WindowState.Minimized) Hide(); };
            Deactivated += delegate { if (popupOpen && !contextMenuOpen) { popupOpen = false; Hide(); } };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; popupOpen = false; Hide(); } };
            SizeChanged += delegate { if (popupOpen && !positioningPopup) PositionPopup(); };
        }

        private static SolidColorBrush Brush(string color) { return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)); }
        internal static Style MakeIconButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(UIElement.RenderTransformProperty, new ScaleTransform(1, 1)));
            style.Setters.Add(new Setter(FrameworkElement.RenderTransformOriginProperty, new System.Windows.Point(0.5, 0.5)));
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter); template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.EnterActions.Add(new BeginStoryboard { Storyboard = ScaleStoryboard(1.14) });
            hover.ExitActions.Add(new BeginStoryboard { Storyboard = ScaleStoryboard(1.0) });
            style.Triggers.Add(hover);
            return style;
        }
        private static Storyboard ScaleStoryboard(double scale)
        {
            var story = new Storyboard();
            var x = new DoubleAnimation { To = scale, Duration = TimeSpan.FromMilliseconds(130), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            var y = new DoubleAnimation { To = scale, Duration = TimeSpan.FromMilliseconds(130), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTargetProperty(x, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTargetProperty(y, new PropertyPath("RenderTransform.ScaleY"));
            story.Children.Add(x); story.Children.Add(y); return story;
        }
        private static Slider MakeSlider()
        {
            return new Slider { Minimum = 0, Maximum = 100, Height = 28, Margin = new Thickness(0, 7, 0, 0), IsMoveToPointEnabled = true };
        }

        private void TogglePopup()
        {
            if (popupOpen && IsVisible) { popupOpen = false; Hide(); return; }
            PositionPopup();
            WindowState = WindowState.Normal; popupOpen = true; Show(); Activate();
            Dispatcher.BeginInvoke(new Action(RefreshMixer), DispatcherPriority.Background);
        }

        private void PositionPopup()
        {
            var area = SystemParameters.WorkArea;
            positioningPopup = true;
            Left = Math.Max(area.Left, area.Right - Width - 8);
            Top = Math.Max(area.Top, area.Bottom - Height - 36);
            positioningPopup = false;
        }

        private void FitHeightToRows(int appCount)
        {
            var area = SystemParameters.WorkArea;
            double wanted = 120 + (appCount * 50) + (status.Visibility == Visibility.Visible ? 20 : 0);
            Height = Math.Max(MinHeight, Math.Min(wanted, area.Height * 0.84));
            if (popupOpen) PositionPopup();
        }

        private void RefreshMixer()
        {
            try
            {
                int oldRowCount = appRows.Count;
                updating = true;
                float master = audio.GetMaster();
                masterRow.Slider.Value = Math.Round(master * 100.0);
                masterRow.Value.Text = Math.Round(master * 100.0).ToString(CultureInfo.InvariantCulture);
                masterRow.Muted = audio.GetMasterMute(); masterRow.RefreshMuteGlyph();
                List<Session> sessions = audio.GetSessions();
                var seen = new HashSet<string>();
                foreach (Session session in sessions)
                {
                    if (String.IsNullOrEmpty(session.Key)) continue;
                    seen.Add(session.Key);
                    AppRow row;
                    if (!appRows.TryGetValue(session.Key, out row))
                    {
                        row = new AppRow(session.Key, session.Name, session.Volume, session.Muted, session.Icon);
                        row.Keys = session.Keys;
                        row.ExecutablePath = session.ExecutablePath;
                        row.ProcessId = session.ProcessId;
                        row.Slider.ValueChanged += delegate
                        {
                            row.Value.Text = Math.Round(row.Slider.Value).ToString(CultureInfo.InvariantCulture);
                            if (!updating) audio.SetSessionVolume(row.Keys, (float)(row.Slider.Value / 100.0), eventContext);
                        };
                        row.MuteButton.Click += delegate
                        {
                            row.Muted = !row.Muted; audio.SetSessionMute(row.Keys, row.Muted, eventContext); row.RefreshMuteGlyph();
                        };
                        row.Name.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { audio.ActivateApplication(row.ExecutablePath, row.ProcessId); e.Handled = true; };
                        row.Icon.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { audio.ActivateApplication(row.ExecutablePath, row.ProcessId); e.Handled = true; };
                        var contextMenu = new ContextMenu();
                        contextMenu.Opened += delegate { contextMenuOpen = true; };
                        contextMenu.Closed += delegate { contextMenuOpen = false; };
                        var openLocation = new MenuItem { Header = "Open File Location", IsEnabled = !String.IsNullOrEmpty(row.ExecutablePath) };
                        openLocation.Click += delegate { audio.OpenFileLocation(row.ExecutablePath); };
                        var closeApp = new MenuItem { Header = "Close the App" };
                        closeApp.Click += delegate { audio.CloseApplication(row.ExecutablePath, row.ProcessId); };
                        var removeFromTray = new MenuItem { Header = "Remove from Tray permanently" };
                        removeFromTray.Click += delegate { audio.ExcludeApplication(row.ExecutablePath, row.Name.Text); RefreshMixer(); };
                        contextMenu.Items.Add(openLocation);
                        contextMenu.Items.Add(closeApp);
                        contextMenu.Items.Add(new Separator());
                        contextMenu.Items.Add(removeFromTray);
                        row.Name.ContextMenu = contextMenu;
                        appRows.Add(session.Key, row); rows.Children.Add(row.Card);
                    }
                    row.Name.Text = session.Name;
                    row.Keys = session.Keys;
                    row.ExecutablePath = session.ExecutablePath;
                    row.ProcessId = session.ProcessId;
                    row.Muted = session.Muted; row.RefreshMuteGlyph();
                    row.Slider.Value = Math.Round(session.Volume * 100.0);
                    row.Value.Text = Math.Round(session.Volume * 100.0).ToString(CultureInfo.InvariantCulture);
                }
                var removed = new List<string>();
                foreach (var item in appRows) if (!seen.Contains(item.Key)) { rows.Children.Remove(item.Value.Card); removed.Add(item.Key); }
                foreach (string key in removed) appRows.Remove(key);
                status.Text = audio.SessionError ?? "";
                status.Visibility = String.IsNullOrEmpty(status.Text) ? Visibility.Collapsed : Visibility.Visible;
                if (!autoSized || oldRowCount != appRows.Count)
                {
                    autoSized = true;
                    FitHeightToRows(appRows.Count);
                }
            }
            catch (Exception ex)
            {
                Program.Log("Refresh exception: " + ex);
                status.Text = "Audio controls aren't available. Retrying…";
            }
            finally { updating = false; }
        }

        protected override void OnClosed(EventArgs e)
        {
            tray.Visible = false; tray.Dispose(); timer.Stop(); base.OnClosed(e);
        }
    }

    internal sealed class AppRow
    {
        public readonly Border Card;
        public readonly TextBlock Name;
        public readonly TextBlock Value;
        public readonly FrameworkElement Icon;
        public readonly Button MuteButton;
        public readonly Slider Slider;
        public readonly string Key;
        public List<string> Keys;
        public bool Muted;
        public string ExecutablePath;
        public uint ProcessId;
        private readonly TextBlock muteGlyph;
        public AppRow(string key, string name, float volume, bool muted, ImageSource icon)
        {
            Key = key; Keys = new List<string>(); Muted = muted;
            Card = new Border { Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#24272B")), CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6) };
            var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) });
            Icon = icon == null
                ? (FrameworkElement)new TextBlock { Text = key == "master" ? "\uE994" : "\uE71D", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 18, Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D7DBE0")), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand }
                : new System.Windows.Controls.Image { Source = icon, Width = 22, Height = 22, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            Grid.SetColumn(Icon, 0); grid.Children.Add(Icon);
            Name = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 5, 0), Cursor = Cursors.Hand };
            Grid.SetColumn(Name, 1); grid.Children.Add(Name);
            muteGlyph = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            MuteButton = new Button { Width = 28, Height = 28, Padding = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D7DBE0")), ToolTip = "Mute", Content = muteGlyph };
            MuteButton.Style = MixerWindow.MakeIconButtonStyle(); MuteButton.Focusable = false;
            Grid.SetColumn(MuteButton, 2); grid.Children.Add(MuteButton);
            Value = new TextBlock { Text = Math.Round(volume * 100.0).ToString(CultureInfo.InvariantCulture), FontSize = 12, Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B9C0C8")), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(Value, 3); grid.Children.Add(Value);
            Slider = new Slider { Minimum = 0, Maximum = 100, Height = 28, Margin = new Thickness(2, 0, 0, 0), IsMoveToPointEnabled = true, Value = Math.Round(volume * 100.0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(Slider, 4); grid.Children.Add(Slider);
            Card.Child = grid; RefreshMuteGlyph();
        }

        public void RefreshMuteGlyph()
        {
            muteGlyph.Text = Muted ? "\uE74F" : "\uE994";
            MuteButton.ToolTip = Muted ? "Unmute" : "Mute";
        }
    }

    internal sealed class Session
    {
        public string Key, Name, ExecutablePath; public uint ProcessId; public float Volume; public bool Muted; public ImageSource Icon; public List<string> Keys = new List<string>(); public int Count;
    }

    internal sealed class Audio
    {
        private const uint CLSCTX_INPROC_SERVER = 1;
        private const uint CLSCTX_ALL = 23;
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid EndpointVolumeId = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly Guid SessionManagerId = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private readonly IMMDeviceEnumerator enumerator;
        private readonly IAudioEndpointVolume endpoint;
        private IAudioSessionManager2 manager;
        private IMMDevice device;
        private int nextActivationAttempt;
        private bool activationErrorLogged;
        private readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> excludedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string excludedAppsFile;
        public string SessionError { get; private set; }

        public Audio()
        {
            excludedAppsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "excluded-apps.txt");
            try
            {
                if (File.Exists(excludedAppsFile))
                    foreach (string entry in File.ReadAllLines(excludedAppsFile)) if (!String.IsNullOrWhiteSpace(entry)) excludedApps.Add(entry.Trim());
            }
            catch (Exception ex) { Program.Log("Could not load exclusions: " + ex.Message); }
            Program.Log("Audio: creating device enumerator");
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Program.Log("Audio: getting default endpoint");
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
            Program.Log("Audio: activating endpoint volume");
            object obj; Guid endpointId = EndpointVolumeId; Marshal.ThrowExceptionForHR(device.Activate(ref endpointId, CLSCTX_INPROC_SERVER, IntPtr.Zero, out obj)); endpoint = (IAudioEndpointVolume)obj;
            SessionError = "Waiting for app audio sessions…";
            Program.Log("Audio: ready");
        }

        private void TryActivateSessionManager()
        {
            ERole[] roles = new ERole[] { ERole.Multimedia, ERole.Console, ERole.Communications };
            Exception lastError = null;
            foreach (ERole role in roles)
            {
                try
                {
                    IMMDevice candidate; int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out candidate);
                    if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }
                    object obj; Guid managerId = SessionManagerId;
                    hr = candidate.Activate(ref managerId, CLSCTX_ALL, IntPtr.Zero, out obj);
                    if (hr < 0) { Marshal.ThrowExceptionForHR(hr); }
                    manager = (IAudioSessionManager2)obj; device = candidate; SessionError = null;
                    Program.Log("Audio: session manager ready"); return;
                }
                catch (Exception ex) { lastError = ex; }
            }
            SessionError = "Couldn't access app audio sessions. Retrying…";
            if (lastError != null && !activationErrorLogged) { Program.Log("Session manager unavailable: " + lastError); activationErrorLogged = true; }
        }
        public float GetMaster() { float value; Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out value)); return value; }
        public void SetMaster(float value, Guid context) { endpoint.SetMasterVolumeLevelScalar(value, ref context); }
        public bool GetMasterMute() { bool mute; Marshal.ThrowExceptionForHR(endpoint.GetMute(out mute)); return mute; }
        public void SetMasterMute(bool mute, Guid context) { endpoint.SetMute(mute, ref context); }
        public List<Session> GetSessions()
        {
            var groups = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase); IAudioSessionEnumerator list;
            if (manager == null && unchecked(Environment.TickCount - nextActivationAttempt) >= 0)
            {
                nextActivationAttempt = unchecked(Environment.TickCount + 3000);
                TryActivateSessionManager();
            }
            if (manager == null) return new List<Session>();
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out list)); int count; Marshal.ThrowExceptionForHR(list.GetCount(out count));
            for (int i = 0; i < count; i++)
            {
                IntPtr controlPtr; if (list.GetSession(i, out controlPtr) < 0 || controlPtr == IntPtr.Zero) continue;
                IAudioSessionControl control;
                try { control = (IAudioSessionControl)Marshal.GetObjectForIUnknown(controlPtr); }
                finally { Marshal.Release(controlPtr); }
                IAudioSessionControl2 control2 = control as IAudioSessionControl2; ISimpleAudioVolume volume = control as ISimpleAudioVolume;
                if (control2 == null || volume == null) continue;
                int state; if (control2.GetState(out state) < 0 || state == 2) continue;
                uint pid; if (control2.GetProcessId(out pid) < 0 || pid == 0) continue;
                string id; if (control2.GetSessionInstanceIdentifier(out id) < 0 || String.IsNullOrEmpty(id)) id = pid.ToString();
                string name, path; ProcessInfo(pid, out name, out path);
                string appKey = AppKey(path, name);
                if (excludedApps.Contains(appKey)) continue;
                float scalar; if (volume.GetMasterVolume(out scalar) < 0) scalar = 1;
                bool muted; if (volume.GetMute(out muted) < 0) muted = false;
                string groupKey = appKey;
                Session grouped;
                if (!groups.TryGetValue(groupKey, out grouped))
                {
                    grouped = new Session { Key = groupKey, ProcessId = pid, Name = name, ExecutablePath = path, Volume = scalar, Muted = muted, Icon = GetIcon(path), Count = 1 };
                    groups.Add(groupKey, grouped);
                }
                else
                {
                    grouped.Volume = ((grouped.Volume * grouped.Count) + scalar) / (grouped.Count + 1);
                    grouped.Muted = grouped.Muted && muted;
                    grouped.Count++;
                }
                if (!grouped.Keys.Contains(id)) grouped.Keys.Add(id);
            }
            return new List<Session>(groups.Values);
        }
        private static void ProcessInfo(uint pid, out string name, out string path)
        {
            name = "Application " + pid; path = null;
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                {
                    name = process.ProcessName;
                    if (!String.IsNullOrEmpty(name) && Char.IsLower(name[0])) name = Char.ToUpperInvariant(name[0]) + name.Substring(1);
                    try { path = process.MainModule.FileName; } catch { }
                }
            }
            catch { }
        }
        private static string AppKey(string path, string name)
        {
            try { if (!String.IsNullOrEmpty(path)) return Path.GetFullPath(path); } catch { }
            return "name:" + (name ?? "unknown");
        }
        public void ExcludeApplication(string path, string name)
        {
            string key = AppKey(path, name);
            if (!excludedApps.Add(key)) return;
            try { File.AppendAllText(excludedAppsFile, key + Environment.NewLine); }
            catch (Exception ex) { Program.Log("Could not save app exclusion: " + ex.Message); }
        }
        public void OpenFileLocation(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { Program.Log("Could not open app location: " + ex.Message); }
        }
        public void CloseApplication(string path, uint processId)
        {
            bool requestedWindowClose = false;
            try
            {
                if (!String.IsNullOrEmpty(path))
                {
                    string processName = Path.GetFileNameWithoutExtension(path);
                    foreach (Process process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            try
                            {
                                process.Refresh();
                                if (process.MainWindowHandle != IntPtr.Zero && String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                                { process.CloseMainWindow(); requestedWindowClose = true; }
                            }
                            catch { }
                        }
                    }
                }
                if (!requestedWindowClose && processId != 0)
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        if (!process.CloseMainWindow()) process.Kill();
                    }
                }
            }
            catch (Exception ex) { Program.Log("Could not close app: " + ex.Message); }
        }
        private ImageSource GetIcon(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            ImageSource cached; if (iconCache.TryGetValue(path, out cached)) return cached;
            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) { iconCache[path] = null; return null; }
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
                    source.Freeze(); iconCache[path] = source; return source;
                }
            }
            catch { iconCache[path] = null; return null; }
        }
        private void ForEachSession(List<string> keys, Action<ISimpleAudioVolume> apply)
        {
            if (manager == null || keys == null || keys.Count == 0) return;
            IAudioSessionEnumerator list; if (manager.GetSessionEnumerator(out list) < 0) return;
            int count; if (list.GetCount(out count) < 0) return;
            var target = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                IntPtr controlPtr; if (list.GetSession(i, out controlPtr) < 0 || controlPtr == IntPtr.Zero) continue;
                IAudioSessionControl control;
                try { control = (IAudioSessionControl)Marshal.GetObjectForIUnknown(controlPtr); }
                finally { Marshal.Release(controlPtr); }
                IAudioSessionControl2 c2 = control as IAudioSessionControl2; if (c2 == null) continue;
                string id; if (c2.GetSessionInstanceIdentifier(out id) < 0 || !target.Contains(id)) continue;
                ISimpleAudioVolume simple = control as ISimpleAudioVolume; if (simple != null) apply(simple);
            }
        }
        public void SetSessionVolume(List<string> keys, float value, Guid context) { ForEachSession(keys, delegate(ISimpleAudioVolume volume) { volume.SetMasterVolume(value, ref context); }); }
        public void SetSessionMute(List<string> keys, bool mute, Guid context) { ForEachSession(keys, delegate(ISimpleAudioVolume volume) { volume.SetMute(mute, ref context); }); }
        public void ActivateApplication(string path, uint processId)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                if (!String.IsNullOrEmpty(path))
                {
                    string processName = Path.GetFileNameWithoutExtension(path);
                    foreach (Process process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            try
                            {
                                process.Refresh();
                                if (process.MainWindowHandle != IntPtr.Zero && String.Equals(process.MainModule.FileName, path, StringComparison.OrdinalIgnoreCase))
                                { handle = process.MainWindowHandle; break; }
                            }
                            catch { }
                        }
                    }
                }
                if (handle == IntPtr.Zero && processId != 0)
                {
                    using (Process process = Process.GetProcessById((int)processId)) { process.Refresh(); handle = process.MainWindowHandle; }
                }
            }
            catch { }
            if (handle != IntPtr.Zero) { ShowWindowAsync(handle, 9); SetForegroundWindow(handle); }
        }

        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    }

    internal enum EDataFlow { Render, Capture, All }
    internal enum ERole { Console, Multimedia, Communications }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsctx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid guid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid guid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IntPtr control);
    }
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
