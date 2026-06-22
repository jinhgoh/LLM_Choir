using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MultiLLM
{
    // Per-machine DPI scaling. The app is declared System-DPI-aware (app.manifest),
    // so Windows renders it at native pixels instead of bitmap-stretching it. Fonts
    // are specified in points and therefore already grow with the display's scaling,
    // but every hand-set pixel size (bar heights, chip widths, control offsets) does
    // not — so at >100% display scaling the now-larger text overflowed the fixed-
    // height top tab bar and bottom prompt bar and got clipped. Multiplying every
    // literal pixel value by this factor keeps the boxes proportional to the text on
    // any machine. Computed once at startup from the system DPI (which, for a
    // System-DPI-aware process, is fixed for the process lifetime).
    static class Dpi
    {
        public static float Scale = 1f;

        public static void Init()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    Scale = g.DpiX / 96f;
            }
            catch { Scale = 1f; }
            if (Scale < 1f) Scale = 1f; // never shrink below the original design sizes
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float S(float px) { return px * Scale; }
        public static Padding S(Padding p) { return new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom)); }
        public static Size S(Size sz) { return new Size(S(sz.Width), S(sz.Height)); }
        public static Point S(Point pt) { return new Point(S(pt.X), S(pt.Y)); }
    }

    // One LLM service shown in its own embedded browser panel.
    class Site
    {
        public string Id;
        public string Name;
        public string Url;
        public Color Color;
        public string[] InputSelectors;
        public string[] SendSelectors;
        public string[] FileInputSelectors;
        public WebView2 Web;
        public CheckBox Enabled;   // include this LLM when sending the prompt
        public CheckBox Show;      // show/hide this LLM's panel (independent of Enabled)
        public Panel Panel;        // the whole panel (header + browser) for this LLM
        public Label Status;
    }

    // The bottom prompt box. A plain TextBox can't display a pasted image, so we
    // intercept the paste (WM_PASTE, which covers Ctrl+V, Shift+Insert and the
    // right-click menu) and hand it to the host: if the host consumes it (because
    // the clipboard held an image to broadcast to the panels), the default text
    // paste is skipped; otherwise normal text paste proceeds untouched.
    class PromptTextBox : TextBox
    {
        const int WM_PASTE = 0x0302;
        const int WM_MOUSEWHEEL = 0x020A;
        const int WHEEL_DELTA = 120;
        const int EM_LINESCROLL = 0x00B6; // scroll a multiline edit by N lines (lParam)

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public Func<bool> OnPaste; // return true if the paste was handled as an image

        // Middle-button click-and-pan ("autoscroll"), like a browser. The native
        // edit control only scrolls by wheel/scrollbar, so we drive it ourselves:
        // middle-click anchors an origin, then a timer scrolls by a number of lines
        // proportional to how far the cursor has moved from that anchor.
        System.Windows.Forms.Timer autoScrollTimer;
        Point autoScrollOrigin;
        bool autoScrolling;
        int mouseWheelRemainder;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE && OnPaste != null && OnPaste()) return;
            if (m.Msg == WM_MOUSEWHEEL)
            {
                ScrollByMouseWheel(m.WParam);
                return;
            }
            base.WndProc(ref m);
        }

        void ScrollByMouseWheel(IntPtr wParam)
        {
            int delta = (short)((((long)wParam) >> 16) & 0xffff);
            if (delta == 0) return;

            mouseWheelRemainder += delta;
            int notches = mouseWheelRemainder / WHEEL_DELTA;
            mouseWheelRemainder %= WHEEL_DELTA;
            if (notches == 0) return;

            int linesPerNotch = SystemInformation.MouseWheelScrollLines;
            if (linesPerNotch <= 0) linesPerNotch = Math.Max(1, ClientSize.Height / Math.Max(1, Font.Height));

            SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(-notches * linesPerNotch));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // A second click of any button while panning ends the gesture.
            if (autoScrolling) { StopAutoScroll(); return; }
            if (e.Button == MouseButtons.Middle) { StartAutoScroll(e.Location); return; }
            base.OnMouseDown(e);
        }

        void StartAutoScroll(Point origin)
        {
            autoScrolling = true;
            autoScrollOrigin = origin;
            Cursor = Cursors.NoMoveVert; // classic up/down anchor indicator
            Capture = true;              // keep getting moves even off the control
            if (autoScrollTimer == null)
            {
                autoScrollTimer = new System.Windows.Forms.Timer { Interval = 30 };
                autoScrollTimer.Tick += delegate { AutoScrollStep(); };
            }
            autoScrollTimer.Start();
        }

        void StopAutoScroll()
        {
            autoScrolling = false;
            if (autoScrollTimer != null) autoScrollTimer.Stop();
            Capture = false;
            Cursor = Cursors.IBeam; // the textbox's normal cursor
        }

        void AutoScrollStep()
        {
            if (!autoScrolling) return;
            int dy = PointToClient(MousePosition).Y - autoScrollOrigin.Y;
            if (Math.Abs(dy) < 10) return; // dead zone so a still cursor doesn't drift
            int lines = dy / 24;           // farther from the anchor → faster scroll
            if (lines == 0) lines = dy > 0 ? 1 : -1;
            SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && autoScrollTimer != null) autoScrollTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    // One full comparison workspace: prompt box + ChatGPT/Claude/Gemini panels.
    // Each browser tab hosts its own ComparisonView; they share one profile
    // (one CoreWebView2Environment), so logins are shared across all tabs.
    class ComparisonView : Panel
    {
        readonly List<Site> sites = new List<Site>();
        PromptTextBox prompt;
        FlowLayoutPanel toggles;
        FlowLayoutPanel showToggles;
        TableLayoutPanel center;
        TableLayoutPanel promptBar;
        FlowLayoutPanel btns;
        Splitter promptSplitter;
        bool initialized;

        // Set by MainForm; called with the first sent message so the tab is renamed.
        public Action<string> OnFirstSend;
        bool firstSendDone;

        // Set by MainForm; called when Ctrl+T / Ctrl+W is pressed while a panel has focus.
        public Action OnNewTabRequested;
        public Action OnCloseTabRequested;

        public ComparisonView()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 31, 34);

            // Panels are laid out left-to-right in this order: Gemini, ChatGPT, Claude.
            sites.Add(new Site
            {
                Id = "gemini", Name = "Gemini", Url = "https://gemini.google.com/app", Color = ColorTranslator.FromHtml("#4285F4"),
                InputSelectors = new[] { "rich-textarea div[contenteditable='true']", "div.ql-editor[contenteditable='true']", "div[contenteditable='true']", "textarea" },
                SendSelectors = new[] { "button[aria-label*='Send message']", "button[aria-label*='Send']" },
                FileInputSelectors = new[] { "input[type='file'][multiple]", "input[type='file']" }
            });
            sites.Add(new Site
            {
                Id = "chatgpt", Name = "ChatGPT", Url = "https://chatgpt.com/", Color = ColorTranslator.FromHtml("#10A37F"),
                InputSelectors = new[] { "#prompt-textarea", "textarea[data-testid='prompt-textarea']", "div[contenteditable='true']", "textarea" },
                SendSelectors = new[] { "button[data-testid='send-button']", "button[aria-label*='Send']" },
                FileInputSelectors = new[] { "input[type='file'][multiple]", "input[type='file']" }
            });
            sites.Add(new Site
            {
                Id = "claude", Name = "Claude", Url = "https://claude.ai/new", Color = ColorTranslator.FromHtml("#C96442"),
                InputSelectors = new[] { "div[contenteditable='true'].ProseMirror", "div[aria-label*='prompt'][contenteditable='true']", "div[contenteditable='true']", "textarea" },
                SendSelectors = new[] { "button[aria-label*='Send']", "button[data-testid='send-button']" },
                FileInputSelectors = new[] { "input[data-testid='file-upload']", "input[type='file'][multiple]", "input[type='file']" }
            });

            BuildUi();
        }

        Button MakeButton(string text, Color back)
        {
            Button b = new Button
            {
                Text = text,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = Color.White,
                Margin = Dpi.S(new Padding(2)),
                Padding = Dpi.S(new Padding(8, 4, 8, 4)),
                Font = new Font("Segoe UI", 9.5f)
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        Label MakeToggleLabel(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Color.FromArgb(154, 156, 161),
                AutoSize = true,
                Margin = Dpi.S(new Padding(0, 4, 6, 0)),
                Font = new Font("Segoe UI", 8.5f)
            };
        }

        void BuildUi()
        {
            // Prompt bar — docked to the BOTTOM of the workspace (the tab bar stays on top).
            promptBar = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Dpi.S(40), // compact start; grown to hug its content below
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(43, 45, 49),
                Padding = Dpi.S(new Padding(8))
            };
            promptBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            promptBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            prompt = new PromptTextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 11f),
                BackColor = Color.FromArgb(27, 28, 31),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                // A vertical scrollbar lets long prompts scroll — via the bar and
                // the mouse wheel (a multiline TextBox with ScrollBars.None ignores
                // the wheel entirely). WordWrap stays on, so no horizontal bar is
                // needed. Middle-button click-and-pan is added in PromptTextBox.
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true
            };
            prompt.KeyDown += delegate (object s, KeyEventArgs e)
            {
                // Enter sends to all; Shift+Enter inserts a line break.
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    SendToAll(true);
                }
            };
            // Pasting an image broadcasts it to every panel (same path as "Attach to all").
            prompt.OnPaste = HandlePromptPaste;
            promptBar.Controls.Add(prompt, 0, 0);

            btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false
            };

            Button sendBtn = MakeButton("Send to all (Enter)  ▷", Color.FromArgb(88, 101, 242));
            sendBtn.Click += delegate { SendToAll(true); };
            Button fillBtn = MakeButton("Fill only", Color.FromArgb(58, 60, 65));
            fillBtn.Click += delegate { SendToAll(false); };
            Button attachBtn = MakeButton("Attach to all", Color.FromArgb(58, 60, 65));
            attachBtn.Click += delegate { AttachToAll(); };
            Button newBtn = MakeButton("New chat (all)", Color.FromArgb(58, 60, 65));
            newBtn.Click += delegate
            {
                foreach (Site s in sites)
                    if (s.Enabled.Checked && s.Web.CoreWebView2 != null)
                        s.Web.Source = new Uri(s.Url);
            };

            FlowLayoutPanel actionRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0)
            };
            actionRow.Controls.Add(sendBtn);
            actionRow.Controls.Add(fillBtn);
            actionRow.Controls.Add(attachBtn);
            actionRow.Controls.Add(newBtn);

            // "Send:" row — which LLMs receive the prompt.
            toggles = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = Dpi.S(new Padding(0, 6, 0, 0))
            };
            toggles.Controls.Add(MakeToggleLabel("Send:"));

            // "Show:" row — which LLM panels are visible (independent of Send).
            showToggles = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = Dpi.S(new Padding(0, 2, 0, 0))
            };
            showToggles.Controls.Add(MakeToggleLabel("Show:"));

            btns.Controls.Add(actionRow);
            btns.Controls.Add(toggles);
            btns.Controls.Add(showToggles);
            promptBar.Controls.Add(btns, 1, 0);

            center = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.FromArgb(30, 31, 34)
            };
            for (int i = 0; i < 3; i++)
                center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3f));

            int col = 0;
            foreach (Site site in sites)
            {
                Site captured = site;

                Panel panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 31, 34) };

                Panel header = new Panel { Dock = DockStyle.Top, Height = Dpi.S(30), BackColor = Color.FromArgb(43, 45, 49) };
                Label name = new Label
                {
                    Text = site.Name,
                    ForeColor = site.Color,
                    Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                    AutoSize = true,
                    Location = Dpi.S(new Point(10, 7))
                };
                site.Status = new Label
                {
                    Text = "loading…",
                    ForeColor = Color.FromArgb(154, 156, 161),
                    Font = new Font("Segoe UI", 8f),
                    AutoSize = true,
                    Location = Dpi.S(new Point(95, 9))
                };
                Button reload = new Button
                {
                    Text = "↻",
                    Width = Dpi.S(34),
                    Dock = DockStyle.Right,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White
                };
                reload.FlatAppearance.BorderSize = 0;
                reload.Click += delegate { if (captured.Web.CoreWebView2 != null) captured.Web.Reload(); };
                header.Controls.Add(name);
                header.Controls.Add(site.Status);
                header.Controls.Add(reload);

                site.Web = new WebView2 { Dock = DockStyle.Fill };
                site.Web.CoreWebView2InitializationCompleted += delegate (object s, CoreWebView2InitializationCompletedEventArgs e)
                {
                    captured.Status.Text = e.IsSuccess ? "ready" : "init failed";
                };

                site.Enabled = new CheckBox
                {
                    Text = site.Name,
                    Checked = true,
                    ForeColor = Color.Gainsboro,
                    AutoSize = true,
                    Margin = Dpi.S(new Padding(0, 3, 10, 0))
                };
                // Checking "Send:" also shows the panel; unchecking "Send:" leaves
                // "Show:" alone (the user may still want to look at that panel).
                site.Enabled.CheckedChanged += delegate
                {
                    if (captured.Enabled.Checked) captured.Show.Checked = true;
                };
                toggles.Controls.Add(site.Enabled);

                site.Show = new CheckBox
                {
                    Text = site.Name,
                    Checked = true,
                    ForeColor = Color.Gainsboro,
                    AutoSize = true,
                    Margin = Dpi.S(new Padding(0, 3, 10, 0))
                };
                // Hiding a panel also stops sending to it; re-showing it does NOT
                // resume sending (the user must re-check "Send:" deliberately).
                site.Show.CheckedChanged += delegate
                {
                    if (!captured.Show.Checked) captured.Enabled.Checked = false;
                    ApplyVisibility();
                };
                showToggles.Controls.Add(site.Show);

                site.Panel = panel;
                panel.Controls.Add(site.Web);
                panel.Controls.Add(header);
                center.Controls.Add(panel, col, 0);
                col++;
            }

            // A thin splitter sits on the prompt bar's top edge; the user drags it up or
            // down (HSplit cursor) to make the typing area taller or shorter. A Bottom-
            // docked splitter resizes the control directly below it — the prompt bar.
            promptSplitter = new Splitter
            {
                Dock = DockStyle.Bottom,
                Height = Dpi.S(5),
                BackColor = Color.FromArgb(58, 60, 65),
                MinExtra = Dpi.S(160) // always keep room for the panels above
            };

            // Docking order: center fills the top, the splitter sits just above the
            // prompt bar, and the prompt bar hugs the bottom edge.
            Controls.Add(center);
            Controls.Add(promptSplitter);
            Controls.Add(promptBar);

            // Size the bar to its content (no empty gap below the checkboxes), and keep
            // that as the floor so the bottom "Show:" row is never clipped — by first
            // layout, by Windows' "make text bigger" setting, or by a too-short drag.
            btns.SizeChanged += delegate { RecalcPromptBar(); };
            HandleCreated += delegate { RecalcPromptBar(); };
            RecalcPromptBar();
        }

        // The prompt bar's natural content height (the button row + the two checkbox
        // rows). This is both the neat default height and the smallest the user can
        // drag the bar down to.
        int PromptContentHeight()
        {
            return btns.PreferredSize.Height + promptBar.Padding.Vertical;
        }

        // Hug the content by default and never let the bar clip its bottom row, while
        // preserving any taller height the user has dragged the splitter to.
        void RecalcPromptBar()
        {
            if (promptBar == null || btns == null) return;
            int min = PromptContentHeight();
            if (promptSplitter != null) promptSplitter.MinSize = min;
            if (promptBar.Height < min) promptBar.Height = min;
        }

        // Show or hide each LLM's panel (independent of whether it receives the prompt).
        // Hidden panels collapse their column so the visible ones share the full width.
        void ApplyVisibility()
        {
            int visible = 0;
            foreach (Site s in sites) if (s.Show.Checked) visible++;
            int shown = Math.Max(visible, 1);
            for (int i = 0; i < sites.Count; i++)
            {
                bool on = sites[i].Show.Checked;
                if (sites[i].Panel != null) sites[i].Panel.Visible = on;
                center.ColumnStyles[i].Width = on ? 100f / shown : 0f;
            }
        }

        public bool HasSavedTitle
        {
            get { return firstSendDone; }
            set { firstSendDone = value; }
        }

        public string[] CurrentUrls()
        {
            List<string> urls = new List<string>();
            foreach (Site s in sites)
            {
                string url = s.Url;
                try
                {
                    if (s.Web != null && s.Web.CoreWebView2 != null && !string.IsNullOrEmpty(s.Web.CoreWebView2.Source))
                        url = s.Web.CoreWebView2.Source;
                    else if (s.Web != null && s.Web.Source != null)
                        url = s.Web.Source.AbsoluteUri;
                }
                catch { }
                urls.Add(url);
            }
            return urls.ToArray();
        }

        // Initialize the three panels against the shared environment. Safe to call
        // once; navigates each panel to its saved URL, or to the service home page.
        public async Task InitAsync(CoreWebView2Environment env)
        {
            await InitAsync(env, null);
        }

        public async Task InitAsync(CoreWebView2Environment env, string[] startUrls)
        {
            if (initialized) return;
            initialized = true;
            for (int i = 0; i < sites.Count; i++)
            {
                Site s = sites[i];
                try
                {
                    if (!s.Web.IsHandleCreated) s.Web.CreateControl();
                    await s.Web.EnsureCoreWebView2Async(env);

                    // Links/citations that open via target="_blank" or window.open would
                    // otherwise spawn a bare WebView2 popup; send them to the user's
                    // actual default browser instead.
                    s.Web.CoreWebView2.NewWindowRequested += delegate (object sender, CoreWebView2NewWindowRequestedEventArgs e)
                    {
                        e.Handled = true;
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch { }
                    };

                    // Bridge Ctrl+T from inside the page (where WebView2 owns the
                    // keyboard) back to the host so it can open a new tab.
                    s.Web.CoreWebView2.WebMessageReceived += delegate (object sender, CoreWebView2WebMessageReceivedEventArgs e)
                    {
                        string m = "";
                        try { m = e.TryGetWebMessageAsString(); } catch { }
                        // Defer to after this event so we never dispose a panel mid-event.
                        if (m == "new-tab")
                            BeginInvoke((MethodInvoker)delegate { if (OnNewTabRequested != null) OnNewTabRequested(); });
                        else if (m == "close-tab")
                            BeginInvoke((MethodInvoker)delegate { if (OnCloseTabRequested != null) OnCloseTabRequested(); });
                    };
                    await s.Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                        "document.addEventListener('keydown',function(e){if(e.ctrlKey&&!e.shiftKey&&!e.altKey){" +
                        "if(e.key==='t'||e.key==='T'){e.preventDefault();try{window.chrome.webview.postMessage('new-tab');}catch(_){}}" +
                        "else if(e.key==='w'||e.key==='W'){e.preventDefault();try{window.chrome.webview.postMessage('close-tab');}catch(_){}}" +
                        "}}, true);");

                    s.Web.Source = StartUri(startUrls, i, s.Url);
                }
                catch (Exception ex) { s.Status.Text = "init error: " + ex.Message; }
            }
        }

        static Uri StartUri(string[] startUrls, int index, string fallback)
        {
            string value = null;
            if (startUrls != null && index >= 0 && index < startUrls.Length)
                value = startUrls[index];

            Uri uri;
            if (!string.IsNullOrEmpty(value) &&
                Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                return uri;

            return new Uri(fallback);
        }

        public void FocusPrompt()
        {
            if (prompt != null) prompt.Focus();
        }

        async void SendToAll(bool autoSend)
        {
            string text = prompt.Text.Trim();
            if (text.Length == 0) return;

            if (autoSend)
            {
                prompt.Clear(); // empty the box once the message is on its way
                if (!firstSendDone)
                {
                    firstSendDone = true;
                    if (OnFirstSend != null) OnFirstSend(text);
                }
            }

            foreach (Site site in sites)
            {
                if (!site.Enabled.Checked) continue;
                if (site.Web.CoreWebView2 == null) { site.Status.Text = "still loading…"; continue; }

                try
                {
                    site.Status.Text = "working…";
                    string r1 = await site.Web.ExecuteScriptAsync(BuildFillJs(text, site.InputSelectors));
                    string res = StripQuotes(r1);

                    if (autoSend && res != "no-input")
                    {
                        await Task.Delay(350);
                        string r2 = await site.Web.ExecuteScriptAsync(BuildSendJs(site.InputSelectors, site.SendSelectors));
                        res = StripQuotes(r2);
                    }
                    site.Status.Text = res;
                }
                catch (Exception ex) { site.Status.Text = "error: " + ex.Message; }
            }
        }

        // The file-upload counterpart of "Send to all": pick file(s) once, then
        // attach them to every enabled panel. Files go straight onto each site's
        // hidden <input type=file> via the DevTools protocol (the same mechanism
        // Playwright/Puppeteer use), so no native file dialog pops per panel and
        // the page's own upload then kicks in. After this, just type and Send.
        async void AttachToAll()
        {
            string[] paths;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Attach file(s) to all";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                paths = dlg.FileNames;
            }
            if (paths == null || paths.Length == 0) return;

            foreach (Site site in sites)
            {
                if (!site.Enabled.Checked) continue;
                if (site.Web.CoreWebView2 == null) { site.Status.Text = "still loading…"; continue; }
                site.Status.Text = "attaching…";
                site.Status.Text = await AttachFilesTo(site, paths);
            }
        }

        // Called from the prompt box's WM_PASTE. If the clipboard holds a bitmap
        // (e.g. a screenshot, or "Copy image" from a page), save it to a temp PNG
        // and broadcast it to every enabled panel — then suppress the default text
        // paste by returning true. A normal text copy can also carry a thumbnail
        // bitmap, so when there's text we leave the paste alone and let it insert.
        bool HandlePromptPaste()
        {
            try
            {
                if (!Clipboard.ContainsImage() || Clipboard.ContainsText()) return false;
                Image img = Clipboard.GetImage();
                if (img == null) return false;
                string path;
                using (img) path = SaveClipboardImage(img);
                if (path == null) return false;
                PasteImageToAll(new[] { path });
                return true;
            }
            catch { return false; }
        }

        // Attach a pasted image to every enabled panel, reusing the same file-input
        // machinery as "Attach to all" (direct injection for ChatGPT/Claude, the
        // intercepted file chooser for Gemini).
        async void PasteImageToAll(string[] paths)
        {
            foreach (Site site in sites)
            {
                if (!site.Enabled.Checked) continue;
                if (site.Web.CoreWebView2 == null) { site.Status.Text = "still loading…"; continue; }
                site.Status.Text = "pasting image…";
                site.Status.Text = await AttachFilesTo(site, paths);
            }
        }

        // Save a clipboard bitmap to a temp PNG (lossless, accepted by every site's
        // uploader) and return its path. Old paste files are tidied so the folder
        // doesn't grow without bound.
        static string SaveClipboardImage(Image img)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "MultiLLMConsole", "paste");
                Directory.CreateDirectory(dir);
                try
                {
                    foreach (string old in Directory.GetFiles(dir, "*.png"))
                        if ((DateTime.Now - File.GetLastWriteTime(old)).TotalHours > 1)
                            File.Delete(old);
                }
                catch { }
                string path = Path.Combine(dir, "paste_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                // Copy into a fresh bitmap so the save isn't tied to the clipboard's
                // backing stream, then write PNG.
                using (Bitmap bmp = new Bitmap(img))
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return path;
            }
            catch { return null; }
        }

        // Attach files to one site. Preferred path: drop them straight onto the
        // page's own <input type=file> via CDP DOM.setFileInputFiles (fast, exact;
        // works for ChatGPT/Claude). If a site has no standing file input (Gemini
        // only creates one mid-upload, behind a native dialog), drive the site's
        // own uploader with that dialog intercepted. Returns a panel-label string.
        async Task<string> AttachFilesTo(Site site, string[] paths)
        {
            CoreWebView2 cw = site.Web.CoreWebView2;
            try
            {
                await cw.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
                int inputId = await FindFileInputId(cw, site.FileInputSelectors);
                if (inputId != 0)
                {
                    DebugLog(site.Name + " FAST nodeId=" + inputId + " " + StripQuotes(await site.Web.ExecuteScriptAsync(EnumInputsJs)));
                    string setParams = "{\"nodeId\":" + inputId + ",\"files\":" + JsArr(paths) + "}";
                    await cw.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", setParams);
                    return Count(paths) + " attached (direct)";
                }

                // No standing input (e.g. Gemini): use its own uploader, intercepted.
                return await AttachViaFileChooser(site, paths);
            }
            catch (Exception ex) { return "attach error: " + ex.Message; }
        }

        // getDocument registers the root node so querySelector can run; returns the
        // nodeId of the first matching file input, or 0 if none is in the DOM.
        async Task<int> FindFileInputId(CoreWebView2 cw, string[] selectors)
        {
            int rootId = FirstNodeId(await cw.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0}"));
            if (rootId == 0) return 0;
            foreach (string sel in selectors)
            {
                string qs = "{\"nodeId\":" + rootId + ",\"selector\":" + JsStr(sel) + "}";
                int id = FirstNodeId(await cw.CallDevToolsProtocolMethodAsync("DOM.querySelector", qs));
                if (id != 0) return id;
            }
            return 0;
        }

        // For sites with no scriptable file input (Gemini only creates one mid-upload,
        // behind a native OS dialog): intercept that dialog via CDP and inject the
        // files into it. We try to open the uploader automatically with activated
        // clicks; if the heuristic misses, interception stays armed so the user's own
        // click on the site's Upload control is still caught and filled. No dialog shows.
        async Task<string> AttachViaFileChooser(Site site, string[] paths)
        {
            CoreWebView2 cw = site.Web.CoreWebView2;
            TaskCompletionSource<string> done = new TaskCompletionSource<string>();
            CoreWebView2DevToolsProtocolEventReceiver receiver = cw.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");

            EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> onChooser = null;
            onChooser = async delegate (object s, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
            {
                try
                {
                    DebugLog(site.Name + " CHOOSER params=" + e.ParameterObjectAsJson);
                    try { DebugLog(site.Name + " inputs=" + StripQuotes(await site.Web.ExecuteScriptAsync(EnumInputsJs))); } catch { }
                    int backend = FirstIntField(e.ParameterObjectAsJson, "backendNodeId");
                    string setParams;
                    string via;
                    if (backend != 0)
                    {
                        via = "picker,bn";
                        setParams = "{\"backendNodeId\":" + backend + ",\"files\":" + JsArr(paths) + "}";
                    }
                    else
                    {
                        // The chooser is open, so the input now exists — find it by nodeId.
                        int id = await FindFileInputId(cw, site.FileInputSelectors);
                        if (id == 0) { done.TrySetResult("chooser node not found"); return; }
                        via = "picker,qs";
                        setParams = "{\"nodeId\":" + id + ",\"files\":" + JsArr(paths) + "}";
                    }
                    await cw.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", setParams);
                    done.TrySetResult("ok:" + via);
                }
                catch (Exception ex) { done.TrySetResult("err:" + ex.Message); }
            };
            receiver.DevToolsProtocolEventReceived += onChooser;

            try
            {
                await cw.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                await cw.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");

                // Auto-open the uploader with *trusted* input events (CDP Input.*),
                // which replicate a real mouse. A synthetic element.click() is ignored
                // for file pickers — opening a file dialog needs a genuine user-activation
                // gesture, which only real/CDP input carries. First click the toolbar
                // button to open the menu, then the "Files" entry inside it.
                if (await ClickCoords(cw, TriggerCoordsJs))
                {
                    for (int i = 0; i < 5 && !done.Task.IsCompleted; i++)
                    {
                        await Task.Delay(350);
                        if (await ClickCoords(cw, FilesCoordsJs)) break;
                    }
                }

                if (!done.Task.IsCompleted)
                    site.Status.Text = "no dialog? click + ▸ Upload in " + site.Name;

                Task finished = await Task.WhenAny(done.Task, Task.Delay(45000));
                if (finished != done.Task) return "upload cancelled";

                string r = done.Task.Result;
                if (r.StartsWith("ok:")) return Count(paths) + " attached (" + r.Substring(3) + ")";
                if (r.StartsWith("err:")) return "attach error: " + r.Substring(4);
                return r;
            }
            finally
            {
                try { receiver.DevToolsProtocolEventReceived -= onChooser; } catch { }
                // Fire-and-forget: await isn't allowed in finally, and we don't need the result.
                try { var _ = cw.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":false}"); } catch { }
            }
        }

        static string Count(string[] paths)
        {
            return paths.Length == 1 ? "1 file" : paths.Length + " files";
        }

        // Diagnostics: list every file input in the page with its key attributes, so
        // we can see whether we're targeting the real uploader or a decoy.
        const string EnumInputsJs =
            "(function(){var a=document.querySelectorAll('input[type=file]');var o=[];" +
            "for(var i=0;i<a.length;i++){var e=a[i];o.push({accept:e.accept,multiple:e.multiple,name:e.name,id:e.id," +
            "cls:(e.className||'').slice(0,50),vis:e.offsetParent!=null});}return JSON.stringify({n:a.length,list:o});})()";

        // Append a line to attach_debug.log next to the exe (best-effort).
        static void DebugLog(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "attach_debug.log"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        // Runtime.evaluate params, optionally carrying transient user activation so
        // an input.click() is allowed to open (and thus trigger) a file chooser.
        static string RuntimeEval(string js, bool userGesture)
        {
            return "{\"expression\":" + JsStr(js) + ",\"userGesture\":" + (userGesture ? "true" : "false") + "}";
        }

        // JS returning the centre "x,y" (or "none") of the toolbar button that opens
        // the upload menu, so we can click it positionally with trusted input.
        const string TriggerCoordsJs =
            "(function(){function vis(el){var r=el.getBoundingClientRect();var s=getComputedStyle(el);" +
            "return r.width>1&&r.height>1&&s.visibility!=='hidden'&&s.display!=='none';}" +
            "var b=document.querySelectorAll('button,[role=button]');" +
            "for(var j=0;j<b.length;j++){var n=b[j];if(!vis(n))continue;" +
            "var lab=((n.getAttribute('aria-label')||'')+' '+(n.getAttribute('mattooltip')||'')+' '+(n.getAttribute('title')||'')).toLowerCase();" +
            "if(/upload|attach|add file|add photo|add image/.test(lab)){var r=n.getBoundingClientRect();" +
            "return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);}}return 'none';})()";

        // JS returning the centre "x,y" (or "none") of the local-file entry in the open
        // upload menu (Gemini labels it "Files"). Leaf-most exact match avoids hitting a
        // big wrapper or the image-gen / Drive / Photos entries.
        const string FilesCoordsJs =
            "(function(){function vis(el){var r=el.getBoundingClientRect();var s=getComputedStyle(el);" +
            "return r.width>1&&r.height>1&&s.visibility!=='hidden'&&s.display!=='none';}" +
            "var a=document.querySelectorAll('button,[role=menuitem],[role=option],a,div,span,li');" +
            "var best=null,bestN=1e9;for(var i=0;i<a.length;i++){var n=a[i];if(!vis(n))continue;" +
            "var t=(n.textContent||'').trim().toLowerCase();" +
            "if(t==='files'||t==='upload files'||t==='add files'||t==='upload from computer'){" +
            "var dc=n.querySelectorAll('*').length;if(dc<bestN){bestN=dc;best=n;}}}" +
            "if(!best)return 'none';var r=best.getBoundingClientRect();" +
            "return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2);})()";

        // Find an element via JS (it returns "x,y" of its centre, or "none"), then click
        // there with trusted CDP input events. Returns whether a click was dispatched.
        async Task<bool> ClickCoords(CoreWebView2 cw, string findJs)
        {
            string v = EvalString(await cw.CallDevToolsProtocolMethodAsync("Runtime.evaluate", RuntimeEval(findJs, false)));
            int comma = v.IndexOf(',');
            if (comma <= 0) return false;
            int x, y;
            if (!int.TryParse(v.Substring(0, comma), out x)) return false;
            if (!int.TryParse(v.Substring(comma + 1), out y)) return false;
            await DispatchClick(cw, x, y);
            return true;
        }

        // A real left click at (x,y) via CDP — trusted input, so it carries the user
        // activation a file dialog requires (a synthetic .click() does not).
        async Task DispatchClick(CoreWebView2 cw, int x, int y)
        {
            string at = "\"x\":" + x + ",\"y\":" + y;
            await cw.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseMoved\"," + at + "}");
            await cw.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mousePressed\"," + at + ",\"button\":\"left\",\"buttons\":1,\"clickCount\":1}");
            await cw.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", "{\"type\":\"mouseReleased\"," + at + ",\"button\":\"left\",\"buttons\":0,\"clickCount\":1}");
        }

        // Extract result.value from a Runtime.evaluate reply for a string-returning expr.
        static string EvalString(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"value\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }

        // Pull the first "nodeId":N out of a CDP JSON reply (0 = none / not found).
        static int FirstNodeId(string json) { return FirstIntField(json, "nodeId"); }

        // Pull the first "<field>":N integer out of a CDP JSON reply (0 = absent).
        static int FirstIntField(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            Match m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*(\\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        // --- JS builders (run inside each site's page) ---

        const string FindVisible =
            "function fv(ss){for(var i=0;i<ss.length;i++){var els;try{els=document.querySelectorAll(ss[i]);}catch(e){continue;}" +
            "for(var j=0;j<els.length;j++){var el=els[j];var r=el.getBoundingClientRect();var s=getComputedStyle(el);" +
            "if(r.width>0&&r.height>0&&s.visibility!=='hidden'&&s.display!=='none')return el;}}return null;}";

        static string BuildFillJs(string text, string[] inputSelectors)
        {
            return "(function(){var sels=" + JsArr(inputSelectors) + ";var text=" + JsStr(text) + ";" + FindVisible +
                "var input=fv(sels);if(!input)return 'no-input';input.focus();var tag=input.tagName.toLowerCase();" +
                "if(tag==='textarea'||tag==='input'){try{input.select();}catch(e){}var ok=document.execCommand('insertText',false,text);" +
                "if(!ok||input.value===''){var proto=tag==='textarea'?window.HTMLTextAreaElement.prototype:window.HTMLInputElement.prototype;" +
                "var d=Object.getOwnPropertyDescriptor(proto,'value');if(d&&d.set){d.set.call(input,text);}else{input.value=text;}" +
                "input.dispatchEvent(new Event('input',{bubbles:true}));}}" +
                "else{var range=document.createRange();range.selectNodeContents(input);var sel=window.getSelection();" +
                "sel.removeAllRanges();sel.addRange(range);var ok2=document.execCommand('insertText',false,text);" +
                "if(!ok2){input.textContent=text;input.dispatchEvent(new InputEvent('input',{bubbles:true}));}}" +
                "return 'filled';})()";
        }

        static string BuildSendJs(string[] inputSelectors, string[] sendSelectors)
        {
            return "(function(){var inSels=" + JsArr(inputSelectors) + ";var sendSels=" + JsArr(sendSelectors) + ";" + FindVisible +
                "var btn=fv(sendSels);if(btn&&!btn.disabled&&btn.getAttribute('aria-disabled')!=='true'){btn.click();return 'sent';}" +
                "var input=fv(inSels);if(input){input.focus();['keydown','keypress','keyup'].forEach(function(t){" +
                "input.dispatchEvent(new KeyboardEvent(t,{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));});return 'sent (enter)';}" +
                "return 'no-send';})()";
        }

        static string JsStr(string s)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string JsArr(string[] arr)
        {
            List<string> parts = new List<string>();
            foreach (string a in arr) parts.Add(JsStr(a));
            return "[" + string.Join(",", parts.ToArray()) + "]";
        }

        static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    public class MainForm : Form
    {
        CoreWebView2Environment env;
        FlowLayoutPanel tabStrip;
        Panel content;
        Button addBtn;
        Button helpBtn;
        readonly List<TabItem> tabs = new List<TabItem>();
        int counter = 0;
        bool restoringSession;
        const string SessionHeader = "LLMChoirSession1";

        class TabItem
        {
            public Panel Chip;
            public Label Title;
            public ComparisonView View;
        }

        class SavedTab
        {
            public string Title;
            public bool HasSavedTitle;
            public string[] Urls;
        }

        class SavedSession
        {
            public int ActiveIndex;
            public readonly List<SavedTab> Tabs = new List<SavedTab>();
        }

        public MainForm()
        {
            Text = "LLM Choir";
            // Manual DPI scaling (Dpi.S) is the single source of truth; turn off
            // WinForms' own auto-scaling so the two never compound.
            AutoScaleMode = AutoScaleMode.None;
            // Scale the default window to the display, but never larger than the
            // screen's working area (high-DPI machines have fewer logical pixels).
            Size want = Dpi.S(new Size(1680, 1000));
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(want.Width, wa.Width);
            Height = Math.Min(want.Height, wa.Height);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 25, 28);

            // Top bar: the tab strip fills the left; the ? help button is
            // pinned to the far-right corner.
            Panel topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = Dpi.S(34),
                BackColor = Color.FromArgb(24, 25, 28)
            };

            tabStrip = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 25, 28),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = Dpi.S(new Padding(6, 6, 0, 0))
            };

            addBtn = new Button
            {
                Text = "+",
                Width = Dpi.S(30),
                Height = Dpi.S(24),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(45, 47, 52),
                Margin = Dpi.S(new Padding(4, 0, 0, 0)),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            addBtn.FlatAppearance.BorderSize = 0;
            addBtn.Click += delegate { AddTab(); };
            tabStrip.Controls.Add(addBtn);

            helpBtn = new Button
            {
                Text = "?",
                Width = Dpi.S(34),
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(45, 47, 52),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            helpBtn.FlatAppearance.BorderSize = 0;
            helpBtn.Click += delegate { ShowGuide(); };

            topBar.Controls.Add(tabStrip); // fills the left
            topBar.Controls.Add(helpBtn);  // pinned to the right corner

            content = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 31, 34) };

            Controls.Add(content);
            Controls.Add(topBar);

            Shown += async delegate
            {
                try
                {
                    env = await CoreWebView2Environment.CreateAsync(null, SharedProfile(), null);
                    await RestoreTabsAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("WebView2 init failed: " + ex.Message, "LLM Choir");
                }
            };
            FormClosing += delegate { SaveSession(); };
        }

        async Task RestoreTabsAsync()
        {
            SavedSession session = LoadSession();
            if (session == null || session.Tabs.Count == 0)
            {
                await AddTabAsync(null, true);
                return;
            }

            int active = session.ActiveIndex;
            if (active < 0 || active >= session.Tabs.Count) active = 0;

            restoringSession = true;
            try
            {
                for (int i = 0; i < session.Tabs.Count; i++)
                    await AddTabAsync(session.Tabs[i], i == active);
            }
            finally
            {
                restoringSession = false;
            }

            if (tabs.Count > 0)
            {
                if (active >= tabs.Count) active = tabs.Count - 1;
                Activate(tabs[active]);
                tabs[active].View.FocusPrompt();
            }
            SaveSession();
        }

        async void AddTab()
        {
            try { await AddTabAsync(null, true); }
            catch (Exception ex) { MessageBox.Show("Could not open a new tab: " + ex.Message, "LLM Choir"); }
        }

        async Task<TabItem> AddTabAsync(SavedTab saved, bool activate)
        {
            if (env == null) return null;
            counter++;

            ComparisonView view = new ComparisonView { Visible = false };
            content.Controls.Add(view);
            if (saved != null) view.HasSavedTitle = saved.HasSavedTitle;

            TabItem ti = new TabItem();
            ti.View = view;
            string titleText = saved != null && !string.IsNullOrEmpty(saved.Title) ? saved.Title : "Compare " + counter;

            Panel chip = new Panel { Width = Dpi.S(156), Height = Dpi.S(26), BackColor = Color.FromArgb(45, 47, 52), Margin = Dpi.S(new Padding(2, 0, 0, 0)) };
            Label title = new Label
            {
                Text = titleText,
                ForeColor = Color.Gainsboro,
                AutoSize = false,
                AutoEllipsis = true, // trims long titles with "…" to fit
                Width = Dpi.S(122),
                Height = Dpi.S(26),
                TextAlign = ContentAlignment.MiddleLeft,
                Location = Dpi.S(new Point(8, 0)),
                Font = new Font("Segoe UI", 9f)
            };
            Button close = new Button
            {
                Text = "×",
                Width = Dpi.S(24),
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 10f)
            };
            close.FlatAppearance.BorderSize = 0;
            chip.Controls.Add(title);
            chip.Controls.Add(close);

            title.Click += delegate { Activate(ti); };
            chip.Click += delegate { Activate(ti); };
            close.Click += delegate { CloseTab(ti); };

            // Middle-click anywhere on the tab closes it (like a browser).
            MouseEventHandler middleClose = delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Middle) CloseTab(ti);
            };
            chip.MouseDown += middleClose;
            title.MouseDown += middleClose;
            close.MouseDown += middleClose;

            ti.Chip = chip;
            ti.Title = title;
            view.OnFirstSend = delegate (string msg) { SetTabTitle(ti, msg); };
            view.OnNewTabRequested = delegate { AddTab(); };
            view.OnCloseTabRequested = delegate { CloseTab(ti); };
            tabs.Add(ti);

            tabStrip.Controls.Add(chip);
            tabStrip.Controls.SetChildIndex(addBtn, tabStrip.Controls.Count - 1); // keep + last

            if (activate) Activate(ti);
            await view.InitAsync(env, saved == null ? null : saved.Urls);
            if (activate) view.FocusPrompt();
            SaveSessionIfReady();
            return ti;
        }

        void Activate(TabItem ti)
        {
            foreach (TabItem t in tabs)
            {
                bool on = (t == ti);
                t.View.Visible = on;
                t.Chip.BackColor = on ? Color.FromArgb(70, 73, 80) : Color.FromArgb(45, 47, 52);
                t.Title.ForeColor = on ? Color.White : Color.Gainsboro;
            }
            ti.View.BringToFront();
            SaveSessionIfReady();
        }

        void CloseTab(TabItem ti)
        {
            int idx = tabs.IndexOf(ti);
            bool wasActive = ti.View.Visible;
            tabs.Remove(ti);
            tabStrip.Controls.Remove(ti.Chip);
            content.Controls.Remove(ti.View);
            try { ti.View.Dispose(); } catch { }

            if (tabs.Count == 0) { AddTab(); return; }
            if (wasActive)
            {
                int act = Math.Min(idx, tabs.Count - 1);
                Activate(tabs[act]);
            }
            SaveSessionIfReady();
        }

        // Rename a tab to its first sent message. The label auto-ellipsizes, so
        // long messages show as "first words…". Capped so we don't store huge text.
        void SetTabTitle(TabItem ti, string msg)
        {
            string t = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length == 0) return;
            if (t.Length > 80) t = t.Substring(0, 80);
            ti.Title.Text = t;
            SaveSessionIfReady();
        }

        void SaveSessionIfReady()
        {
            if (restoringSession || env == null) return;
            SaveSession();
        }

        void SaveSession()
        {
            if (tabs.Count == 0) return;

            try
            {
                Directory.CreateDirectory(DataRoot());
                List<string> lines = new List<string>();
                lines.Add(SessionHeader);
                lines.Add("active\t" + ActiveTabIndex());

                foreach (TabItem t in tabs)
                {
                    List<string> parts = new List<string>();
                    parts.Add("tab");
                    parts.Add(t.View.HasSavedTitle ? "1" : "0");
                    parts.Add(EncodeField(t.Title.Text));
                    foreach (string url in t.View.CurrentUrls())
                        parts.Add(EncodeField(url));
                    lines.Add(string.Join("\t", parts.ToArray()));
                }

                File.WriteAllLines(SessionFile(), lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        SavedSession LoadSession()
        {
            try
            {
                string path = SessionFile();
                if (!File.Exists(path)) return null;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length == 0 || lines[0] != SessionHeader) return null;

                SavedSession session = new SavedSession();
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    string[] parts = lines[i].Split('\t');
                    if (parts.Length == 0) continue;

                    if (parts[0] == "active" && parts.Length > 1)
                    {
                        int active;
                        if (int.TryParse(parts[1], out active)) session.ActiveIndex = active;
                        continue;
                    }

                    if (parts[0] == "tab" && parts.Length > 2)
                    {
                        SavedTab tab = new SavedTab();
                        tab.HasSavedTitle = parts[1] == "1";
                        tab.Title = DecodeField(parts[2]);

                        List<string> urls = new List<string>();
                        for (int j = 3; j < parts.Length; j++)
                            urls.Add(DecodeField(parts[j]));
                        tab.Urls = urls.ToArray();
                        session.Tabs.Add(tab);
                    }
                }

                return session.Tabs.Count == 0 ? null : session;
            }
            catch { return null; }
        }

        int ActiveTabIndex()
        {
            TabItem active = ActiveTab();
            int idx = active == null ? -1 : tabs.IndexOf(active);
            return idx < 0 ? 0 : idx;
        }

        static string EncodeField(string value)
        {
            if (value == null) value = "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        static string DecodeField(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
            catch { return ""; }
        }

        // Ctrl+T opens a new tab when focus is on the host UI (prompt box, tab bar).
        // When a panel has focus, the in-page script handles it instead.
        TabItem ActiveTab()
        {
            foreach (TabItem t in tabs)
                if (t.View.Visible) return t;
            return null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.T))
            {
                AddTab();
                return true;
            }
            if (keyData == (Keys.Control | Keys.W))
            {
                TabItem a = ActiveTab();
                if (a != null) CloseTab(a);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Open the "how to use" window (the ? button in the tab strip).
        void ShowGuide()
        {
            using (HelpForm f = new HelpForm())
                f.ShowDialog(this);
        }

        static string DataRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiLLMConsole");
        }

        static string SessionFile()
        {
            return Path.Combine(DataRoot(), "last-session.tsv");
        }

        // ONE shared profile for every panel in every tab, so a Google sign-in in
        // any panel is reused everywhere (single sign-on).
        static string SharedProfile()
        {
            return Path.Combine(DataRoot(), "profile");
        }

        static Mutex singleInstance;

        [DllImport("user32.dll")]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        static void Main()
        {
            bool createdNew;
            singleInstance = new Mutex(true, "LLMChoir.SingleInstance", out createdNew);
            if (!createdNew)
            {
                IntPtr h = FindWindow(null, "LLM Choir");
                if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                return;
            }

            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { LogCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { LogCrash(e.ExceptionObject as Exception); };

            CleanupOrphanedWebViews();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Dpi.Init(); // read the display scaling once, before any window is built
            Application.Run(new MainForm());
            GC.KeepAlive(singleInstance);
        }

        // Kill leftover msedgewebview2 processes that belong to THIS app's data
        // folder (from a previous crash/kill); they would lock the shared profile.
        static void CleanupOrphanedWebViews()
        {
            int killed = 0;
            try
            {
                string ours = DataRoot().ToLowerInvariant();
                using (ManagementObjectSearcher searcher =
                    new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedgewebview2.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        object cl = mo["CommandLine"];
                        if (cl != null && cl.ToString().ToLowerInvariant().Contains(ours))
                        {
                            try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); killed++; }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            if (killed > 0) Thread.Sleep(600);
        }

        static void LogCrash(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"),
                    DateTime.Now.ToString("u") + "  " + (ex == null ? "unknown" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            try { MessageBox.Show((ex == null ? "Unknown error" : ex.Message), "LLM Choir error"); }
            catch { }
        }
    }

    // Modal "how to use" window opened from the ? button in the tab strip.
    class HelpForm : Form
    {
        const string Guide =
@"LLM Choir
Ask ChatGPT, Claude, and Gemini at the same time.

SENDING
• Type in the box at the bottom and press Enter to send to all three panels.
• Shift+Enter inserts a line break instead of sending.
• ""Send to all"" does the same thing as pressing Enter.
• ""Fill only"" stages your text in each panel without sending it.

ATTACHING FILES
• ""Attach to all"" lets you pick one or more files once; they are uploaded to
  every enabled panel at the same time. Then type your prompt and Send to all.
• Paste an image (Ctrl+V) into the prompt box to attach it to every enabled
  panel at once — e.g. a screenshot or an image copied from a web page.

PANELS
• Each panel is a real, logged-in browser for that service.
• ""Send:"" checkboxes choose which LLMs receive the prompt.
• ""Show:"" checkboxes show/hide each LLM's panel (independent of Send) — visible
  panels expand to fill the space, so you can focus on one or two at a time.
• ↻ reloads a single panel.
• ""New chat (all)"" starts a fresh conversation in every panel.

TABS
• ""+"" opens another independent 3-panel comparison (shortcut: Ctrl+T).
• Click a tab to switch to it; ""×"" or middle-click closes it (Ctrl+W).
• Each tab is named after the first message you send in it.
• All tabs share one login, but each keeps its own separate chats.

LOGIN
• Sign in once per service — sessions are saved and shared across all tabs.
• Signing into Google in any panel signs you into the others.

TROUBLESHOOTING
• If a prompt or file stops landing on a site after that site redesigns its
  page, the matching selectors may need updating (see the README).";

        public HelpForm()
        {
            Text = "LLM Choir — Guide";
            AutoScaleMode = AutoScaleMode.None;
            Width = Dpi.S(660);
            Height = Dpi.S(720);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(30, 31, 34);
            ForeColor = Color.White;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            MinimumSize = Dpi.S(new Size(440, 360));

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(30, 31, 34),
                Padding = Dpi.S(new Padding(16, 14, 16, 10))
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Dpi.S(44f)));

            RichTextBox box = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(30, 31, 34),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 10.5f),
                Text = Guide
            };
            root.Controls.Add(box, 0, 0);

            FlowLayoutPanel buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Color.FromArgb(30, 31, 34),
                Margin = new Padding(0)
            };
            Button close = new Button
            {
                Text = "Close",
                Width = Dpi.S(96),
                Height = Dpi.S(30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(88, 101, 242),
                Font = new Font("Segoe UI", 9.5f),
                Margin = Dpi.S(new Padding(0, 6, 0, 0))
            };
            close.FlatAppearance.BorderSize = 0;
            close.Click += delegate { Close(); };
            buttonRow.Controls.Add(close);
            root.Controls.Add(buttonRow, 0, 1);

            Controls.Add(root);

            AcceptButton = close;
            CancelButton = close; // Esc closes the dialog
        }
    }
}
