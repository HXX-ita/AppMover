using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AppMover.Core
{
    /// <summary>
    /// 递归删除目录树，**遇到重解析点只删链接本身、绝不递归进去**。
    ///
    /// 为什么不能直接用 Directory.Delete(path, true)：
    /// 我们对「递归删除是否会跟随联接」不能想当然 —— 一旦跟随，
    /// 删掉 C:\Program Files\Foo 这个联接就会把 D:\Foo 里的真实数据一起删掉。
    /// 这是本软件最不可接受的失败模式，所以这里自己实现，语义明确。
    /// </summary>
    public static class SafeDelete
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool RemoveDirectoryW(string path);

        /// <summary>删除整个目录树。返回是否全部成功；失败项会记录在 failures 中。</summary>
        public static bool DeleteTree(string path, List<string> failures)
        {
            if (failures == null) failures = new List<string>();
            try
            {
                FileAttributes attr = File.GetAttributes(PathUtil.IoPath(path));
                if ((attr & FileAttributes.Directory) != FileAttributes.Directory)
                {
                    File.Delete(PathUtil.IoPath(path));
                    return true;
                }
                if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                {
                    // 联接/符号链接：只摘链接
                    return RemoveDirectoryW(PathUtil.IoPath(path)) || !Directory.Exists(path);
                }
            }
            catch (Exception ex)
            {
                failures.Add(path + " : " + ex.Message);
                return false;
            }

            bool ok = true;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(PathUtil.IoPath(path)); }
            catch (Exception ex) { failures.Add(path + " : " + ex.Message); return false; }

            for (int i = 0; i < entries.Length; i++)
            {
                string e = entries[i];
                try
                {
                    FileAttributes ea = File.GetAttributes(PathUtil.IoPath(e));
                    if ((ea & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        if ((ea & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            if (!RemoveDirectoryW(PathUtil.IoPath(e)) && Directory.Exists(e))
                                { failures.Add(e + " : 无法删除联接"); ok = false; }
                        }
                        else
                        {
                            if (!DeleteTree(e, failures)) ok = false;
                        }
                    }
                    else
                    {
                        try
                        {
                            File.SetAttributes(PathUtil.IoPath(e), FileAttributes.Normal);
                        }
                        catch { }
                        File.Delete(PathUtil.IoPath(e));
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(e + " : " + ex.Message);
                    ok = false;
                }
            }

            if (ok)
            {
                try { Directory.Delete(PathUtil.IoPath(path), false); }
                catch (Exception ex) { failures.Add(path + " : " + ex.Message); ok = false; }
            }
            return ok;
        }
    }
}
