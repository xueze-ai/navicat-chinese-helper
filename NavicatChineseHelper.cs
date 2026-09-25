// Navicat 中文助手
// 监听 Navicat 的“信息/状态”面板，把每次执行后的英文报错自动翻译成中文，悬浮显示。
// 可选接入任意 OpenAI 兼容接口（阿里云百炼 / DashScope 等），让 AI 讲清报错原因、检查方法与解决办法，
// 并按严重程度用不同颜色显示；同时统计调用次数与 token 消耗。
// 纯 .NET Framework 实现，无第三方依赖，可用系统自带 csc.exe 编译。编译方式见同目录“编译.bat”。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

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

    // ---------------- AI 配置 ----------------

    internal class AiConfig
    {
        public bool Enabled = true;
        public string BaseUrl = "";
        public string ApiKey = "";
        public string Model = "qwen-flash";
        public double PriceIn = 0.15;    // 元 / 百万 tokens
        public double PriceOut = 1.5;    // 元 / 百万 tokens
        public int MaxTokens = 1200;

        public static string FilePath(string dir)
        {
            return Path.Combine(dir, "AI" + "配置.txt");
        }

        public AiConfig Clone()
        {
            AiConfig c = new AiConfig();
            c.Enabled = Enabled;
            c.BaseUrl = BaseUrl;
            c.ApiKey = ApiKey;
            c.Model = Model;
            c.PriceIn = PriceIn;
            c.PriceOut = PriceOut;
            c.MaxTokens = MaxTokens;
            return c;
        }

        public static AiConfig Load(string path)
        {
            AiConfig c = new AiConfig();
            try
            {
                if (!File.Exists(path)) return c;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "启用") c.Enabled = (v == "1" || v == "true" || v == "是" || v == "开");
                    else if (k == "地址") c.BaseUrl = v;
                    else if (k == "密钥") c.ApiKey = v;
                    else if (k == "模型") c.Model = v;
                    else if (k == "输入单价") c.PriceIn = ParseDouble(v, c.PriceIn);
                    else if (k == "输出单价") c.PriceOut = ParseDouble(v, c.PriceOut);
                    else if (k == "最大输出") c.MaxTokens = (int)ParseDouble(v, c.MaxTokens);
                }
            }
            catch { }
            return c;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Navicat 中文助手 · AI 设置");
            sb.AppendLine("# 这个文件里有 API 密钥，只放在本机用，请不要上传到 GitHub 等公开仓库。");
            sb.AppendLine("# 地址填 OpenAI 兼容地址，到 /v1 为止，例如 https://xxx/compatible-mode/v1");
            sb.AppendLine("# 单价单位：元 / 百万 tokens；填 0 表示只统计 token、不算钱。");
            sb.AppendLine("启用=" + (Enabled ? "1" : "0"));
            sb.AppendLine("地址=" + BaseUrl);
            sb.AppendLine("密钥=" + ApiKey);
            sb.AppendLine("模型=" + Model);
            sb.AppendLine("输入单价=" + PriceIn.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("输出单价=" + PriceOut.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("最大输出=" + MaxTokens);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static double ParseDouble(string s, double def)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            if (double.TryParse(s, out d)) return d;
            return def;
        }
    }

    // ---------------- AI 结果与调用 ----------------

    internal class AiResult
    {
        public string Level = "一般";
        public string Why = "";
        public List<string> Check = new List<string>();
        public List<string> Fix = new List<string>();
        public string Tip = "";
        public string Raw = "";
        public bool Parsed;
        public bool Truncated;
    }

    internal class AiCallResult
    {
        public bool Ok;
        public string Error = "";
        public AiResult Result = new AiResult();
        public int PromptTokens;
        public int CompletionTokens;
        public string Model = "";

        public static AiCallResult Fail(string msg)
        {
            AiCallResult r = new AiCallResult();
            r.Ok = false;
            r.Error = msg;
            return r;
        }
    }

    internal static class AiClient
    {
        public const string SystemPrompt =
            "你是 MySQL 数据库助教，读者是刚开始学数据库的新手。用户会给你一条在 Navicat 里执行失败的 SQL、" +
            "MySQL 返回的英文报错，以及本地词库给出的中文大意。\n" +
            "请诊断这个报错，并且【只输出一个 JSON 对象】：不要 markdown 代码围栏，不要输出任何解释性文字。JSON 结构：\n" +
            "{\"level\":\"严重|需处理|一般|提示\",\"why\":\"为什么会报这个错\",\"check\":[\"怎么检查现场\"],\"fix\":[\"怎么解决\"],\"tip\":\"避坑提示\"}\n" +
            "要求：\n" +
            "1. level 的判断：连不上数据库、权限不足、数据损坏或可能丢数据 → 严重；要改表结构、索引、约束、外键、建表语句 → 需处理；普通数据值、字段名拼写、类型不匹配、SQL 写错 → 一般；不是错误（只是提示）→ 提示。\n" +
            "2. why 用 1~2 句说清根本原因，不要复述报错原文。\n" +
            "3. check 1~3 条，每条都要具体：要么是能直接执行的 SQL（例如 SHOW INDEX FROM `表名`;），要么是界面上点哪里看什么。\n" +
            "3.1 写检查用的 SQL 时列名要写对：SHOW INDEX 的结果里 Key_name 是索引名、Column_name 才是列名；查某列有没有索引用 SHOW INDEX FROM `表名`; 再看 Column_name。\n" +
            "4. fix 1~4 条，按顺序可执行：每条给出可以直接复制运行的 SQL 或明确的操作；表名、字段名能从用户给的 SQL 里看出来就用真实名字，不要写占位符。\n" +
            "5. tip 一句话避坑提示，没有就给空字符串。\n" +
            "6. 全部用简体中文，不要 emoji，不要用 markdown 语法；SQL 里的库名、表名、字段名用反引号包住。\n" +
            "7. 不确定的地方不要编造，说明需要用户确认即可。\n" +
            "8. 只讲与本次报错直接相关的判断，不要把可选条件说成必须（例如不要声称外键列必须唯一），拿不准就写「需要确认」。";

        public static AiCallResult Ask(AiConfig cfg, string sql, string english, int code, string zh)
        {
            return Send(cfg, SystemPrompt, BuildUserMsg(sql, english, code, zh), true);
        }

        public static AiCallResult Ping(AiConfig cfg)
        {
            return Send(cfg, "你是助手，回答要极简。", "只回复两个字母：OK", false);
        }

        private static string BuildUserMsg(string sql, string english, int code, string zh)
        {
            StringBuilder sb = new StringBuilder();
            if (sql != null && sql.Trim().Length > 0)
            {
                sb.AppendLine("出错的那条 SQL：");
                sb.AppendLine(sql.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("MySQL 英文报错（错误码 " + code + "）：");
            sb.AppendLine(english == null ? "" : english.Trim());
            sb.AppendLine();
            if (zh != null && zh.Trim().Length > 0)
            {
                sb.AppendLine("本地词库给出的中文大意：");
                sb.AppendLine(zh.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("请按系统提示的 JSON 格式分析这个报错。");
            return sb.ToString();
        }

        private static AiCallResult Send(AiConfig cfg, string sys, string user, bool parseJson)
        {
            AiCallResult r = new AiCallResult();
            r.Model = cfg.Model;
            try
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
                catch { }

                string url = (cfg.BaseUrl == null ? "" : cfg.BaseUrl.Trim()).TrimEnd('/');
                if (url.Length == 0) return AiCallResult.Fail("还没有填 API 地址（点「AI设置」填写）");
                if (cfg.ApiKey.Trim().Length == 0) return AiCallResult.Fail("还没有填 API 密钥（点「AI设置」填写）");
                if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == false) url += "/chat/completions";

                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;

                Dictionary<string, object> body = new Dictionary<string, object>();
                body["model"] = cfg.Model;
                body["temperature"] = 0.2;
                body["max_tokens"] = cfg.MaxTokens > 0 ? cfg.MaxTokens : 1200;
                List<object> msgs = new List<object>();
                Dictionary<string, object> m1 = new Dictionary<string, object>();
                m1["role"] = "system";
                m1["content"] = sys;
                msgs.Add(m1);
                Dictionary<string, object> m2 = new Dictionary<string, object>();
                m2["role"] = "user";
                m2["content"] = user;
                msgs.Add(m2);
                body["messages"] = msgs;

                byte[] data = Encoding.UTF8.GetBytes(ser.Serialize(body));

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Accept = "application/json";
                req.UserAgent = "NavicatZhHelper/1.2";
                req.Headers["Authorization"] = "Bearer " + cfg.ApiKey.Trim();
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);

                string text;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();

                return ParseResponse(ser, text, cfg, parseJson, r);
            }
            catch (WebException wex)
            {
                string detail = wex.Message;
                try
                {
                    if (wex.Response != null)
                    {
                        using (StreamReader sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            string body = sr.ReadToEnd();
                            if (body.Length > 400) body = body.Substring(0, 400);
                            detail = (int)wex.Status + " " + wex.Status + "：" + body;
                        }
                    }
                }
                catch { }
                Diag.Log("ai web error: " + detail);
                return AiCallResult.Fail(detail);
            }
            catch (Exception ex)
            {
                Diag.LogError("ai", ex);
                return AiCallResult.Fail(ex.GetType().Name + "：" + ex.Message);
            }
        }

        private static AiCallResult ParseResponse(JavaScriptSerializer ser, string text, AiConfig cfg, bool parseJson, AiCallResult r)
        {
            Dictionary<string, object> root = ser.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) return AiCallResult.Fail("接口返回的内容看不懂：" + Short(text));

            object usageObj;
            if (root.TryGetValue("usage", out usageObj))
            {
                Dictionary<string, object> u = usageObj as Dictionary<string, object>;
                if (u != null)
                {
                    r.PromptTokens = ToInt(u, "prompt_tokens");
                    r.CompletionTokens = ToInt(u, "completion_tokens");
                }
            }
            object modelObj;
            if (root.TryGetValue("model", out modelObj) && modelObj != null) r.Model = Convert.ToString(modelObj);

            string content = "";
            object choicesObj;
            if (root.TryGetValue("choices", out choicesObj))
            {
                object[] choices = choicesObj as object[];
                if (choices != null && choices.Length > 0)
                {
                    Dictionary<string, object> c0 = choices[0] as Dictionary<string, object>;
                    object msgObj;
                    if (c0 != null && c0.TryGetValue("message", out msgObj))
                    {
                        Dictionary<string, object> msg = msgObj as Dictionary<string, object>;
                        object cObj;
                        if (msg != null && msg.TryGetValue("content", out cObj) && cObj != null)
                            content = Convert.ToString(cObj);
                    }
                }
            }

            if (content.Trim().Length == 0)
                return AiCallResult.Fail("接口没有返回内容：" + Short(text));

            r.Ok = true;
            r.Result = parseJson ? ParseAnswer(content) : new AiResult();
            if (!parseJson) { r.Result.Why = content.Trim(); r.Result.Parsed = true; }
            return r;
        }

        public static AiResult ParseAnswer(string content)
        {
            AiResult a = new AiResult();
            a.Raw = content == null ? "" : content.Trim();
            string s = a.Raw;

            // 去掉可能出现的 ```json 围栏，再截出最外层的花括号
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl > 0) s = s.Substring(nl + 1);
                int fence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) s = s.Substring(0, fence);
            }
            int b = s.IndexOf('{');
            int e = s.LastIndexOf('}');
            string json = (b >= 0 && e > b) ? s.Substring(b, e - b + 1) : s;

            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (d != null)
                {
                    a.Level = NormalizeLevel(GetStr(d, "level"));
                    a.Why = GetStr(d, "why");
                    a.Check = GetList(d, "check");
                    a.Fix = GetList(d, "fix");
                    a.Tip = GetStr(d, "tip");
                    a.Parsed = (a.Why.Length > 0 || a.Fix.Count > 0 || a.Check.Count > 0);
                }
            }
            catch { a.Parsed = false; }

            // 严格解析失败（比如回答被 max_tokens 截断）时，退化成按字段抽取，尽量把有用的内容显示出来
            if (!a.Parsed)
            {
                AiResult loose = LooseParse(s);
                if (loose.Parsed)
                {
                    loose.Raw = a.Raw;
                    loose.Truncated = TriTail(s);
                    return loose;
                }
            }

            a.Truncated = TriTail(s);
            if (!a.Parsed) a.Level = NormalizeLevel("");
            return a;
        }

        // 结尾不是右花括号，说明回答很可能被长度上限截断了
        private static bool TriTail(string s)
        {
            if (s == null) return false;
            s = s.TrimEnd();
            return s.Length > 0 && s[s.Length - 1] != '}';
        }

        // 不依赖 JSON 合法性的兜底解析：直接按 "level"/"why"/"check"/"fix"/"tip" 抽字段
        private static AiResult LooseParse(string s)
        {
            AiResult a = new AiResult();
            if (s == null) return a;
            a.Level = NormalizeLevel(JsonStr(s, "level"));
            a.Why = JsonStr(s, "why");
            a.Check = JsonArr(s, "check");
            a.Fix = JsonArr(s, "fix");
            a.Tip = JsonStr(s, "tip");
            a.Parsed = (a.Why.Length > 0 || a.Check.Count > 0 || a.Fix.Count > 0);
            return a;
        }

        private static string JsonStr(string s, string key)
        {
            Match m = Regex.Match(s, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            int pos = m.Index + m.Length;
            StringBuilder sb = new StringBuilder();
            ReadJsonString(s, ref pos, sb, true);
            return sb.ToString().Trim();
        }

        private static List<string> JsonArr(string s, string key)
        {
            List<string> list = new List<string>();
            Match m = Regex.Match(s, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[", RegexOptions.IgnoreCase);
            if (!m.Success) return list;
            int pos = m.Index + m.Length;
            while (pos < s.Length)
            {
                while (pos < s.Length && (s[pos] == ' ' || s[pos] == ',' || s[pos] == '\r' || s[pos] == '\n' || s[pos] == '\t')) pos++;
                if (pos >= s.Length || s[pos] == ']') break;
                if (s[pos] != '"') { pos++; continue; }
                StringBuilder sb = new StringBuilder();
                bool closed = ReadJsonString(s, ref pos, sb, false);
                string item = sb.ToString().Trim();
                if (item.Length > 0) list.Add(item);
                if (!closed) break;
            }
            return list;
        }

        // 从 s[pos] 开始读一个 JSON 字符串；started=true 表示 pos 已在引号之后
        private static bool ReadJsonString(string s, ref int pos, StringBuilder outp, bool started)
        {
            if (!started)
            {
                if (pos >= s.Length || s[pos] != '"') return false;
                pos++;
            }
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '\\' && pos + 1 < s.Length)
                {
                    char n = s[pos + 1];
                    pos += 2;
                    if (n == 'n') outp.Append('\n');
                    else if (n == 'r') outp.Append('\r');
                    else if (n == 't') outp.Append('\t');
                    else if (n == 'u' && pos + 4 <= s.Length)
                    {
                        int cp;
                        if (int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cp))
                        {
                            outp.Append((char)cp);
                            pos += 4;
                        }
                    }
                    else outp.Append(n);
                    continue;
                }
                if (c == '"') { pos++; return true; }
                outp.Append(c);
                pos++;
            }
            return false;
        }

        private static string NormalizeLevel(string s)
        {
            if (s == null) return "一般";
            if (s.IndexOf("严重") >= 0 || s.IndexOf("危险") >= 0 || s.IndexOf("致命") >= 0) return "严重";
            if (s.IndexOf("需处理") >= 0 || s.IndexOf("警告") >= 0 || s.IndexOf("注意") >= 0) return "需处理";
            if (s.IndexOf("提示") >= 0 || s.IndexOf("信息") >= 0 || s.IndexOf("正常") >= 0) return "提示";
            if (s.IndexOf("一般") >= 0) return "一般";
            return "一般";
        }

        private static string GetStr(Dictionary<string, object> d, string k)
        {
            object v;
            if (d != null && d.TryGetValue(k, out v) && v != null) return Convert.ToString(v).Trim();
            return "";
        }

        private static List<string> GetList(Dictionary<string, object> d, string k)
        {
            List<string> list = new List<string>();
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return list;
            object[] arr = v as object[];
            if (arr != null)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) continue;
                    string s = Convert.ToString(arr[i]).Trim();
                    if (s.Length > 0) list.Add(s);
                }
                return list;
            }
            string one = Convert.ToString(v).Trim();
            if (one.Length > 0) list.Add(one);
            return list;
        }

        private static int ToInt(Dictionary<string, object> d, string k)
        {
            object v;
            int n = 0;
            if (d != null && d.TryGetValue(k, out v) && v != null)
            {
                try { n = Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                catch { n = 0; }
            }
            return n;
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 200 ? s.Substring(0, 200) + "..." : s;
        }
    }

    // ---------------- 用量统计 ----------------

    internal class AiUsage
    {
        public long Calls;
        public long Failed;
        public long CachedHits;
        public long PromptTokens;
        public long CompletionTokens;

        public long TotalTokens { get { return PromptTokens + CompletionTokens; } }

        public double Cost(double priceIn, double priceOut)
        {
            return PromptTokens * priceIn / 1000000.0 + CompletionTokens * priceOut / 1000000.0;
        }

        public void Add(AiCallResult r)
        {
            Calls++;
            if (!r.Ok) Failed++;
            PromptTokens += r.PromptTokens;
            CompletionTokens += r.CompletionTokens;
        }

        public void CopyFrom(AiUsage o)
        {
            if (o == null) return;
            Calls = o.Calls;
            Failed = o.Failed;
            CachedHits = o.CachedHits;
            PromptTokens = o.PromptTokens;
            CompletionTokens = o.CompletionTokens;
        }

        public static string FilePath(string dir)
        {
            return Path.Combine(dir, "AI" + "用量.txt");
        }

        public static AiUsage Load(string path)
        {
            AiUsage u = new AiUsage();
            try
            {
                if (!File.Exists(path)) return u;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    long v;
                    if (!long.TryParse(line.Substring(eq + 1).Trim(), out v)) continue;
                    if (k == "调用次数") u.Calls = v;
                    else if (k == "失败次数") u.Failed = v;
                    else if (k == "缓存命中") u.CachedHits = v;
                    else if (k == "输入tokens") u.PromptTokens = v;
                    else if (k == "输出tokens") u.CompletionTokens = v;
                }
            }
            catch { }
            return u;
        }

        public void Save(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Navicat 中文助手 · AI 用量累计（程序自动写入，可随时删除）");
                sb.AppendLine("调用次数=" + Calls);
                sb.AppendLine("失败次数=" + Failed);
                sb.AppendLine("缓存命中=" + CachedHits);
                sb.AppendLine("输入tokens=" + PromptTokens);
                sb.AppendLine("输出tokens=" + CompletionTokens);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }
    }

    // ---------------- 主窗口 ----------------

    internal class MainForm : Form
    {
        private readonly ErrorDict _dict = new ErrorDict();
        private Translator _tr;
        private string _dictPath;
        private string _cfgPath;
        private string _dir;

        private System.Windows.Forms.Timer _timer;
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

        // AI 相关
        private AiConfig _aiConfig;
        private readonly AiUsage _usageAll = new AiUsage();
        private readonly AiUsage _usageSession = new AiUsage();
        private readonly Dictionary<string, AiResult> _aiCache = new Dictionary<string, AiResult>();
        private readonly Queue<AiJob> _aiQueue = new Queue<AiJob>();
        private readonly object _aiGate = new object();
        private int _aiBusy;
        private int _aiSeq;
        private bool _aiHintShown;
        private int _logVersion;
        private static readonly Color AiBack = Color.FromArgb(255, 251, 238);

        private class AiJob
        {
            public string Sql = "";
            public int Code;
            public string English = "";
            public string Zh = "";
            public string Sig = "";
            public string Model = "";
            public int Seq;
            public string Marker = "";   // 占位行（单行、带序号，用于返回后按内容定位）
        }

        public MainForm(string dictPath, string cfgPath, string dir, bool demo)
        {
            _dictPath = dictPath;
            _cfgPath = cfgPath;
            _dir = dir;
            _aiConfig = AiConfig.Load(cfgPath);
            _usageAll.CopyFrom(AiUsage.Load(AiUsage.FilePath(dir)));
            _tr = new Translator(_dict);
            BuildUi();
            LoadDict();
            if (demo) RenderDemo();
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += new EventHandler(OnTick);
            _timer.Start();
        }

        // --demo：不依赖 Navicat，直接喂几条样例报错走一遍完整流程（含 AI 分析），用于演示与自检
        private void RenderDemo()
        {
            string[] lines = new string[]
            {
                "ALTER TABLE `emp` ADD CONSTRAINT `emp_ibfk_1` FOREIGN KEY (`NAME`) REFERENCES `dept` (`dname`) ON UPDATE CASCADE ON DELETE SET NULL",
                "> 时间: 0.002s",
                "> 1822 - Failed to add the foreign key constraint. Missing index for constraint 'emp_ibfk_1' in the referenced table 'dept'",
                "> 时间: 0.002s",
                "INSERT INTO `t_user` VALUES (NULL, 'admin', 123)",
                "> 1062 - Duplicate entry 'admin' for key 't_user.username'",
                "> 时间: 0.001s",
                "SELECT * FROM `t_user` WHERE `id` = 1",
                "> 1054 - Unknown column 'ids' in 'where clause'",
                "> 时间: 0.001s",
                "USE wh0524",
                "> 1049 - Unknown database 'wh0525'",
                "> 时间: 0.001s"
            };
            RenderLines(lines);
        }

        private void BuildUi()
        {
            Text = "Navicat 中文助手";
            Width = 760;
            Height = 520;
            StartPosition = FormStartPosition.Manual;
            Font = new Font("微软雅黑", 9F);
            MinimumSize = new Size(700, 340);

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
            clearBtn.Click += delegate(object s, EventArgs e) { _log.Clear(); _logVersion++; };

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

            Button aiCfgBtn = new Button();
            aiCfgBtn.Text = "AI设置";
            aiCfgBtn.Width = 76;
            aiCfgBtn.Left = 364;
            aiCfgBtn.Top = 5;
            aiCfgBtn.Click += new EventHandler(OnAiSettingsClick);

            _topChk = new CheckBox();
            _topChk.Text = "置顶";
            _topChk.Checked = true;
            _topChk.Left = 448;
            _topChk.Top = 8;
            _topChk.Width = 58;
            _topChk.CheckedChanged += delegate(object s, EventArgs e) { TopMost = _topChk.Checked; };

            Button usageBtn = new Button();
            usageBtn.Text = "用量统计";
            usageBtn.Width = 92;
            usageBtn.Dock = DockStyle.Right;
            usageBtn.Click += new EventHandler(OnUsageClick);

            top.Controls.Add(_pauseBtn);
            top.Controls.Add(clearBtn);
            top.Controls.Add(copyBtn);
            top.Controls.Add(dictBtn);
            top.Controls.Add(reloadBtn);
            top.Controls.Add(aiCfgBtn);
            top.Controls.Add(_topChk);
            top.Controls.Add(usageBtn);

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
            tip.Text = "在 Navicat 里执行 SQL，这里会自动显示中文翻译；开启 AI 后会给出原因、检查步骤和解决办法。";
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

        private void OnAiSettingsClick(object sender, EventArgs e)
        {
            AiSettingsForm f = new AiSettingsForm(_aiConfig);
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                _aiConfig = f.Config;
                _aiConfig.Save(_cfgPath);
                _aiHintShown = false;
                append("\r\n[AI 设置已保存] 模型 " + _aiConfig.Model + "，AI 分析" +
                       (_aiConfig.Enabled ? "已开启" : "已关闭") + "。\r\n\r\n", Color.FromArgb(0, 110, 160), false);
                UpdateStatus();
            }
        }

        private void OnUsageClick(object sender, EventArgs e)
        {
            StatsForm f = new StatsForm(_usageSession, _usageAll, _aiConfig, _dir);
            f.ShowDialog(this);
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            string ai;
            if (!_aiConfig.Enabled) ai = "AI 未开启";
            else if (_aiConfig.ApiKey.Trim().Length == 0) ai = "AI 待配置";
            else if (_usageSession.Calls == 0) ai = "AI 就绪（" + _aiConfig.Model + "）";
            else ai = "AI " + _usageSession.Calls + " 次 · " + _usageSession.TotalTokens + " tokens · " + Money(_usageSession.Cost(_aiConfig.PriceIn, _aiConfig.PriceOut));
            string s = "词库 " + _dict.Count + " 条   |   " + ai;
            if (_paused) s = "已暂停   |   " + s;
            _status.Text = s + "   |   " + _lastTimeText;
        }

        private static string Money(double v)
        {
            if (v <= 0) return "0 元";
            if (v < 0.01) return v.ToString("0.0000", CultureInfo.InvariantCulture) + " 元";
            return v.ToString("0.00", CultureInfo.InvariantCulture) + " 元";
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
            string sql = "";
            ParsedLine errLine = null;
            string zhText = "";
            for (int i = 0; i < block.Count; i++)
            {
                ParsedLine p = _tr.Parse(block[i]);
                if (p.Kind == LineKind.Error)
                {
                    hasError = true;
                    errLine = p;
                    List<RenderedLine> rs = _tr.TranslateError(p);
                    for (int k = 0; k < rs.Count; k++) append(rs[k].Text, rs[k].Color, rs[k].Bold);
                    DictEntry de = _dict.Find(p.Code, p.English);
                    if (de != null) zhText = Translator.ApplyTemplate(de.Zh, p.English);
                }
                else if (p.Kind == LineKind.SqlEcho)
                {
                    if (sql.Length == 0) sql = p.Raw.Trim();
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
                return;
            }

            if (errLine != null) StartAiForError(sql, errLine, zhText);
        }

        // ---------- AI 分析 ----------

        private void StartAiForError(string sql, ParsedLine p, string zh)
        {
            if (!_aiConfig.Enabled) return;
            if (_aiConfig.ApiKey.Trim().Length == 0)
            {
                if (!_aiHintShown)
                {
                    _aiHintShown = true;
                    append("    [提示] 已开启 AI 分析，但还没填 API 密钥：点上面「AI设置」填一下地址、密钥和模型。\r\n",
                           Color.FromArgb(200, 120, 0), false);
                }
                return;
            }

            string sig = p.Code + "|" + p.English + "|" + sql;
            AiResult cached;
            if (_aiCache.ContainsKey(sig))
            {
                cached = _aiCache[sig];
                _usageSession.CachedHits++;
                _usageAll.CachedHits++;
                _usageAll.Save(AiUsage.FilePath(_dir));
                AppendAiBlock(cached, "（同一条错误，直接复用上次的分析，没再花钱）", _log.TextLength);
                UpdateStatus();
                return;
            }

            AiJob job = new AiJob();
            job.Sql = sql;
            job.Code = p.Code;
            job.English = p.English;
            job.Zh = zh;
            job.Sig = sig;
            job.Model = _aiConfig.Model;
            job.Seq = ++_aiSeq;
            // 占位行做成“单行 + 带序号”，返回后按内容定位替换。
            // 不能记位置：多条请求同时返回时，先返回的那条会把后面几行的位置顶偏。
            job.Marker = "【AI 分析】正在请 " + job.Model + " 分析错误 " + p.Code + " …（#" + job.Seq + "）";
            append(job.Marker + "\r\n", Color.FromArgb(150, 150, 150), false);

            lock (_aiGate) { _aiQueue.Enqueue(job); }
            PumpAi();
        }

        private void PumpAi()
        {
            lock (_aiGate)
            {
                while (_aiBusy < 2 && _aiQueue.Count > 0)
                {
                    AiJob j = _aiQueue.Dequeue();
                    _aiBusy++;
                    ThreadPool.QueueUserWorkItem(delegate(object st) { RunAiJob((AiJob)st); }, j);
                }
            }
        }

        private void RunAiJob(AiJob j)
        {
            AiConfig cfg = _aiConfig.Clone();
            AiCallResult r;
            try { r = AiClient.Ask(cfg, j.Sql, j.English, j.Code, j.Zh); }
            catch (Exception ex) { r = AiCallResult.Fail(ex.GetType().Name + "：" + ex.Message); }

            lock (_aiGate) { _aiBusy--; }
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new MethodInvoker(delegate() { OnAiDone(j, r, cfg); }));
            }
            catch { }
            PumpAi();
        }

        private void OnAiDone(AiJob j, AiCallResult r, AiConfig cfg)
        {
            _usageSession.Add(r);
            _usageAll.Add(r);
            _usageAll.Save(AiUsage.FilePath(_dir));

            if (r.Ok) _aiCache[j.Sig] = r.Result;

            // 按内容找回自己那行占位，找到就在原位替换；找不到（例如用户清空过日志）就追加到末尾
            int pos = -1;
            try
            {
                int idx = _log.Find(j.Marker, RichTextBoxFinds.None);
                if (idx >= 0)
                {
                    _log.ReadOnly = false;   // 只读时删不掉（SelectedText="" 会被忽略），先临时解锁
                    // Find() 返回的下标与 Select() 是同一套坐标（已实测），这里再回读校验一次更保险。
                    if (idx + j.Marker.Length <= _log.TextLength)
                    {
                        _log.Select(idx, j.Marker.Length);
                        if (_log.SelectedText == j.Marker)
                        {
                            _log.Select(idx, j.Marker.Length + 1);   // 连同行尾换行一起删掉（\r\n 内部只占 1 个字符）
                            _log.SelectedText = "";
                            pos = idx;
                        }
                    }
                    _log.ReadOnly = true;
                }
            }
            catch { pos = -1; }
            if (pos < 0) pos = _log.TextLength;

            if (r.Ok)
            {
                AppendAiBlock(r.Result, "", pos);
            }
            else
            {
                int at = Wr(pos, "【AI 分析失败】" + r.Error + "\r\n", Color.FromArgb(200, 60, 60), true, Color.Empty);
                Wr(at, "    提示：检查「AI设置」里的地址、密钥和模型名；也可以先点里面的「测试连接」。\r\n\r\n",
                   Color.FromArgb(150, 150, 150), false, Color.Empty);
            }

            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            UpdateStatus();
        }

        private void AppendAiBlock(AiResult a, string note, int pos)
        {
            Color sev = SeverityColor(a.Level);
            if (pos < 0 || pos > _log.TextLength) pos = _log.TextLength;
            int at = pos;   // 用游标往下推，保证整块内容贴在同一个位置，不散到文末

            StringBuilder sb = new StringBuilder();
            sb.Append("【AI 分析 · " + a.Level + "】");
            if (a.Why.Length > 0) sb.Append(a.Why);
            sb.Append("\r\n");
            at = Wr(at, sb.ToString(), sev, true, AiBack);

            if (note.Length > 0)
                at = Wr(at, "    " + note + "\r\n", Color.FromArgb(150, 150, 150), false, AiBack);

            if (a.Check.Count > 0)
            {
                at = Wr(at, "    先这样检查：\r\n", sev, true, AiBack);
                for (int i = 0; i < a.Check.Count; i++)
                    at = Wr(at, "      " + (i + 1) + ". " + a.Check[i] + "\r\n", Color.FromArgb(60, 60, 60), false, AiBack);
            }
            if (a.Fix.Count > 0)
            {
                at = Wr(at, "    然后这样解决：\r\n", sev, true, AiBack);
                for (int i = 0; i < a.Fix.Count; i++)
                    at = Wr(at, "      " + (i + 1) + ". " + a.Fix[i] + "\r\n", Color.FromArgb(40, 40, 40), false, AiBack);
            }
            if (a.Tip.Length > 0)
                at = Wr(at, "    避坑：" + a.Tip + "\r\n", Color.FromArgb(0, 110, 160), false, AiBack);

            if (a.Truncated)
                at = Wr(at, "    （回答可能被长度上限截断；想更完整可在「AI设置」里把「最大输出 tokens」调大）\r\n",
                        Color.FromArgb(200, 120, 0), false, AiBack);

            if (!a.Parsed)
            {
                at = Wr(at, "    （AI 没有按 JSON 格式返回，下面是它的原话）\r\n", Color.FromArgb(150, 150, 150), false, AiBack);
                string[] raw = (a.Raw == null ? "" : a.Raw).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < raw.Length; i++)
                    if (raw[i].Trim().Length > 0)
                        at = Wr(at, "    " + raw[i].Trim() + "\r\n", Color.FromArgb(60, 60, 60), false, AiBack);
            }

            Wr(at, "\r\n", Color.FromArgb(120, 120, 120), false, Color.Empty);
        }

        // 在 at 处插入一段带格式的文本，返回插入后的下一个位置
        private int Wr(int at, string text, Color color, bool bold, Color back)
        {
            WriteAt(at, text, color, bold, back);
            // RichTextBox 内部把 \r\n 存成 1 个字符，所以按实际存储长度推进，否则每行会多推一格
            return at + text.Replace("\r\n", "\n").Length;
        }

        private static Color SeverityColor(string level)
        {
            if (level == null) return Color.FromArgb(170, 130, 0);
            if (level.IndexOf("严重") >= 0) return Color.FromArgb(200, 30, 30);      // 红
            if (level.IndexOf("需处理") >= 0) return Color.FromArgb(220, 110, 0);    // 橙
            if (level.IndexOf("提示") >= 0) return Color.FromArgb(0, 140, 70);       // 绿
            return Color.FromArgb(170, 130, 0);                                       // 一般 → 黄
        }

        // ---------- 富文本写入 ----------

        private void append(string text, Color color, bool bold)
        {
            WriteAt(_log.TextLength, text, color, bold, Color.Empty);
        }

        // 日志框设了只读：只读时能往里插字，但删不掉（SelectedText="" 会被忽略），
        // 所以替换占位行这类“先删后写”的操作必须临时解除只读，做完再恢复。
        private void LogEdit(Action body)
        {
            bool ro = _log.ReadOnly;
            if (ro) _log.ReadOnly = false;
            try { body(); }
            finally { if (ro) _log.ReadOnly = true; }
        }

        private void WriteAt(int pos, string text, Color color, bool bold, Color back)
        {
            if (pos < 0) pos = 0;
            if (pos > _log.TextLength) pos = _log.TextLength;
            _log.Select(pos, 0);
            if (color != Color.Empty) _log.SelectionColor = color;
            _log.SelectionFont = bold ? _fontBold : _fontNormal;
            _log.SelectionBackColor = (back == Color.Empty) ? _log.BackColor : back;
            _log.SelectedText = text;
            _log.SelectionBackColor = _log.BackColor;
            _log.SelectionColor = _log.ForeColor;
            _log.SelectionFont = _fontNormal;
        }
    }

    // ---------------- AI 设置窗口 ----------------

    internal class AiSettingsForm : Form
    {
        public AiConfig Config;

        private CheckBox _enabled;
        private TextBox _url;
        private TextBox _key;
        private TextBox _model;
        private TextBox _pin;
        private TextBox _pout;
        private TextBox _maxTok;
        private CheckBox _showKey;
        private Label _msg;

        public AiSettingsForm(AiConfig cfg)
        {
            Config = cfg.Clone();

            Text = "AI 设置";
            Width = 620;
            Height = 400;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("微软雅黑", 9F);

            int y = 14;
            _enabled = new CheckBox();
            _enabled.Text = "启用 AI 分析（每条报错调用一次接口，会产生费用；同一条报错只分析一次）";
            _enabled.Checked = Config.Enabled;
            _enabled.Left = 14;
            _enabled.Top = y;
            _enabled.Width = 560;
            Controls.Add(_enabled);   // 之前漏了这行，复选框没显示出来
            y += 32;

            AddLabel("API 兼容地址（到 /v1 为止）", 14, y);
            _url = AddText(14, y + 20, 570);
            _url.Text = Config.BaseUrl;
            y += 52;

            AddLabel("API 密钥", 14, y);
            _key = AddText(14, y + 20, 470);
            _key.Text = Config.ApiKey;
            _key.UseSystemPasswordChar = true;
            _showKey = new CheckBox();
            _showKey.Text = "显示";
            _showKey.Left = 492;
            _showKey.Top = y + 22;
            _showKey.Width = 60;
            _showKey.CheckedChanged += delegate(object s, EventArgs e) { _key.UseSystemPasswordChar = !_showKey.Checked; };
            Controls.Add(_showKey);
            y += 52;

            AddLabel("模型名", 14, y);
            _model = AddText(14, y + 20, 200);
            _model.Text = Config.Model;
            AddLabel("输入单价", 232, y);
            _pin = AddText(232, y + 20, 110);
            _pin.Text = Config.PriceIn.ToString(CultureInfo.InvariantCulture);
            AddLabel("输出单价", 358, y);
            _pout = AddText(358, y + 20, 110);
            _pout.Text = Config.PriceOut.ToString(CultureInfo.InvariantCulture);
            AddLabel("最大输出", 484, y);
            _maxTok = AddText(484, y + 20, 100);
            _maxTok.Text = Config.MaxTokens.ToString();
            y += 56;

            Label notes = new Label();
            notes.Left = 14;
            notes.Top = y;
            notes.Width = 576;
            notes.Height = 84;
            notes.ForeColor = Color.FromArgb(120, 120, 120);
            notes.Text = "单价单位：元 / 百万 tokens；最大输出单位：tokens（回答说一半被截断时把它调大）。\r\n" +
                         "地址示例：https://llm-xxxx.cn-beijing.maas.aliyuncs.com/compatible-mode/v1（OpenAI 兼容地址）\r\n" +
                         "配置保存在程序同目录的 AI配置.txt，里面有密钥，请不要上传到 GitHub 等公开仓库。\r\n" +
                         "单价填 0 表示只统计 token、不算钱。";
            Controls.Add(notes);
            y += 86;

            _msg = new Label();
            _msg.Left = 14;
            _msg.Top = y;
            _msg.Width = 470;
            _msg.Height = 40;
            _msg.Text = "";
            Controls.Add(_msg);

            Button test = new Button();
            test.Text = "测试连接";
            test.Left = 14;
            test.Top = y + 44;
            test.Width = 90;
            test.Click += new EventHandler(OnTestClick);

            Button save = new Button();
            save.Text = "保存";
            save.Left = 396;
            save.Top = y + 44;
            save.Width = 88;
            save.Click += new EventHandler(OnSaveClick);
            save.DialogResult = DialogResult.None;

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.Left = 494;
            cancel.Top = y + 44;
            cancel.Width = 88;
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(test);
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            ClientSize = new Size(600, y + 92);
        }

        private void AddLabel(string text, int left, int top)
        {
            Label l = new Label();
            l.Text = text;
            l.Left = left;
            l.Top = top;
            l.AutoSize = true;   // 按文字实际宽度排，避免标签互相盖住
            l.ForeColor = Color.FromArgb(70, 70, 70);
            Controls.Add(l);
        }

        private TextBox AddText(int left, int top, int width)
        {
            TextBox t = new TextBox();
            t.Left = left;
            t.Top = top;
            t.Width = width;
            Controls.Add(t);
            return t;
        }

        private AiConfig Collect()
        {
            AiConfig c = new AiConfig();
            c.Enabled = _enabled.Checked;
            c.BaseUrl = _url.Text.Trim();
            c.ApiKey = _key.Text.Trim();
            c.Model = _model.Text.Trim();
            c.PriceIn = AiConfig.ParseDouble(_pin.Text, 0);
            c.PriceOut = AiConfig.ParseDouble(_pout.Text, 0);
            c.MaxTokens = (int)AiConfig.ParseDouble(_maxTok.Text, 1200);
            if (c.Model.Length == 0) c.Model = "qwen-flash";
            if (c.MaxTokens <= 0) c.MaxTokens = 1200;
            return c;
        }

        private void OnSaveClick(object sender, EventArgs e)
        {
            Config = Collect();
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OnTestClick(object sender, EventArgs e)
        {
            AiConfig c = Collect();
            _msg.ForeColor = Color.FromArgb(0, 110, 160);
            _msg.Text = "正在测试 ...";
            _msg.Refresh();
            AiCallResult r = AiClient.Ping(c);
            if (r.Ok)
            {
                double cost = r.PromptTokens * c.PriceIn / 1000000.0 + r.CompletionTokens * c.PriceOut / 1000000.0;
                _msg.ForeColor = Color.FromArgb(0, 140, 70);
                _msg.Text = "连接成功：模型 " + r.Model + " 回复「" + r.Result.Why + "」\r\n" +
                            "本次用量：输入 " + r.PromptTokens + " tokens，输出 " + r.CompletionTokens + " tokens，约 " +
                            cost.ToString("0.000000", CultureInfo.InvariantCulture) + " 元。";
            }
            else
            {
                _msg.ForeColor = Color.FromArgb(200, 60, 60);
                _msg.Text = "连接失败：" + r.Error;
            }
        }
    }

    // ---------------- 用量统计窗口 ----------------

    internal class StatsForm : Form
    {
        public StatsForm(AiUsage session, AiUsage all, AiConfig cfg, string dir)
        {
            Text = "AI 用量统计";
            Width = 560;
            Height = 540;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("微软雅黑", 9F);

            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.Left = 12;
            t.Top = 12;
            t.Width = 522;
            t.Height = 420;
            t.BackColor = Color.FromArgb(252, 252, 252);
            t.Font = new Font("Consolas", 9.5F);
            t.Text = Build(session, all, cfg, dir);
            t.TabStop = false;   // 避免一进来整段文字被选中（蓝底）
            Shown += delegate(object s2, EventArgs e2) { t.SelectionStart = 0; t.SelectionLength = 0; };
            Controls.Add(t);

            Button clear = new Button();
            clear.Text = "清空累计";
            clear.Left = 12;
            clear.Top = 442;
            clear.Width = 90;
            clear.Click += delegate(object s, EventArgs e)
            {
                if (MessageBox.Show(this, "把累计统计清零？（已花的钱不会退回，只是这张表归零）", "确认",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                all.Calls = 0; all.Failed = 0; all.CachedHits = 0; all.PromptTokens = 0; all.CompletionTokens = 0;
                all.Save(AiUsage.FilePath(dir));
                t.Text = Build(session, all, cfg, dir);
            };

            Button open = new Button();
            open.Text = "打开目录";
            open.Left = 110;
            open.Top = 442;
            open.Width = 90;
            open.Click += delegate(object s, EventArgs e) { try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { } };

            Button close = new Button();
            close.Text = "关闭";
            close.Left = 444;
            close.Top = 442;
            close.Width = 90;
            close.Click += delegate(object s, EventArgs e) { Close(); };

            Controls.Add(clear);
            Controls.Add(open);
            Controls.Add(close);
        }

        private static string Build(AiUsage session, AiUsage all, AiConfig cfg, string dir)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("本次会话（程序启动之后）");
            sb.AppendLine(Row("调用次数", session.Calls + "（成功 " + (session.Calls - session.Failed) + "，失败 " + session.Failed + "）"));
            sb.AppendLine(Row("输入 tokens", session.PromptTokens.ToString("N0")));
            sb.AppendLine(Row("输出 tokens", session.CompletionTokens.ToString("N0")));
            sb.AppendLine(Row("合计 tokens", session.TotalTokens.ToString("N0")));
            sb.AppendLine(Row("同错复用", session.CachedHits + " 次（没花钱）"));
            sb.AppendLine(Row("估算花费", Money(session.Cost(cfg.PriceIn, cfg.PriceOut))));
            sb.AppendLine();
            sb.AppendLine("累计（保存在 AI用量.txt）");
            sb.AppendLine(Row("调用次数", all.Calls + "（成功 " + (all.Calls - all.Failed) + "，失败 " + all.Failed + "）"));
            sb.AppendLine(Row("输入 tokens", all.PromptTokens.ToString("N0")));
            sb.AppendLine(Row("输出 tokens", all.CompletionTokens.ToString("N0")));
            sb.AppendLine(Row("合计 tokens", all.TotalTokens.ToString("N0")));
            sb.AppendLine(Row("同错复用", all.CachedHits + " 次（没花钱）"));
            sb.AppendLine(Row("估算花费", Money(all.Cost(cfg.PriceIn, cfg.PriceOut))));
            sb.AppendLine();
            sb.AppendLine("当前设置");
            sb.AppendLine(Row("模型", cfg.Model));
            sb.AppendLine(Row("单价", "输入 " + cfg.PriceIn + " 元/百万，输出 " + cfg.PriceOut + " 元/百万"));
            sb.AppendLine(Row("接口", cfg.BaseUrl));
            sb.AppendLine();
            sb.AppendLine("说明：费用是按上面的单价和真实 token 数算出来的估算值，最终以服务商账单为准；");
            sb.AppendLine("     单价随便改，改完这里的花费会跟着变。同一条报错重复出现时不会重复调用接口。");
            return sb.ToString();
        }

        private static string Row(string k, string v)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("  ").Append(k);
            int pad = 18 - DisplayLen(k);
            for (int i = 0; i < pad; i++) sb.Append(' ');
            sb.Append(": ").Append(v);
            return sb.ToString();
        }

        private static int DisplayLen(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) n += (s[i] > 127) ? 2 : 1;
            return n / 2 + (n % 2);
        }

        private static string Money(double v)
        {
            return v.ToString("0.000000", CultureInfo.InvariantCulture) + " 元";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string dictPath = Path.Combine(dir, "词库.txt");
            string cfgPath = AiConfig.FilePath(dir);
            bool demo = false;
            Diag.Init(Path.Combine(dir, "诊断日志.txt"));

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--debug") Diag.Enabled = true;
                if (args[i] == "--demo") demo = true;
            }
            Diag.Log("start: dir=" + dir + " bit=" + (IntPtr.Size * 8) + " os64=" + Environment.Is64BitOperatingSystem);

            if (args.Length > 0 && args[0] == "--selftest")
            {
                RunSelfTest(dictPath, Path.Combine(dir, "自检结果.txt"));
                return;
            }
            if (args.Length > 0 && args[0] == "--aitest")
            {
                RunAiSelfTest(cfgPath, dir, Path.Combine(dir, "AI自检.txt"));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(dictPath, cfgPath, dir, demo));
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

        // AI 自检：拿两条真实报错走一遍 AI 分析，结果写入文件（含 token 用量与费用）
        private static void RunAiSelfTest(string cfgPath, string dir, string outPath)
        {
            AiConfig cfg = AiConfig.Load(cfgPath);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Navicat 中文助手 · AI 自检结果");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("接口地址：" + cfg.BaseUrl);
            sb.AppendLine("模型：" + cfg.Model);
            sb.AppendLine("AI 开关：" + (cfg.Enabled ? "开" : "关") + "，密钥：" + (cfg.ApiKey.Trim().Length > 0 ? "已填写" : "未填写"));
            sb.AppendLine("单价：输入 " + cfg.PriceIn + " 元/百万 tokens，输出 " + cfg.PriceOut + " 元/百万 tokens");
            sb.AppendLine(new string('=', 70));

            string[] samples = new string[]
            {
                "ALTER TABLE `emp` ADD CONSTRAINT `emp_ibfk_1` FOREIGN KEY (`NAME`) REFERENCES `dept` (`dname`) ON UPDATE CASCADE ON DELETE SET NULL|1822|Failed to add the foreign key constraint. Missing index for constraint 'emp_ibfk_1' in the referenced table 'dept'|添加外键失败：父表 'dept' 中缺少约束 'emp_ibfk_1' 所需的索引",
                "INSERT INTO `t_user` VALUES (NULL, 'admin', 123)|1062|Duplicate entry 'admin' for key 't_user.username'|值 'admin' 重复，违反唯一约束"
            };

            AiUsage total = new AiUsage();
            int okCount = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                string[] f = samples[i].Split('|');
                sb.AppendLine();
                sb.AppendLine(">>> 样例 " + (i + 1) + "：SQL = " + f[0]);
                sb.AppendLine("    英文原文 = " + f[2]);
                AiCallResult r = AiClient.Ask(cfg, f[0], f[2], int.Parse(f[1]), f[3]);
                total.Add(r);
                if (r.Ok) okCount++;
                if (r.Ok)
                {
                    sb.AppendLine("    模型 = " + r.Model + "，token：输入 " + r.PromptTokens + " / 输出 " + r.CompletionTokens);
                    sb.AppendLine("    严重程度 = " + r.Result.Level);
                    sb.AppendLine("    原因 = " + r.Result.Why);
                    for (int k = 0; k < r.Result.Check.Count; k++) sb.AppendLine("    检查" + (k + 1) + " = " + r.Result.Check[k]);
                    for (int k = 0; k < r.Result.Fix.Count; k++) sb.AppendLine("    解决" + (k + 1) + " = " + r.Result.Fix[k]);
                    sb.AppendLine("    避坑 = " + r.Result.Tip);
                    sb.AppendLine("    JSON 解析 = " + (r.Result.Parsed ? "成功" : "失败（下面是原话）"));
                    if (!r.Result.Parsed)
                    {
                        string[] raw = r.Result.Raw.Replace("\r\n", "\n").Split('\n');
                        for (int k = 0; k < raw.Length; k++) sb.AppendLine("      " + raw[k]);
                    }
                }
                else
                {
                    sb.AppendLine("    调用失败 = " + r.Error);
                }
            }

            sb.AppendLine();
            sb.AppendLine(new string('=', 70));
            sb.AppendLine("成功 " + okCount + "/" + samples.Length + " 条。");
            sb.AppendLine("合计 token：输入 " + total.PromptTokens + "，输出 " + total.CompletionTokens +
                          "，合计 " + total.TotalTokens);
            sb.AppendLine("估算花费：" + total.Cost(cfg.PriceIn, cfg.PriceOut).ToString("0.000000", CultureInfo.InvariantCulture) + " 元");
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
    }
}