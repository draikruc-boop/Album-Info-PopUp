using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Serialization;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();

        private RatingPopupForm popupForm;
        private string selectedFileUrl = string.Empty;
        private string lastRenderedFileUrl = string.Empty;
        private bool contextMenuRegistered;
        private volatile bool tagSettingsDialogOpen;
        private HashSet<MetaDataType> displayedTagFields = new HashSet<MetaDataType>();
        private Dictionary<MetaDataType, Font> displayedTagFonts = new Dictionary<MetaDataType, Font>();

        private static string DiagnosticLogPath =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "AlbumInfoPopup.log");
        private static string TagSelectionFilePath =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "AlbumInfoPopup.tags");
        private static string TagFontSettingsFilePath =>
            Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "AlbumInfoPopup.fonts.xml");
        private static readonly MetaDataType[] DefaultTagFields =
        {
            MetaDataType.TrackTitle, MetaDataType.Album, MetaDataType.AlbumArtist,
            MetaDataType.Artist, MetaDataType.TrackNo, MetaDataType.DiscNo,
            MetaDataType.Year, MetaDataType.Genre, MetaDataType.Composer,
            MetaDataType.Publisher, MetaDataType.Rating, MetaDataType.BeatsPerMin,
            MetaDataType.Comment, MetaDataType.Quality, MetaDataType.Encoder
        };

        private static Plugin instance;
        private static IntPtr mouseHookHandle = IntPtr.Zero;
        private static System.Threading.Thread mouseHookThread;
        private static uint mouseHookThreadId;
        private static readonly System.Threading.ManualResetEvent mouseHookReady = new System.Threading.ManualResetEvent(false);
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static LowLevelMouseProc mouseProc;

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            instance = this;

            // Fixes DPI-scaling mismatches (mis-sized/cropped popup, wrong star clicked)
            try { SetProcessDPIAware(); } catch { }

            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            displayedTagFields = LoadDisplayedTagFields();
            displayedTagFonts = LoadDisplayedTagFonts();
            Trace("Album info popup: build marker TextOnlyNoPersistence-20261001; assembly=" + typeof(Plugin).Assembly.Location);
            Trace("Album info popup: initialized; diagnostics file is " + DiagnosticLogPath);
            RegisterContextMenuItem();
            try { mbApiInterface.MB_RegisterCommand("PopupAlbumInfo", ShowSelectedAlbumInfo); }
            catch (Exception ex) { Trace("Album info popup: command registration failed: " + ex.Message); }
            InstallMouseHook();

            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Album info popup";
            about.Description = "Shows selected track tags with saved per-tag font settings.";
            about.Author = "You";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 1;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.StartupOnly | ReceiveNotificationFlags.PlayerEvents;
            about.ConfigurationPanelHeight = 0;

            return about;
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }

        public void Close(PluginCloseReason reason)
        {
            RemoveMouseHook();
            if (popupForm != null && !popupForm.IsDisposed)
            {
                try { popupForm.Invoke((Action)(() => popupForm.Close())); } catch { }
            }
            foreach (Font font in displayedTagFonts.Values)
                font.Dispose();
            displayedTagFonts.Clear();
        }

        public void Uninstall() => RemoveMouseHook();

        private void ShowSelectedAlbumInfo(object sender, EventArgs e)
        {
            try
            {
                if (!mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out string[] files) || files == null || files.Length == 0)
                    return;

                selectedFileUrl = files[0];
                ShowSelectedFileTags(selectedFileUrl, true);
            }
            catch (Exception ex)
            {
                Trace("Album info popup: context command failed: " + ex.Message);
            }
        }

        private void ShowSelectedFileTags(string sourceFileUrl, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(sourceFileUrl))
                return;
            if (!forceRefresh && string.Equals(sourceFileUrl, lastRenderedFileUrl, StringComparison.OrdinalIgnoreCase))
                return;

            selectedFileUrl = sourceFileUrl;
            lastRenderedFileUrl = sourceFileUrl;
            try
            {
                var fields = new List<MetaDataType>(displayedTagFields);
                fields.Sort((left, right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.ToString(), right.ToString()));

                if (fields.Count == 0)
                {
                    ShowPopup("No tag fields selected. Use the settings button to choose fields.");
                    return;
                }

                if (!mbApiInterface.Library_GetFileTags(sourceFileUrl, fields.ToArray(), out string[] values) || values == null)
                {
                    Trace("Album info popup: selected file tags could not be read.");
                    return;
                }

                var entries = new List<KeyValuePair<MetaDataType, string>>();
                for (int index = 0; index < fields.Count && index < values.Length; index++)
                {
                    if (!string.IsNullOrWhiteSpace(values[index]))
                        entries.Add(new KeyValuePair<MetaDataType, string>(fields[index], values[index].Replace('\0', ',')));
                }
                var fontFields = new List<string>();
                foreach (var entry in entries)
                    if (displayedTagFonts.ContainsKey(entry.Key))
                        fontFields.Add(entry.Key.ToString());
                Trace("Album info popup: rendering " + entries.Count + " tags; custom fonts applied to " + string.Join(", ", fontFields));
                ShowPopup(entries.Count == 0 ? "No tags found" : "", entries);
            }
            catch (Exception ex)
            {
                Trace("Album info popup: selected tags failed: " + ex.Message);
            }
        }

        private HashSet<MetaDataType> LoadDisplayedTagFields()
        {
            var fields = new HashSet<MetaDataType>();
            try
            {
                if (File.Exists(TagSelectionFilePath))
                {
                    foreach (string name in File.ReadAllLines(TagSelectionFilePath))
                        if (Enum.TryParse(name, out MetaDataType field) && Enum.IsDefined(typeof(MetaDataType), field))
                            fields.Add(field);
                    return fields;
                }
            }
            catch (Exception ex)
            {
                Trace("Album info popup: tag selections could not be loaded: " + ex.Message);
            }
            return new HashSet<MetaDataType>(DefaultTagFields);
        }

        private Dictionary<MetaDataType, Font> LoadDisplayedTagFonts()
        {
            var fonts = new Dictionary<MetaDataType, Font>();
            try
            {
                if (!File.Exists(TagFontSettingsFilePath))
                    return fonts;

                var serializer = new XmlSerializer(typeof(List<TagFontSetting>));
                using (var stream = File.OpenRead(TagFontSettingsFilePath))
                {
                    var settings = (List<TagFontSetting>)serializer.Deserialize(stream);
                    foreach (TagFontSetting setting in settings)
                    {
                        if (Enum.TryParse(setting.Field, out MetaDataType field) && Enum.IsDefined(typeof(MetaDataType), field) &&
                            !string.IsNullOrWhiteSpace(setting.Family) && setting.Size > 0)
                        {
                            try { fonts[field] = new Font(setting.Family, setting.Size, (FontStyle)setting.Style); }
                            catch { }
                        }
                    }
                }
                Trace("Album info popup: loaded " + fonts.Count + " tag font overrides.");
            }
            catch (Exception ex)
            {
                Trace("Album info popup: tag fonts could not be loaded: " + ex.Message);
            }
            return fonts;
        }

        private void ShowTagSelectionDialog(IWin32Window owner)
        {
            if (tagSettingsDialogOpen)
                return;

            tagSettingsDialogOpen = true;
            try
            {
                var fields = (MetaDataType[])Enum.GetValues(typeof(MetaDataType));
                using (var form = new TagSelectionForm(fields, displayedTagFields, displayedTagFonts))
                {
                    if (form.ShowDialog(owner) != DialogResult.OK)
                        return;

                    displayedTagFields = new HashSet<MetaDataType>(form.SelectedFields);
                    var selectedFonts = form.SelectedFonts;
                    Trace("Album info popup: settings dialog returned " + selectedFonts.Count + " tag font overrides.");
                    foreach (Font font in displayedTagFonts.Values)
                        font.Dispose();
                    displayedTagFonts = selectedFonts;
                    SaveTagSettings();

                    if (!string.IsNullOrWhiteSpace(selectedFileUrl))
                        ShowSelectedFileTags(selectedFileUrl, true);
                }
            }
            finally
            {
                tagSettingsDialogOpen = false;
            }
        }

        private void SaveTagSettings()
        {
            var names = new List<string>();
            foreach (MetaDataType field in displayedTagFields)
                names.Add(field.ToString());
            try
            {
                File.WriteAllLines(TagSelectionFilePath, names);
            }
            catch (Exception ex)
            {
                Trace("Album info popup: tag selections could not be saved: " + ex.Message);
            }

            var fontSettings = new List<TagFontSetting>();
            foreach (var pair in displayedTagFonts)
            {
                fontSettings.Add(new TagFontSetting
                {
                    Field = pair.Key.ToString(),
                    Family = pair.Value.FontFamily.Name,
                    Size = pair.Value.Size,
                    Style = (int)pair.Value.Style
                });
            }

            try
            {
                var serializer = new XmlSerializer(typeof(List<TagFontSetting>));
                using (var stream = File.Create(TagFontSettingsFilePath))
                    serializer.Serialize(stream, fontSettings);
                Trace("Album info popup: saved " + fontSettings.Count + " tag font overrides.");
            }
            catch (Exception ex)
            {
                Trace("Album info popup: tag fonts could not be saved: " + ex.Message);
            }
        }

        private void Trace(string message)
        {
            try { File.AppendAllText(DiagnosticLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine); } catch { }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    if (!contextMenuRegistered)
                        RegisterContextMenuItem();
                    break;
                case NotificationType.TrackChanged:
                    lastRenderedFileUrl = string.Empty;
                    if (popupForm != null && !popupForm.IsDisposed)
                    {
                        try { popupForm.Invoke((Action)(() => popupForm.Close())); }
                        catch { /* form may already be closing/disposed; safe to ignore */ }
                    }
                    break;
                case NotificationType.SelectedFileChanged:
                    ShowSelectedFileTags(sourceFileUrl);
                    break;
            }
        }

        private void RegisterContextMenuItem()
        {
            try
            {
                ToolStripItem item = mbApiInterface.MB_AddMenuItem(
                    "context.Main/PopUPAlbum info", null, ShowSelectedAlbumInfo);
                contextMenuRegistered = item != null;
                Trace(contextMenuRegistered
                    ? "Album info popup: registered context.Main/PopUPAlbum info."
                    : "Album info popup: context menu registration returned no item.");
            }
            catch (Exception ex)
            {
                Trace("Album info popup: context menu registration failed: " + ex.Message);
                contextMenuRegistered = false;
            }
        }

        private void InstallMouseHook()
        {
            if (mouseHookThread != null && mouseHookThread.IsAlive)
                return;

            mouseHookReady.Reset();
            mouseHookThread = new System.Threading.Thread(() =>
            {
                mouseHookThreadId = GetCurrentThreadId();
                try
                {
                    mouseProc = MouseHookCallback;
                    mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, IntPtr.Zero, 0);
                    if (mouseHookHandle == IntPtr.Zero)
                        Trace("Album info popup: mouse hook installation failed with error " + Marshal.GetLastWin32Error());
                    else
                        Trace("Album info popup: mouse hook installed with handle " + mouseHookHandle + " on thread " + mouseHookThreadId);
                }
                catch (Exception ex)
                {
                    Trace("Album info popup: mouse hook installation threw: " + ex);
                }
                finally
                {
                    mouseHookReady.Set();
                }

                if (mouseHookHandle != IntPtr.Zero)
                    Application.Run(new ApplicationContext());
            });
            mouseHookThread.IsBackground = true;
            mouseHookThread.SetApartmentState(System.Threading.ApartmentState.STA);
            mouseHookThread.Start();
            if (!mouseHookReady.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Trace("Album info popup: timed out waiting for mouse hook thread initialization.");
            }
        }

        private void RemoveMouseHook()
        {
            if (mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHookHandle);
                mouseHookHandle = IntPtr.Zero;
            }

            if (mouseHookThreadId != 0)
            {
                PostThreadMessage(mouseHookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                mouseHookThreadId = 0;
            }
        }

        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && instance != null)
            {
                if (IsMouseButtonDown(wParam) && GetCursorPos(out POINT cursorPos))
                    instance.DismissPopupIfClickedOutside(cursorPos);
            }

            return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
        }

        private static bool IsMouseButtonDown(IntPtr message)
        {
            return message == (IntPtr)WM_LBUTTONDOWN ||
                message == (IntPtr)WM_RBUTTONDOWN ||
                message == (IntPtr)WM_MBUTTONDOWN ||
                message == (IntPtr)WM_XBUTTONDOWN;
        }

        private bool DismissPopupIfClickedOutside(POINT cursorPos)
        {
            if (tagSettingsDialogOpen)
                return false;

            RatingPopupForm form = popupForm;
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return false;

            try
            {
                form.BeginInvoke((Action)(() =>
                {
                    if (!form.IsDisposed && form.Visible && !form.Bounds.Contains(cursorPos.X, cursorPos.Y))
                        form.Close();
                }));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ShowPopup(string content, IList<KeyValuePair<MetaDataType, string>> tagEntries = null)
        {
            RatingPopupForm existingForm = popupForm;
            if (existingForm != null && !existingForm.IsDisposed && existingForm.IsHandleCreated)
            {
                try
                {
                    existingForm.BeginInvoke((Action)(() =>
                    {
                        if (tagEntries == null)
                            existingForm.UpdateContent(content);
                        else
                            existingForm.UpdateTagContent(tagEntries, displayedTagFonts);
                    }));
                    return;
                }
                catch
                {
                }
            }

            var thread = new System.Threading.Thread(() =>
            {
                // Must be called before any window is created on this thread.
                // This can override the process's DPI awareness (which MusicBee
                // already locked in before our plugin loaded) on a per-thread basis,
                // which is what actually fixes the cropped/mis-scaled rendering.
                try { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }

                try
                {
                    var form = new RatingPopupForm(content, tagEntries, displayedTagFonts);
                    form.TagSettingsRequested += () => instance?.ShowTagSelectionDialog(form);
                    popupForm = form;

                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    instance?.Trace("Album info popup: window creation failed: " + ex);
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
    }

    /// <summary>Styled borderless popup that displays a single album title.</summary>
    public class RatingPopupForm : Form
    {
        // Plain Panel doesn't double-buffer, so redrawing it on every MouseMove (for the
        // hover preview) causes visible flicker/twitching. This subclass fixes that.
        private class DoubleBufferedPanel : Panel
        {
            public DoubleBufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        private const int BaseWidth = 760;
        private const int BaseHeight = 360;
        private const int BaseBorderInset = 16;
        private const int BaseCornerRadius = 32;
        private const int BaseShadowExpand = 4;
        private const int BaseShadowOffsetX = 5;
        private const int BaseShadowOffsetY = 6;
        private const float BaseBorderWidth = 3f;
        private const int BaseCloseSize = 28; // includes room for the button's drop shadow
        private int borderInset, cornerRadius, shadowExpand, shadowOffsetX, shadowOffsetY, closeSize;
        private float borderWidth;

        // Soft top-to-bottom shading for the card background.
        private static readonly Color GradientTop = Color.FromArgb(240, 240, 244);
        private static readonly Color GradientBottom = Color.FromArgb(208, 208, 214);
        private static readonly Color IconColor = Color.FromArgb(40, 40, 45);

        private readonly Timer topmostTimer;
        private readonly Panel infoPanel;
        private readonly RichTextBox detailsTextBox;
        private readonly Font detailsFont;
        private readonly Panel closeButton;
        private readonly Panel settingsButton;
        private readonly ToolTip settingsToolTip;
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        private ShadowForm shadowForm;

        public event Action TagSettingsRequested;

        public RatingPopupForm(
            string content,
            IList<KeyValuePair<Plugin.MetaDataType, string>> tagEntries,
            IDictionary<Plugin.MetaDataType, Font> tagFonts)
        {
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true; // reduces flicker from the gradient/border repainting
            BackColor = GradientBottom; // fallback; actual background is painted as a gradient below

            MinimumSize = new Size(520, 240);
            Size = new Size(BaseWidth, BaseHeight);
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.Right - Width - 20, screen.Bottom - Height - 20);

            infoPanel = new DoubleBufferedPanel();
            infoPanel.Paint += (s, e) => PaintGradientSlice(e.Graphics, infoPanel);
            Controls.Add(infoPanel);

            detailsFont = new Font("Segoe UI", 10f);
            detailsTextBox = new RichTextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = GradientTop,
                ForeColor = Color.FromArgb(28, 28, 30),
                Font = detailsFont,
                DetectUrls = false,
                Text = string.IsNullOrWhiteSpace(content) ? "No tags found" : content
            };
            infoPanel.Controls.Add(detailsTextBox);

            closeButton = MakeRaisedButton((g, bounds) => DrawCloseIcon(g, bounds), () => Close());
            Controls.Add(closeButton);
            closeButton.BringToFront();

            settingsButton = MakeRaisedButton((g, bounds) => DrawSettingsIcon(g, bounds), () => TagSettingsRequested?.Invoke());
            settingsButton.AccessibleName = "Choose displayed tags";
            Controls.Add(settingsButton);
            settingsButton.BringToFront();
            settingsToolTip = new ToolTip();
            settingsToolTip.SetToolTip(settingsButton, "Choose displayed tags");
            if (tagEntries != null)
                UpdateTagContent(tagEntries, tagFonts);

            bool dragging = false;
            Point dragStartPoint = Point.Empty;

            MouseEventHandler dragStart = (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    dragging = true;
                    dragStartPoint = e.Location;
                }
            };
            MouseEventHandler dragMove = (s, e) =>
            {
                if (dragging)
                {
                    Point diff = new Point(e.X - dragStartPoint.X, e.Y - dragStartPoint.Y);
                    Location = new Point(Location.X + diff.X, Location.Y + diff.Y);
                }
            };
            MouseEventHandler dragEnd = (s, e) =>
            {
                if (dragging)
                    dragging = false;
            };

            infoPanel.MouseDown += dragStart;
            infoPanel.MouseMove += dragMove;
            infoPanel.MouseUp += dragEnd;
            MouseDown += dragStart;
            MouseMove += dragMove;
            MouseUp += dragEnd;

            topmostTimer = new Timer { Interval = 1000 };
            topmostTimer.Tick += (s, e) =>
            {
                if (shadowForm != null && shadowForm.IsHandleCreated)
                    SetWindowPos(shadowForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (IsHandleCreated)
                    SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            };
            FormClosed += (s, e) =>
            {
                topmostTimer.Stop();
                topmostTimer.Dispose();
                shadowForm?.Close();
                shadowForm?.Dispose();
                detailsFont.Dispose();
                settingsToolTip.Dispose();
            };

            shadowForm = new ShadowForm();
            RecalculateLayout();
        }

        public void UpdateContent(string content)
        {
            string nextText = string.IsNullOrWhiteSpace(content) ? "No tags found" : content;
            if (!string.Equals(detailsTextBox.Text, nextText, StringComparison.Ordinal))
                detailsTextBox.Text = nextText;
        }

        public void UpdateTagContent(
            IList<KeyValuePair<Plugin.MetaDataType, string>> entries,
            IDictionary<Plugin.MetaDataType, Font> tagFonts)
        {
            if (entries == null || entries.Count == 0)
            {
                UpdateContent("No tags found");
                return;
            }

            detailsTextBox.SuspendLayout();
            SendMessage(detailsTextBox.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                var content = new StringBuilder();
                var lineRanges = new List<Tuple<Plugin.MetaDataType, int, int>>();
                foreach (var entry in entries)
                {
                    string line = entry.Key + ": " + entry.Value + Environment.NewLine;
                    int start = content.Length;
                    content.Append(line);
                    lineRanges.Add(Tuple.Create(entry.Key, start, line.Length));
                }

                detailsTextBox.Text = content.ToString();
                foreach (var range in lineRanges)
                {
                    if (tagFonts.TryGetValue(range.Item1, out Font tagFont))
                    {
                        detailsTextBox.Select(range.Item2, range.Item3);
                        detailsTextBox.SelectionFont = tagFont;
                    }
                }
                detailsTextBox.Select(0, 0);
            }
            finally
            {
                SendMessage(detailsTextBox.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                detailsTextBox.ResumeLayout();
                detailsTextBox.Invalidate();
            }

        }

        // Native OS drop shadow for borderless windows (the same effect Windows uses
        // for tooltips/menus) - makes the card look like it's floating instead of flat.
        protected override CreateParams CreateParams
        {
            get
            {
                const int CS_DROPSHADOW = 0x00020000;
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        private static GraphicsPath GetRoundedRectPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // A small borderless window painted as a plain dark, semi-transparent rounded
        // silhouette, positioned just behind and below-right of the card. This gives a
        // clearly visible "dropped" shadow that doesn't depend on the OS/DWM honoring
        // CS_DROPSHADOW for a custom-Region window (which is often too subtle to notice).
        private class ShadowForm : Form
        {
            public ShadowForm()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = Color.Black;
                Opacity = 0.30;
                DoubleBuffered = true;
            }

            public void SetShape(int width, int height, int radius)
            {
                Size = new Size(width, height);
                using (var path = GetRoundedRectPath(new Rectangle(0, 0, width, height), radius))
                {
                    Region = new Region(path);
                }
            }
        }

        private void RepositionShadow()
        {
            if (shadowForm != null)
                shadowForm.Location = new Point(Location.X - shadowExpand + shadowOffsetX, Location.Y - shadowExpand + shadowOffsetY);
        }

        private void RecalculateLayout()
        {
            float scale = Math.Min((float)Width / BaseWidth, (float)Height / BaseHeight);
            scale = Math.Max(0.4f, Math.Min(3f, scale));

            borderInset = Math.Max(6, (int)Math.Round(BaseBorderInset * scale));
            cornerRadius = Math.Max(4, (int)Math.Round(BaseCornerRadius * scale));
            shadowExpand = Math.Max(2, (int)Math.Round(BaseShadowExpand * scale));
            shadowOffsetX = Math.Max(2, (int)Math.Round(BaseShadowOffsetX * scale));
            shadowOffsetY = Math.Max(2, (int)Math.Round(BaseShadowOffsetY * scale));
            borderWidth = Math.Max(1.5f, BaseBorderWidth * scale);
            closeSize = Math.Max(22, (int)Math.Round(BaseCloseSize * scale));

            if (infoPanel != null)
                infoPanel.Bounds = new Rectangle(borderInset, borderInset,
                    Math.Max(1, Width - borderInset * 2), Math.Max(1, Height - borderInset * 2));
            if (detailsTextBox != null)
            {
                detailsTextBox.Bounds = new Rectangle(12, 12,
                    Math.Max(1, infoPanel.Width - 24), Math.Max(1, infoPanel.Height - 24));
            }
            if (closeButton != null)
                closeButton.Bounds = new Rectangle(Width - borderInset - closeSize, borderInset, closeSize, closeSize);
            if (settingsButton != null)
                settingsButton.Bounds = new Rectangle(Width - borderInset - closeSize * 2 - 6, borderInset, closeSize, closeSize);

            using (var path = GetRoundedRectPath(new Rectangle(0, 0, Width, Height), cornerRadius))
            {
                Region = new Region(path);
            }

            if (shadowForm != null)
            {
                shadowForm.SetShape(Width + shadowExpand * 2, Height + shadowExpand * 2, cornerRadius + shadowExpand);
                RepositionShadow();
            }

            infoPanel?.Invalidate();
            Invalidate();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RecalculateLayout();
            shadowForm.Show();
            LocationChanged += (s, e2) => RepositionShadow();
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (infoPanel != null)
                RecalculateLayout();
        }

        // FormBorderStyle.None means there's no OS-drawn border to grab for resizing, so we
        // manually report the edge/corner hit-test codes ourselves; Windows then runs its
        // normal interactive resize loop (cursor changes, live drag, ResizeEnd event) exactly
        // as it would for a regular window border.
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
            HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeBorder = 8; // must stay <= the smallest borderInset can scale down to

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result == HTCLIENT)
                {
                    int screenX = unchecked((short)((long)m.LParam & 0xFFFF));
                    int screenY = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                    Point pos = PointToClient(new Point(screenX, screenY));

                    bool onLeft = pos.X <= ResizeBorder;
                    bool onRight = pos.X >= Width - ResizeBorder;
                    bool onTop = pos.Y <= ResizeBorder;
                    bool onBottom = pos.Y >= Height - ResizeBorder;

                    if (onTop && onLeft) m.Result = (IntPtr)HTTOPLEFT;
                    else if (onTop && onRight) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (onBottom && onLeft) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (onBottom && onRight) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (onLeft) m.Result = (IntPtr)HTLEFT;
                    else if (onRight) m.Result = (IntPtr)HTRIGHT;
                    else if (onTop) m.Result = (IntPtr)HTTOP;
                    else if (onBottom) m.Result = (IntPtr)HTBOTTOM;
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

            var outerRect = new Rectangle(0, 0, Width, Height);

            using (var outerPath = GetRoundedRectPath(outerRect, cornerRadius))
            using (var borderPen = new Pen(Color.FromArgb(150, 150, 156), borderWidth) { Alignment = PenAlignment.Inset })
            {
                e.Graphics.DrawPath(borderPen, outerPath);
            }

            // A thin, semi-transparent highlight just inside the border gives the
            // card a touch of glassy depth instead of a flat single-tone edge.
            var innerRect = Rectangle.Inflate(outerRect, -(int)borderWidth - 1, -(int)borderWidth - 1);
            int innerRadius = Math.Max(1, cornerRadius - (int)borderWidth);
            using (var innerPath = GetRoundedRectPath(innerRect, innerRadius))
            using (var highlightPen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            {
                e.Graphics.DrawPath(highlightPen, innerPath);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Soft top-to-bottom gradient instead of a flat fill; the Region already
            // clips this to the rounded card shape, so painting the full rectangle is fine.
            using (var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Math.Max(1, Height)),
                       GradientTop, GradientBottom, LinearGradientMode.Vertical))
            {
                e.Graphics.FillRectangle(brush, new Rectangle(0, 0, Width, Height));
            }
        }

        // Paints the portion of the form's overall gradient that falls behind a given
        // child control, using that control's own Graphics context (whose (0,0) is the
        // control's top-left, not the form's). Building the brush's rectangle offset by
        // -control.Top and sized to the full form height makes the two line up exactly,
        // so the label/star panel appear to share one continuous background.
        private void PaintGradientSlice(Graphics g, Control control)
        {
            var rect = new Rectangle(0, -control.Top, Math.Max(1, control.Width), Math.Max(1, Height));
            using (var brush = new LinearGradientBrush(rect, GradientTop, GradientBottom, LinearGradientMode.Vertical))
            {
                g.FillRectangle(brush, control.ClientRectangle);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RecalculateLayout();
            Refresh();

            if (shadowForm != null && shadowForm.IsHandleCreated)
                SetWindowPos(shadowForm.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            topmostTimer.Start();
        }

    #if false
        // Retired star-rating implementation.
        // Total width of the row of stars at the current scaled size, and the x-offset
        // that centers that row within the star panel's actual width. Shared by both
        // drawing and hit-testing so a click always lands on the star it looks like it did.
        private int StarRowWidth => StarCount * starSize + (StarCount - 1) * starSpacing;
        private int StarRowStartX => Math.Max(0, (starPanel.Width - StarRowWidth) / 2);

        // Given an x-coordinate within the star row, return the rating value
        // (in 0.5 increments) that point corresponds to. Uses the current scaled
        // starSize/starSpacing and the row's centered start position, so it always
        // matches what's on screen.
        private double RatingFromX(int x)
        {
            int stepWidth = starSize + starSpacing;
            int adjustedX = x - StarRowStartX; // account for the same centering DrawStar uses

            if (adjustedX < 0) return 0;

            int index = adjustedX / stepWidth;          // which star (0-based)
            if (index >= StarCount) return StarCount;

            int xWithinStar = adjustedX - (index * stepWidth);
            bool leftHalf = xWithinStar < (starSize / 2);

            return leftHalf ? index + 0.5 : index + 1.0;
        }

        private void StarPanel_MouseMove(object sender, MouseEventArgs e)
        {
            displayRating = RatingFromX(e.X);
            starPanel.Invalidate();
        }

        private void StarPanel_MouseClick(object sender, MouseEventArgs e)
        {
            committedRating = RatingFromX(e.X);
            displayRating = committedRating;
            starPanel.Invalidate();

            RatingSelected?.Invoke(committedRating);
            // Stays open after rating; Plugin.cs closes it when the track ends
            // (next TrackChanged notification), so the transport controls remain usable.
        }

        private void StarPanel_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;

            // Panel's own background paints opaque before this event fires (transparent
            // BackColor doesn't reliably work), so we deliberately paint over the whole
            // client area with our own matching gradient slice first.
            PaintGradientSlice(e.Graphics, starPanel);
            DrawSpectrum(e.Graphics, starPanel.Bounds);

            for (int i = 0; i < StarCount; i++)
            {
                int x = i * (starSize + starSpacing) + StarRowStartX;
                int y = (starPanel.Height - starSize) / 2 + starVerticalNudge;

                double starValue = displayRating - i; // how "filled" this star is: 0, 0.5, or 1+
                starValue = Math.Max(0, Math.Min(1, starValue));

                DrawStar(e.Graphics, x, y, starSize, starValue);
            }
        }

        private static readonly Color GoldLight = Color.FromArgb(255, 238, 130);
        private static readonly Color GoldDark = Color.FromArgb(198, 128, 0);
        private static readonly Color GoldOutline = Color.FromArgb(150, 92, 0);
        private static readonly Color EmptyLight = Color.FromArgb(238, 238, 242);
        private static readonly Color EmptyDark = Color.FromArgb(176, 176, 184);

        private void DrawStar(Graphics g, int x, int y, int size, double fillFraction)
        {
            PointF[] points = StarPoints(x, y, size);
            var center = new PointF(x + size / 2f, y + size / 2f);

            // Soft drop shadow, offset down-right, so the star looks raised off the card.
            float off = Math.Max(1f, size / 22f);
            var shadow = new PointF[points.Length];
            for (int i = 0; i < points.Length; i++)
                shadow[i] = new PointF(points[i].X + off * 0.6f, points[i].Y + off * 1.2f);
            using (var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillPolygon(shadowBrush, shadow);

            // Empty (grey) star, faceted the same way as the gold one.
            DrawFacetedStar(g, points, center, EmptyDark, EmptyLight);
            using (var emptyPen = new Pen(Color.FromArgb(170, 150, 150, 158), 1f))
                g.DrawPolygon(emptyPen, points);

            if (fillFraction <= 0)
                return;

            // Gold star, clipped to the filled fraction (so half-stars show half gold).
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(x, y, (int)Math.Round(size * fillFraction), size), CombineMode.Intersect);

            DrawFacetedStar(g, points, center, GoldDark, GoldLight);

            // Glossy highlight fading down from the top.
            using (var glossBrush = new LinearGradientBrush(new Rectangle(x, y, size, size),
                       Color.FromArgb(95, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillPolygon(glossBrush, points);

            using (var goldPen = new Pen(GoldOutline, Math.Max(1f, size / 50f)) { LineJoin = LineJoin.Round })
                g.DrawPolygon(goldPen, points);

            g.Clip = oldClip;
            oldClip.Dispose();
        }

        // Draws a 5-point star as 10 triangular facets (two per point) lit from the top-left,
        // which is what gives it the raised, pyramid-like 3D look.
        private static void DrawFacetedStar(Graphics g, PointF[] pts, PointF center, Color dark, Color light)
        {
            // A mid-tone base underneath hides hairline seams between the anti-aliased facets.
            using (var baseBrush = new SolidBrush(LerpColor(dark, light, 0.5f)))
                g.FillPolygon(baseBrush, pts);

            const double lightAngle = -3 * Math.PI / 4; // light comes from the top-left
            for (int i = 0; i < 10; i += 2)             // even indices are the outer points
            {
                FillFacet(g, center, pts[i], pts[(i + 1) % 10], dark, light, lightAngle);
                FillFacet(g, center, pts[i], pts[(i + 9) % 10], dark, light, lightAngle);
            }
        }

        private static void FillFacet(Graphics g, PointF c, PointF a, PointF b, Color dark, Color light, double lightAngle)
        {
            float mx = (a.X + b.X + c.X) / 3f - c.X;
            float my = (a.Y + b.Y + c.Y) / 3f - c.Y;
            double facing = Math.Atan2(my, mx);

            float t = (float)(0.5 + 0.5 * Math.Cos(facing - lightAngle));
            t = Math.Max(0f, Math.Min(1f, t * 1.15f - 0.075f)); // slight contrast boost

            using (var brush = new SolidBrush(LerpColor(dark, light, t)))
                g.FillPolygon(brush, new[] { c, a, b });
        }

        private static Color LerpColor(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // Standard 5-point star polygon inside a size x size box
        private PointF[] StarPoints(int x, int y, int size)
        {
            float cx = x + size / 2f;
            float cy = y + size / 2f;
            float outerR = size / 2f;
            float innerR = outerR * 0.4f;

            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double angle = Math.PI / 2 * 3 + i * Math.PI / 5;
                float r = (i % 2 == 0) ? outerR : innerR;
                pts[i] = new PointF(
                    cx + (float)(r * Math.Cos(angle)),
                    cy + (float)(r * Math.Sin(angle))
                );
            }
            return pts;
        }
#endif

        // A round, raised 3D button: drop shadow, gradient body, glossy rim highlight and a
        // border. It visibly sinks (shadow shrinks, gradient flips) while the mouse is held down.
        private Panel MakeRaisedButton(Action<Graphics, Rectangle> paintIcon, Action onClick)
        {
            bool hovering = false, pressed = false;
            var btn = new DoubleBufferedPanel { Cursor = Cursors.Hand };
            btn.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                PaintGradientSlice(g, btn);

                int w = btn.Width, h = btn.Height;
                int sp = Math.Max(2, w / 9);                       // room reserved for the shadow
                int push = pressed ? Math.Max(1, sp / 2) : 0;      // pressed = sunk toward the shadow
                var body = new Rectangle(push, push, Math.Max(4, w - sp), Math.Max(4, h - sp));

                if (!pressed)
                {
                    var shadowRect = new Rectangle(body.X + sp / 2, body.Y + sp, body.Width, body.Height);
                    using (var shadowPath = GetRoundedRectPath(shadowRect, shadowRect.Width / 2))
                    using (var shadowBrush = new SolidBrush(Color.FromArgb(75, 0, 0, 0)))
                        g.FillPath(shadowBrush, shadowPath);
                }

                Color top = hovering ? Color.FromArgb(255, 255, 255) : Color.FromArgb(248, 248, 252);
                Color bottom = hovering ? Color.FromArgb(218, 218, 226) : Color.FromArgb(198, 198, 208);
                if (pressed) { var t = top; top = bottom; bottom = t; } // flip the light to look pushed in

                using (var bodyPath = GetRoundedRectPath(body, body.Width / 2))
                {
                    using (var bodyBrush = new LinearGradientBrush(body, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(bodyBrush, bodyPath);

                    // Glossy rim just inside the edge, brightest at the top.
                    var rim = Rectangle.Inflate(body, -2, -2);
                    if (rim.Width > 2 && rim.Height > 2)
                    {
                        using (var rimPath = GetRoundedRectPath(rim, rim.Width / 2))
                        using (var rimBrush = new LinearGradientBrush(rim, Color.FromArgb(pressed ? 60 : 210, 255, 255, 255),
                                   Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        using (var rimPen = new Pen(rimBrush, 1.5f))
                            g.DrawPath(rimPen, rimPath);
                    }

                    using (var borderPen = new Pen(Color.FromArgb(150, 130, 130, 142), 1.2f))
                        g.DrawPath(borderPen, bodyPath);
                }

                int pad = Math.Max(4, body.Width / 3); // extra padding shrinks the X specifically
                paintIcon(g, Rectangle.Inflate(body, -pad, -pad));
            };
            btn.MouseEnter += (s, e) => { hovering = true; btn.Invalidate(); };
            btn.MouseLeave += (s, e) => { hovering = false; pressed = false; btn.Invalidate(); };
            btn.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; btn.Invalidate(); } };
            btn.MouseUp += (s, e) => { pressed = false; btn.Invalidate(); };
            btn.Click += (s, e) => onClick?.Invoke();
            return btn;
        }

        private static void DrawCloseIcon(Graphics g, Rectangle bounds)
        {
            using (var pen = new Pen(IconColor, Math.Max(1.5f, bounds.Width / 7f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                g.DrawLine(pen, bounds.Right, bounds.Top, bounds.Left, bounds.Bottom);
            }
        }

        private static void DrawSettingsIcon(Graphics g, Rectangle bounds)
        {
            float centerX = bounds.Left + bounds.Width / 2f;
            float centerY = bounds.Top + bounds.Height / 2f;
            float outerRadius = Math.Min(bounds.Width, bounds.Height) * 0.36f;
            float innerRadius = outerRadius * 0.35f;
            using (var pen = new Pen(IconColor, Math.Max(1.4f, bounds.Width / 9f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            })
            {
                for (int spoke = 0; spoke < 8; spoke++)
                {
                    double angle = spoke * Math.PI / 4;
                    float startX = centerX + (float)Math.Cos(angle) * outerRadius * 0.62f;
                    float startY = centerY + (float)Math.Sin(angle) * outerRadius * 0.62f;
                    float endX = centerX + (float)Math.Cos(angle) * outerRadius;
                    float endY = centerY + (float)Math.Sin(angle) * outerRadius;
                    g.DrawLine(pen, startX, startY, endX, endY);
                }

                g.DrawEllipse(pen, centerX - outerRadius * 0.64f, centerY - outerRadius * 0.64f,
                    outerRadius * 1.28f, outerRadius * 1.28f);
                g.DrawEllipse(pen, centerX - innerRadius, centerY - innerRadius,
                    innerRadius * 2, innerRadius * 2);
            }
        }

    }
}