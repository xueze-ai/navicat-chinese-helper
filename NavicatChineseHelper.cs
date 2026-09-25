// Navicat 中文助手
// 监听 Navicat 的“信息/状态”面板，把每次执行后的英文报错自动翻译成中文，悬浮显示。
// 纯 .NET Framework 实现，无第三方依赖，可用系统自带 csc.exe 编译。
// 编译方式见同目录“编译.bat”。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NavicatZhHelper
{
    // 排查用日志：只有加 --debug 参数启动时才会写文件
    internal static class Diag
    {
        public static bool Enabled;
        private static readonly object Gate = new object();
        private static string _path = "";

        public static void Init(string p) { _path = p; }

        public static void Log(string msg)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024) File.Delete(_path);
                    File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff  ") + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void LogError(string where, Exception ex)
        {
            Log("!! " + where + " :: " + ex.GetType().Name + " - " + ex.Message);
        }
    }

    // ---------------- 词库 ----------------

    internal class DictEntry
    {
        public int Code;
        public string Zh = "";
        public string Hint = "";
    }

    internal class ErrorDict
    {
        private readonly Dictionary<int, List<DictEntry>> _map = new Dictionary<int, List<DictEntry>>();
        private int _count;

        public int Count { get { return _count; } }

        public void Load(string path)
        {
            _map.Clear();
            _count = 0;
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 2) continue;
                int code;
                if (!int.TryParse(parts[0].Trim(), out code)) continue;
                DictEntry e = new DictEntry();
                e.Code = code;
                e.Zh = parts[1].Trim();
                if (parts.Length > 2) e.Hint = parts[2].Trim();
                if (e.Zh.Length == 0) continue;
                List<DictEntry> list;
                if (!_map.TryGetValue(code, out list)) { list = new List<DictEntry>(); _map[code] = list; }
                list.Add(e);
                _count++;
            }
        }

        // 同一个错误码可能对应多种英文措辞（例如 1062 有两种写法），挑参数能完整套用的那一条
        public DictEntry Find(int code, string english)
        {
            List<DictEntry> list;
            if (!_map.TryGetValue(code, out list) || list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            for (int i = 0; i < list.Count; i++)
            {
                if (Translator.CanFill(list[i].Zh, english)) return list[i];
            }
            return list[0];
        }
    }

    // ---------------- 翻译引擎 ----------------

    internal enum LineKind
    {
        SqlEcho,      // 被执行的 SQL 原文
        Error,        // > 1146 - Table ... doesn't exist
        Timing,       // > 时间: 0.001s
        Affected,     // > 受影响的行: 1
        Info,         // 其它以 > 开头的提示
        Blank
    }

    internal class ParsedLine
    {
        public LineKind Kind;
        public int Code;
        public string English = "";
        public string Raw = "";
        public string Value = "";
    }

    internal class RenderedLine
    {
        public Color Color = Color.Black;
        public string Text = "";
        public bool Bold;
        public bool Italic;
    }

    internal class Translator
    {
        private readonly ErrorDict _dict;
        private static readonly Regex ErrRe = new Regex(@"^>\s*(?:ERROR\s+)?(\d{3,5})\s*[-:]\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex TimeRe = new Regex(@"^>\s*(?:时间|Time|耗时)\s*[:：]\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RowRe = new Regex(@"^>\s*(?:受影响的行|影响行数|Affected\s+rows?|Rows?\s+affected|Affected)\s*[:：]\s*(\d+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex QuotedRe = new Regex(@"'([^']*)'", RegexOptions.Compiled);
        private static readonly Regex NumberRe = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex HolderRe = new Regex(@"\{([^}]*)\}", RegexOptions.Compiled);

        public Translator(ErrorDict dict) { _dict = dict; }

        public static List<string> QuotedArgs(string english)
        {
            List<string> quoted = new List<string>();
            foreach (Match m in QuotedRe.Matches(english)) quoted.Add(m.Groups[1].Value);
            return quoted;
        }

        public static List<string> NumericArgs(string english)
        {
            List<string> nums = new List<string>();
            foreach (Match m in NumberRe.Matches(english)) nums.Add(m.Groups[0].Value);
            return nums;
        }

        // 模板里的占位符是否都能在当前这条英文消息里取到值
        public static bool CanFill(string tpl, string english)
        {
            if (tpl.IndexOf('{') < 0) return true;
            int q = QuotedArgs(english).Count;
            int n = NumericArgs(english).Count;
            foreach (Match m in HolderRe.Matches(tpl))
            {
                string token = m.Groups[1].Value.Trim();
                int idx;
                if (token.Length > 1 && (token[0] == 'N' || token[0] == 'n'))
                {
                    if (!int.TryParse(token.Substring(1), out idx)) return false;
                    if (idx < 1 || idx > n) return false;
                }
                else
                {
                    if (!int.TryParse(token, out idx)) return false;
                    if (idx < 1 || idx > q) return false;
                }
            }
            return true;
        }

        public static bool IsResultLine(string line)
        {
            return line.TrimStart().StartsWith(">");
        }

        public ParsedLine Parse(string line)
        {
            ParsedLine p = new ParsedLine();
            p.Raw = line;
            string t = line.Trim();
            if (t.Length == 0) { p.Kind = LineKind.Blank; return p; }

            Match m = ErrRe.Match(t);
            if (m.Success)
            {
                int code;
                if (int.TryParse(m.Groups[1].Value, out code))
                {
                    p.Kind = LineKind.Error;
                    p.Code = code;
                    p.English = m.Groups[2].Value.Trim();
                    return p;
                }
            }
            m = TimeRe.Match(t);
            if (m.Success) { p.Kind = LineKind.Timing; p.Value = m.Groups[1].Value.Trim(); return p; }
            m = RowRe.Match(t);
            if (m.Success) { p.Kind = LineKind.Affected; p.Value = m.Groups[1].Value.Trim(); return p; }
            if (t.StartsWith(">")) { p.Kind = LineKind.Info; p.Value = t.Substring(1).Trim(); return p; }
            p.Kind = LineKind.SqlEcho;
            return p;
        }

        // 解析一条错误行，返回可直接显示的若干行
        public List<RenderedLine> TranslateError(ParsedLine p)
        {
            List<RenderedLine> outp = new List<RenderedLine>();
            DictEntry e = _dict.Find(p.Code, p.English);

            if (e == null)
            {
                RenderedLine r0 = new RenderedLine();
                r0.Color = Color.FromArgb(200, 40, 40);
                r0.Bold = true;
                r0.Text = "【错误 " + p.Code + "】(词库暂无此码，下面是原文)\r\n";
                outp.Add(r0);
                RenderedLine r1 = new RenderedLine();
                r1.Color = Color.FromArgb(90, 90, 90);
                r1.Text = "    " + p.English + "\r\n";
                outp.Add(r1);
                return outp;
            }

            string zh = ApplyTemplate(e.Zh, p.English);

            RenderedLine head = new RenderedLine();
            head.Color = Color.FromArgb(200, 30, 30);
            head.Bold = true;
            head.Text = "【错误 " + p.Code + "】" + zh + "\r\n";
            outp.Add(head);

            if (e.Hint.Length > 0)
            {
                RenderedLine hint = new RenderedLine();
                hint.Color = Color.FromArgb(0, 110, 160);
                hint.Text = "    建议：" + e.Hint + "\r\n";
                outp.Add(hint);
            }

            RenderedLine src = new RenderedLine();
            src.Color = Color.FromArgb(150, 150, 150);
            src.Italic = true;
            src.Text = "    原文：" + p.Code + " - " + p.English + "\r\n";
            outp.Add(src);
            return outp;
        }

        // 把词库模板里的 {1} {2} / {N1} {N2} 换成英文消息里的实际参数
        public static string ApplyTemplate(string tpl, string english)
        {
            if (tpl.IndexOf('{') < 0) return tpl;

            List<string> quoted = QuotedArgs(english);
            List<string> nums = NumericArgs(english);

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < tpl.Length; i++)
            {
                char c = tpl[i];
                if (c == '{')
                {
                    int close = tpl.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string token = tpl.Substring(i + 1, close - i - 1).Trim();
                        string rep = null;
                        if (token.Length > 1 && (token[0] == 'N' || token[0] == 'n'))
                        {
                            int idx;
                            if (int.TryParse(token.Substring(1), out idx) && idx >= 1 && idx <= nums.Count)
                                rep = nums[idx - 1];
                        }
                        else
                        {
                            int idx;
                            if (int.TryParse(token, out idx) && idx >= 1 && idx <= quoted.Count)
                                rep = quoted[idx - 1];
                        }
                        if (rep != null) { sb.Append(rep); i = close; continue; }
                        // 参数缺失：标注出来，避免给出错误的中文
                        sb.Append("？");
                        i = close;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    // ---------------- 主窗口 ----------------

    internal class MainForm : Form
    {
        private readonly ErrorDict _dict = new ErrorDict();
        private Translator _tr;
        private string _dictPath;

        private Timer _timer;
        private readonly Dictionary<string, string> _lastText = new Dictionary<string, string>();

        private RichTextBox _log;
        private Font _fontNormal;
        private Font _fontBold;
        private Label _status;
        private Button _pauseBtn;
        private CheckBox _topChk;
        private NotifyIcon _tray;
        private bool _paused;
        private bool _realExit;
        private string _lastTimeText = "";

        public MainForm(string dictPath)
        {
            _dictPath = dictPath;
            _tr = new Translator(_dict);
            BuildUi();
            LoadDict();
            _timer = new Timer();
            _timer.Interval = 500;
            _timer.Tick += new EventHandler(OnTick);
            _timer.Start();
        }

        private void BuildUi()
        {
            Text = "Navicat 中文助手";
            Width = 620;
            Height = 520;
            StartPosition = FormStartPosition.Manual;
            Font = new Font("微软雅黑", 9F);
            MinimumSize = new Size(460, 320);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 38;
            top.Padding = new Padding(6, 6, 6, 0);

            _pauseBtn = new Button();
            _pauseBtn.Text = "暂停";
            _pauseBtn.Width = 64;
            _pauseBtn.Left = 6;
            _pauseBtn.Top = 5;
            _pauseBtn.Click += new EventHandler(OnPauseClick);

            Button clearBtn = new Button();
            clearBtn.Text = "清空";
            clearBtn.Width = 56;
            clearBtn.Left = 76;
            clearBtn.Top = 5;
            clearBtn.Click += delegate(object s, EventArgs e) { _log.Clear(); };

            Button copyBtn = new Button();
            copyBtn.Text = "复制全部";
            copyBtn.Width = 76;
            copyBtn.Left = 138;
            copyBtn.Top = 5;
            copyBtn.Click += new EventHandler(OnCopyClick);

            Button dictBtn = new Button();
            dictBtn.Text = "词库";
            dictBtn.Width = 56;
            dictBtn.Left = 220;
            dictBtn.Top = 5;
            dictBtn.Click += new EventHandler(OnOpenDictClick);

            Button reloadBtn = new Button();
            reloadBtn.Text = "载入词库";
            reloadBtn.Width = 76;
            reloadBtn.Left = 282;
            reloadBtn.Top = 5;
            reloadBtn.Click += delegate(object s, EventArgs e) { LoadDict(); };

            _topChk = new CheckBox();
            _topChk.Text = "置顶";
            _topChk.Checked = true;
            _topChk.Left = 368;
            _topChk.Top = 8;
            _topChk.Width = 58;
            _topChk.CheckedChanged += delegate(object s, EventArgs e) { TopMost = _topChk.Checked; };

            top.Controls.Add(_pauseBtn);
            top.Controls.Add(clearBtn);
            top.Controls.Add(copyBtn);
            top.Controls.Add(dictBtn);
            top.Controls.Add(reloadBtn);
            top.Controls.Add(_topChk);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Color.FromArgb(252, 252, 252);
            _fontNormal = new Font("微软雅黑", 9.5F);
            _fontBold = new Font("微软雅黑", 9.5F, FontStyle.Bold);
            _log.Font = _fontNormal;
            _log.WordWrap = true;
            _log.BorderStyle = BorderStyle.None;
            _log.HideSelection = false;

            Label tip = new Label();
            tip.Dock = DockStyle.Top;
            tip.Height = 22;
            tip.ForeColor = Color.FromArgb(130, 130, 130);
            tip.Padding = new Padding(8, 0, 0, 0);
            tip.Text = "在 Navicat 里执行 SQL，这里会自动显示中文翻译。";
            tip.Font = new Font("微软雅黑", 8.5F);

            Panel spacer = new Panel();
            spacer.Dock = DockStyle.Top;
            spacer.Height = 6;

            _status = new Label();
            _status.Dock = DockStyle.Bottom;
            _status.Height = 24;
            _status.TextAlign = ContentAlignment.MiddleLeft;
            _status.Padding = new Padding(8, 0, 0, 0);
            _status.ForeColor = Color.FromArgb(90, 90, 90);
            _status.BorderStyle = BorderStyle.FixedSingle;
            _status.Text = "正在查找 Navicat ...";

            Controls.Add(_log);
            Controls.Add(_status);
            Controls.Add(tip);
            Controls.Add(spacer);
            Controls.Add(top);

            TopMost = true;

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "Navicat 中文助手";
            _tray.Visible = true;
            ContextMenu trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("显示窗口", new EventHandler(delegate(object s, EventArgs e)
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            }));
            trayMenu.MenuItems.Add("退出", new EventHandler(delegate(object s, EventArgs e)
            {
                _realExit = true;
                Close();
            }));
            _tray.ContextMenu = trayMenu;
            _tray.DoubleClick += new EventHandler(delegate(object s, EventArgs e)
            {
                Show();
                WindowState = FormWindowState.Normal;
                Activate();
            });

            FormClosing += new FormClosingEventHandler(OnFormClosing);

            // 默认贴到屏幕右下角
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Left = wa.Right - Width - 24;
            Top = wa.Bottom - Height - 24;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_realExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(1500, "Navicat 中文助手", "已最小化到托盘，双击图标可以重新打开。", ToolTipIcon.Info);
                return;
            }
            _timer.Stop();
            _tray.Visible = false;
        }

        private void LoadDict()
        {
            _dict.Load(_dictPath);
            UpdateStatus();
            if (_dict.Count == 0)
            {
                append("找不到或无法解析词库文件：\r\n" + _dictPath + "\r\n请确认这个文件与本程序在同一个目录。\r\n\r\n", Color.FromArgb(200, 30, 30), true);
            }
        }

        private void OnPauseClick(object sender, EventArgs e)
        {
            _paused = !_paused;
            _pauseBtn.Text = _paused ? "继续" : "暂停";
            UpdateStatus();
        }

        private void OnCopyClick(object sender, EventArgs e)
        {
            if (_log.Text.Length > 0)
            {
                try { Clipboard.SetText(_log.Text); } catch { }
            }
        }

        private void OnOpenDictClick(object sender, EventArgs e)
        {
            try { Process.Start("notepad.exe", "\"" + _dictPath + "\""); } catch { }
        }

        private void UpdateStatus()
        {
            string s = "词库 " + _dict.Count + " 条";
            if (_paused) s = "已暂停 | " + s;
            _status.Text = s + "   |   " + _lastTimeText;
        }

        // ---------- 轮询 ----------

        private void OnTick(object sender, EventArgs e)
        {
            if (_paused) return;
            try { ScanNavicat(); }
            catch (Exception ex) { Diag.LogError("OnTick", ex); }
        }

        private void ScanNavicat()
        {
            Process[] procs = Process.GetProcessesByName("navicat");
            if (procs.Length == 0)
            {
                _lastTimeText = "未找到 navicat.exe，等待启动 ...";
                UpdateStatus();
                return;
            }

            int panels = 0;
            for (int i = 0; i < procs.Length; i++)
            {
                int pid = procs[i].Id;
                List<IntPtr> wins = TopWindows.Of(pid);
                Diag.Log("proc[" + pid + "] topWindows=" + wins.Count);

                for (int w = 0; w < wins.Count; w++)
                {
                    List<MemoPanel> memos = MemoPanel.FindAll(wins[w]);
                    Diag.Log("  win=" + wins[w].ToString() + " panels=" + memos.Count);

                    for (int k = 0; k < memos.Count; k++)
                    {
                        MemoPanel m = memos[k];
                        panels++;
                        Diag.Log("    panel hwnd=" + m.Handle.ToString() + " cls=" + m.ClassName + " len=" + m.Text.Length);

                        string key = pid.ToString() + ":" + m.Handle.ToString();
                        string prev;
                        bool had = _lastText.TryGetValue(key, out prev);
                        _lastText[key] = m.Text;
                        if (had && prev == m.Text) continue;

                        string[] fresh = DiffLines(had ? prev : null, m.Text);
                        Diag.Log("    fresh=" + fresh.Length);
                        if (fresh.Length > 0) RenderLines(fresh);
                    }
                }
            }

            _lastTimeText = panels > 0
                ? ("已连接 Navicat（" + panels + " 个结果面板），监听中")
                : "找到 Navicat 进程，等待执行结果 ...";
            UpdateStatus();
        }

        // 枚举某个进程的全部顶层窗口（Navicat 可能同时开着多个查询窗口）
        internal static class TopWindows
        {
            private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
            private const uint GW_OWNER = 4;

            [DllImport("user32.dll")]
            private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

            [DllImport("user32.dll")]
            private static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

            public static List<IntPtr> Of(int pid)
            {
                List<IntPtr> list = new List<IntPtr>();
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    try
                    {
                        uint wp;
                        GetWindowThreadProcessId(h, out wp);
                        if (wp != (uint)pid) return true;
                        if (!IsWindowVisible(h)) return true;
                        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                        list.Add(h);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                return list;
            }
        }

        // 用 Win32 子窗口枚举直接读 Navicat 的“信息”结果面板（Delphi 的 TMemo）。
        // 不走 UI Automation：实测同一台机器上 UIA 会因提供程序差异漏掉这个面板，
        // 而 EnumChildWindows + WM_GETTEXT 能稳定读到全文。
        internal sealed class MemoPanel
        {
            public IntPtr Handle;
            public string ClassName = "";
            public string Text = "";

            private const uint WM_GETTEXT = 0x000D;
            private const uint SMTO_ABORTIFHUNG = 0x0002;
            private const int BufferChars = 65536;

            private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern bool EnumChildWindows(IntPtr hWnd, EnumProc cb, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr hWnd, StringBuilder buf, int max);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowText(IntPtr hWnd, StringBuilder buf, int max);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeout")]
            private static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

            public static List<MemoPanel> FindAll(IntPtr main)
            {
                List<MemoPanel> found = new List<MemoPanel>();
                EnumChildWindows(main, delegate(IntPtr h, IntPtr l)
                {
                    try
                    {
                        string cls = ClassOf(h);
                        if (!IsMemoClass(cls)) return true;
                        string text = ReadText(h);
                        if (text == null || text.Trim().Length == 0) return true;
                        if (!HasResultLine(text)) return true;
                        MemoPanel p = new MemoPanel();
                        p.Handle = h;
                        p.ClassName = cls;
                        p.Text = text;
                        found.Add(p);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                return found;
            }

            private static string ClassOf(IntPtr h)
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, sb.Capacity);
                return sb.ToString();
            }

            private static string ReadText(IntPtr h)
            {
                StringBuilder sb = new StringBuilder(BufferChars);
                if (GetWindowText(h, sb, sb.Capacity) > 0) return sb.ToString();

                StringBuilder sb2 = new StringBuilder(BufferChars);
                IntPtr res;
                SendMessageTimeoutText(h, WM_GETTEXT, (IntPtr)sb2.Capacity, sb2, SMTO_ABORTIFHUNG, 1000, out res);
                return sb2.ToString();
            }

            // Delphi 的备注框；排除 SQL 编辑器（TEEAutoCompleteMemo / libeeScintilla）等
            private static bool IsMemoClass(string cls)
            {
                if (string.IsNullOrEmpty(cls)) return false;
                if (cls.IndexOf("AutoComplete", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (cls.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (cls.IndexOf("TEE", StringComparison.OrdinalIgnoreCase) == 0) return false;
                return cls.StartsWith("TMemo")
                    || cls.StartsWith("TRichEdit")
                    || cls.Equals("RichEdit20W")
                    || cls.Equals("RichEdit20A")
                    || cls.Equals("RichEdit50W");
            }

            // 结果面板的特征：至少有一行以 “>” 开头（> 报错 / > 时间 / > 受影响的行）
            private static bool HasResultLine(string text)
            {
                string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith(">")) return true;
                }
                return false;
            }
        }

        // 只取上一次之后新增的内容；面板被清空/重置时返回全部
        private static string[] DiffLines(string prev, string current)
        {
            string[] cur = SplitLines(current);
            if (prev == null) return cur;
            if (current == prev) return new string[0];
            if (current.StartsWith(prev)) return SplitLines(current.Substring(prev.Length));
            return cur;
        }

        private static string[] SplitLines(string s)
        {
            List<string> list = new List<string>();
            if (s == null) return list.ToArray();
            string[] raw = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i].Trim().Length == 0 && (i == raw.Length - 1)) continue;
                list.Add(raw[i]);
            }
            return list.ToArray();
        }

        // ---------- 渲染 ----------

        private void RenderLines(string[] lines)
        {
            // 按“一条被执行语句 + 它的结果”分组
            List<List<string>> blocks = new List<List<string>>();
            List<string> cur = null;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;
                bool result = Translator.IsResultLine(line);
                if (!result)
                {
                    if (cur != null && cur.Count > 0) blocks.Add(cur);
                    cur = new List<string>();
                }
                if (cur == null) cur = new List<string>();
                cur.Add(line);
            }
            if (cur != null && cur.Count > 0) blocks.Add(cur);

            Diag.Log("render: lines=" + lines.Length + " blocks=" + blocks.Count);
            if (blocks.Count == 0) return;

            for (int b = 0; b < blocks.Count; b++) RenderBlock(blocks[b]);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
        }

        private void RenderBlock(List<string> block)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            append("\r\n── " + stamp + " " + new string('─', 30) + "\r\n", Color.FromArgb(190, 190, 190), false);

            bool hasError = false;
            for (int i = 0; i < block.Count; i++)
            {
                ParsedLine p = _tr.Parse(block[i]);
                if (p.Kind == LineKind.Error)
                {
                    hasError = true;
                    List<RenderedLine> rs = _tr.TranslateError(p);
                    for (int k = 0; k < rs.Count; k++) append(rs[k].Text, rs[k].Color, rs[k].Bold);
                }
                else if (p.Kind == LineKind.SqlEcho)
                {
                    append("执行：" + p.Raw.Trim() + "\r\n", Color.FromArgb(70, 70, 70), false);
                }
                else if (p.Kind == LineKind.Timing)
                {
                    append("耗时：" + p.Value + "\r\n", Color.FromArgb(140, 140, 140), false);
                }
                else if (p.Kind == LineKind.Affected)
                {
                    append("受影响的行：" + p.Value + "\r\n", Color.FromArgb(0, 140, 70), false);
                }
                else if (p.Kind == LineKind.Info)
                {
                    append(p.Value + "\r\n", Color.FromArgb(120, 120, 120), false);
                }
            }

            if (!hasError)
            {
                append("【执行成功】没有报错\r\n", Color.FromArgb(0, 140, 70), true);
            }
        }

        private void append(string text, Color color, bool bold)
        {
            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = color;
            _log.SelectionFont = bold ? _fontBold : _fontNormal;
            _log.AppendText(text);
            _log.SelectionColor = _log.ForeColor;
            _log.SelectionFont = _fontNormal;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string dictPath = Path.Combine(dir, "词库.txt");
            Diag.Init(Path.Combine(dir, "诊断日志.txt"));

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--debug") Diag.Enabled = true;
            }
            Diag.Log("start: dir=" + dir + " bit=" + (IntPtr.Size * 8) + " os64=" + Environment.Is64BitOperatingSystem);

            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest(dictPath, Path.Combine(dir, "自检结果.txt"));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(dictPath));
        }

        // 自检：把 Navicat 真实格式的样例喂进解析/翻译流程，结果写入文件，便于核对
        private static void RunSelfTest(string dictPath, string outPath)
        {
            ErrorDict dict = new ErrorDict();
            dict.Load(dictPath);
            Translator tr = new Translator(dict);

            string[] samples = new string[]
            {
                "INSERT INTO t_user VALUES(NULL, NULL,123156)",
                "> 1146 - Table 'wh0524.t_user' doesn't exist",
                "> 时间: 0.001s",
                "INSERT INTO t_user VALUES(NULL, 'admin', 123456);",
                "> 1062 - Duplicate entry 'admin' for key 't_user.username'",
                "> 时间: 0.002s",
                "INSERT INTO t_user VALUES(NULL, NULL, 123456);",
                "> 1048 - Column 'username' cannot be null",
                "> 时间: 0.001s",
                "INSERT INTO t_user VALUES(NULL, 'admin', '123456');",
                "> 1366 - Incorrect integer value: 'admin' for column 'username' at row 1",
                "> 时间: 0.001s",
                "INSERT INTO t_student VALUES(1, 'zhangsan', 99);",
                "> 1452 - Cannot add or update a child row: a foreign key constraint fails (`wh0524`.`t_student`, CONSTRAINT `fk_1` FOREIGN KEY (`c_id`) REFERENCES `t_course` (`id`))",
                "> 时间: 0.003s",
                "CREATE TABLE t_emp(id INT PRIMARY KEY);",
                "> 1050 - Table 't_emp' already exists",
                "> 时间: 0.001s",
                "SELECT * FROM t_user WHERE id = 1",
                "> 1054 - Unknown column 'ids' in 'where clause'",
                "> 时间: 0.001s",
                "SELECT * FROM t_user",
                "> 受影响的行: 3",
                "> 时间: 0.002s",
                "CREATE TABLE t_x(id INT AUTO_INCREMENT, name VARCHAR(10));",
                "> 1075 - Incorrect table definition; there can be only one auto column and it must be defined as a key",
                "> 时间: 0.001s"
            };

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Navicat 中文助手 · 自检结果");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("词库条目：" + dict.Count);
            sb.AppendLine(new string('=', 70));

            int errorCount = 0;
            int knownCount = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                string line = samples[i];
                ParsedLine p = tr.Parse(line);
                if (p.Kind == LineKind.Error)
                {
                    errorCount++;
                    if (dict.Find(p.Code, p.English) != null) knownCount++;
                    sb.AppendLine();
                    sb.AppendLine(">>> 输入: " + line);
                    List<RenderedLine> rs = tr.TranslateError(p);
                    for (int k = 0; k < rs.Count; k++)
                        sb.AppendLine("    " + rs[k].Text.TrimEnd());
                }
                else if (p.Kind == LineKind.Timing)
                {
                    sb.AppendLine(">>> 输入: " + line + "     => 耗时：" + p.Value);
                }
                else if (p.Kind == LineKind.Affected)
                {
                    sb.AppendLine(">>> 输入: " + line + "     => 受影响的行：" + p.Value);
                }
                else if (p.Kind == LineKind.SqlEcho)
                {
                    sb.AppendLine();
                    sb.AppendLine(">>> 执行：" + line);
                }
            }

            sb.AppendLine();
            sb.AppendLine(new string('=', 70));
            sb.AppendLine("错误行共 " + errorCount + " 条，其中词库命中 " + knownCount + " 条。");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
