using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AppMover.Core
{
    /// <summary>一次搬移操作的记录。撤销就靠它。</summary>
    public class JournalEntry
    {
        public string Id = "";
        public string Time = "";
        public string Original = "";
        public string Target = "";
        public string Status = "planned";     // planned / completed / failed / reverted
        public string Message = "";
        public string Method = "";
        public long Bytes;
        public long Files;
        public bool JunctionCreated;
    }

    /// <summary>
    /// 撤销日志：每行一个 JSON 对象（JSON Lines）。
    ///
    /// 存放位置默认在可执行文件同目录（便携），可用环境变量 APPMOVER_JOURNAL 覆盖。
    /// 之所以不用 %LOCALAPPDATA%：本软件是便携工具，日志跟着程序走更符合预期；
    /// 而且日志里含用户机器上的真实路径，放在程序目录更容易被用户看到与审阅。
    /// </summary>
    public static class Journal
    {
        public static string Path
        {
            get
            {
                string custom = Environment.GetEnvironmentVariable("APPMOVER_JOURNAL");
                if (!string.IsNullOrEmpty(custom)) return custom;
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "journal.jsonl");
            }
        }

        public static void Append(JournalEntry e)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            JsonLite.AppendString(sb, "id", e.Id, true);
            JsonLite.AppendString(sb, "time", e.Time, false);
            JsonLite.AppendString(sb, "original", e.Original, false);
            JsonLite.AppendString(sb, "target", e.Target, false);
            JsonLite.AppendString(sb, "status", e.Status, false);
            JsonLite.AppendString(sb, "method", e.Method, false);
            JsonLite.AppendNumber(sb, "bytes", e.Bytes, false);
            JsonLite.AppendNumber(sb, "files", e.Files, false);
            JsonLite.AppendBool(sb, "junctionCreated", e.JunctionCreated, false);
            JsonLite.AppendString(sb, "message", e.Message, false);
            sb.Append("}");
            sb.AppendLine();

            File.AppendAllText(Path, sb.ToString(), new UTF8Encoding(false));
        }

        public static List<JournalEntry> ReadAll()
        {
            List<JournalEntry> list = new List<JournalEntry>();
            if (!File.Exists(Path)) return list;

            string[] lines;
            try { lines = File.ReadAllLines(Path, Encoding.UTF8); }
            catch { return list; }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line) || line.Trim().Length == 0) continue;
                Dictionary<string, string> m = JsonLite.ParseFlatObject(line);
                if (m.Count == 0) continue;

                JournalEntry e = new JournalEntry();
                e.Id = Get(m, "id");
                e.Time = Get(m, "time");
                e.Original = Get(m, "original");
                e.Target = Get(m, "target");
                e.Status = Get(m, "status");
                e.Method = Get(m, "method");
                e.Message = Get(m, "message");
                long v;
                if (long.TryParse(Get(m, "bytes"), out v)) e.Bytes = v;
                if (long.TryParse(Get(m, "files"), out v)) e.Files = v;
                e.JunctionCreated = Get(m, "junctionCreated") == "true";
                list.Add(e);
            }
            return list;
        }

        /// <summary>更新某条记录的状态（重写整个文件，日志规模很小）。</summary>
        public static bool UpdateStatus(string id, string status, string message)
        {
            return UpdateStatus(id, status, message, null);
        }

        /// <summary>
        /// 更新记录状态，并可一并修正 junctionCreated。
        ///
        /// 为什么要这个可选参数：本方法是从磁盘**重读**记录再写回，
        /// 所以调用方在内存里改的字段（例如 Execute 里设的 entry.JunctionCreated = true）
        /// 是保存不下来的 —— 真实搬移测试后才发现日志里 "junctionCreated" 一直是 false，
        /// 联接明明建成功了。字段写不对的日志比没有这个字段更误导人。
        /// </summary>
        public static bool UpdateStatus(string id, string status, string message, bool? junctionCreated)
        {
            List<JournalEntry> all = ReadAll();
            bool found = false;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Id != id) continue;
                all[i].Status = status;
                all[i].Message = message;
                if (junctionCreated.HasValue) all[i].JunctionCreated = junctionCreated.Value;
                found = true;
            }
            if (!found) return false;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < all.Count; i++)
            {
                JournalEntry e = all[i];
                sb.Append("{");
                JsonLite.AppendString(sb, "id", e.Id, true);
                JsonLite.AppendString(sb, "time", e.Time, false);
                JsonLite.AppendString(sb, "original", e.Original, false);
                JsonLite.AppendString(sb, "target", e.Target, false);
                JsonLite.AppendString(sb, "status", e.Status, false);
                JsonLite.AppendString(sb, "method", e.Method, false);
                JsonLite.AppendNumber(sb, "bytes", e.Bytes, false);
                JsonLite.AppendNumber(sb, "files", e.Files, false);
                JsonLite.AppendBool(sb, "junctionCreated", e.JunctionCreated, false);
                JsonLite.AppendString(sb, "message", e.Message, false);
                sb.Append("}");
                sb.AppendLine();
            }
            File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        private static string Get(Dictionary<string, string> m, string k)
        {
            string v;
            return m.TryGetValue(k, out v) ? v : "";
        }

        public static string NewId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }
}
