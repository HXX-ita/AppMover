using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AppMover.Core
{
    /// <summary>占用情况：哪些进程正打开该目录里的文件。</summary>
    public class OccupancyReport
    {
        public List<string> Processes = new List<string>();
        public List<string> LockedFiles = new List<string>();
        public int FilesScanned;
        public bool ScanTruncated;

        public bool HasOccupancy { get { return Processes.Count > 0; } }
    }

    /// <summary>
    /// 用 Windows 的 Restart Manager 查询「谁正占着这些文件」。
    ///
    /// 为什么用它而不是枚举句柄：Restart Manager 是 Windows 安装程序的标准做法，
    /// 它知道哪些进程（含服务）持有文件，并且能给出可读的进程名。
    /// 自己枚举系统句柄既需要管理员权限，也容易漏。
    /// </summary>
    public static class Occupancy
    {
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle,
            uint nFiles, string[] rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[] rgApplications,
            uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle,
            out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        /// <summary>
        /// 检查目录占用。为控制耗时，优先登记可执行模块（exe/dll/sys），
        /// 再补足其他文件，最多登记 cap 个。
        /// </summary>
        public static OccupancyReport Check(string directory, int cap)
        {
            OccupancyReport rep = new OccupancyReport();
            List<string> files = CollectCandidates(directory, cap, rep);
            if (files.Count == 0) return rep;

            uint handle;
            string key = Guid.NewGuid().ToString("N");
            int rv = RmStartSession(out handle, 0, key);
            if (rv != 0) return rep;

            try
            {
                string[] names = files.ToArray();
                rv = RmRegisterResources(handle, (uint)names.Length, names, 0, null, 0, null);
                if (rv != 0) return rep;

                uint needed = 0, count = 0, reasons = 0;
                rv = RmGetList(handle, out needed, ref count, null, ref reasons);
                if (rv == 234 && needed > 0)         // ERROR_MORE_DATA
                {
                    RM_PROCESS_INFO[] infos = new RM_PROCESS_INFO[needed];
                    count = needed;
                    rv = RmGetList(handle, out needed, ref count, infos, ref reasons);
                    if (rv == 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            string app = infos[i].strAppName;
                            string svc = infos[i].strServiceShortName;
                            int pid = infos[i].Process.dwProcessId;
                            string line = app;
                            if (!string.IsNullOrEmpty(svc)) line += "（服务: " + svc + "）";
                            line += " [PID " + pid + "]";
                            if (!rep.Processes.Contains(line)) rep.Processes.Add(line);
                        }
                    }
                }
            }
            finally
            {
                RmEndSession(handle);
            }
            return rep;
        }

        private static List<string> CollectCandidates(string directory, int cap, OccupancyReport rep)
        {
            List<string> modules = new List<string>();
            List<string> others = new List<string>();
            Stack<string> stack = new Stack<string>();
            stack.Push(directory);

            while (stack.Count > 0)
            {
                if (modules.Count + others.Count >= cap) { rep.ScanTruncated = true; break; }
                string cur = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(PathUtil.IoPath(cur)); }
                catch { continue; }

                for (int i = 0; i < entries.Length; i++)
                {
                    string e = entries[i];
                    try
                    {
                        FileAttributes a = File.GetAttributes(e);
                        if ((a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint) continue;
                        if ((a & FileAttributes.Directory) == FileAttributes.Directory) { stack.Push(e); continue; }
                        rep.FilesScanned++;
                        string ext = Path.GetExtension(e).ToLowerInvariant();
                        if (ext == ".exe" || ext == ".dll" || ext == ".sys" || ext == ".ocx" || ext == ".node")
                            modules.Add(e);
                        else
                            others.Add(e);
                    }
                    catch { }
                }
            }

            List<string> result = new List<string>();
            for (int i = 0; i < modules.Count && result.Count < cap; i++) result.Add(modules[i]);
            for (int i = 0; i < others.Count && result.Count < cap; i++) result.Add(others[i]);
            return result;
        }

        /// <summary>关闭指定 PID 的进程（用于「先关掉占用它的软件」）。返回是否成功发出关闭请求。</summary>
        public static bool TryCloseProcess(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                return p.CloseMainWindow();
            }
            catch { return false; }
        }
    }
}
