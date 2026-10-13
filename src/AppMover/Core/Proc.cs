using System;
using System.Diagnostics;
using System.Text;

namespace AppMover.Core
{
    /// <summary>启动外部进程并收集输出。用于调用 robocopy 这类系统工具。</summary>
    public static class Proc
    {
        /// <summary>
        /// 运行命令并等待结束。timeoutMs 为 0 表示不限制。
        /// 返回进程退出码；启动失败返回 -1。
        /// </summary>
        public static int Run(string fileName, string arguments, Action<string> log, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // Windows 自带工具（robocopy 等）按 OEM 代码页输出。早先这里写死 UTF-8，
            // 导致中文环境下用户看到的是乱码而不是真正的错误原因。
            try
            {
                Encoding oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            catch { }

            try
            {
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    StringBuilder tail = new StringBuilder();
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (tail) { tail.AppendLine(e.Data); if (tail.Length > 8000) tail.Remove(0, tail.Length - 8000); }
                        if (log != null) log("    " + e.Data);
                    };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        if (log != null) log("    [stderr] " + e.Data);
                    };

                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    if (timeoutMs > 0)
                    {
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch { }
                            if (log != null) log("    命令超时被终止: " + fileName);
                            return 124;
                        }
                    }
                    else
                    {
                        p.WaitForExit();
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("    无法启动 " + fileName + " : " + ex.Message);
                return -1;
            }
        }

        /// <summary>运行命令并返回完整标准输出（用于查询类命令）。</summary>
        public static string Capture(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (timeoutMs > 0) p.WaitForExit(timeoutMs); else p.WaitForExit();
                    return outp;
                }
            }
            catch { return null; }
        }
    }
}
