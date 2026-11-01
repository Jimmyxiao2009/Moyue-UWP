using System;
using System.Collections.Generic;
using System.Text;

namespace Moyue
{
    /// <summary>
    /// 轻量运行日志：内存环形缓冲 + 可选落盘钩子。
    /// 纯 C#（不依赖 UWP API），后端库与 UWP 工程同时编入；
    /// 落盘由 UWP 侧的 LogFile 通过 Sink 完成。
    /// 排查"获取失败"时重点看：jm 行的域名尝试、img 行的图片失败原因、codec 行的解码分支。
    /// </summary>
    public static class Log
    {
        const int MaxLines = 1500;

        static readonly object Gate = new object();
        static readonly List<string> Lines = new List<string>();

        /// <summary>每写一行回调一次（UWP 侧接落盘；可为 null）。</summary>
        public static Action<string> Sink { get; set; }

        public static int Count
        {
            get
            {
                lock (Gate)
                    return Lines.Count;
            }
        }

        public static void I(string tag, string message) => Write("I", tag, message);

        public static void W(string tag, string message) => Write("W", tag, message);

        public static void E(string tag, string message) => Write("E", tag, message);

        /// <summary>带异常链的警告（与 E 相同格式，级别为 W）。</summary>
        public static void W(string tag, string message, Exception ex) =>
            E(tag, message, ex);

        /// <summary>记录异常时连内部异常链一起展开，避免只看到一句"失败"。</summary>
        public static void E(string tag, string message, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(message);
            for (var e = ex; e != null; e = e.InnerException)
                sb.Append(" <- ").Append(e.GetType().Name).Append(": ").Append(FirstLine(e.Message));
            Write("E", tag, sb.ToString());
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            int cut = -1;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n' || s[i] == '\r')
                {
                    cut = i;
                    break;
                }
            var line = cut >= 0 ? s.Substring(0, cut) : s;
            return line.Length > 200 ? line.Substring(0, 200) + "…" : line;
        }

        static void Write(string level, string tag, string message)
        {
            var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var line = stamp + " " + level + " [" + (tag ?? "") + "] " + (message ?? "");
            Action<string> sink;
            lock (Gate)
            {
                Lines.Add(line);
                if (Lines.Count > MaxLines)
                    Lines.RemoveRange(0, Lines.Count - MaxLines);
                sink = Sink;
            }
            if (sink == null)
                return;
            try
            {
                sink(line);
            }
            catch (Exception)
            {
                // 日志自身失败不能影响主流程。
            }
        }

        /// <summary>启动时把磁盘已有行灌回缓冲（不触发 Sink，避免回写）。</summary>
        public static void Replay(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            lock (Gate)
            {
                Lines.Add(line);
                if (Lines.Count > MaxLines)
                    Lines.RemoveRange(0, Lines.Count - MaxLines);
            }
        }

        /// <summary>导出全部日志文本（旧行在前）。</summary>
        public static string Dump()
        {
            lock (Gate)
                return string.Join(Environment.NewLine, Lines.ToArray());
        }

        public static void Clear()
        {
            lock (Gate)
                Lines.Clear();
        }
    }
}
