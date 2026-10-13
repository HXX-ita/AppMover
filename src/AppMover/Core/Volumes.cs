using System;
using System.Collections.Generic;
using System.IO;

namespace AppMover.Core
{
    /// <summary>一个卷（分区）的可用性与资格信息。搬移目标的资格判定都基于这里。</summary>
    public class VolumeInfo
    {
        public string Letter;          // "C:"
        public string Label;
        public string FileSystem;      // NTFS / exFAT / ...
        public string DriveType;       // Fixed / Removable / Network
        public long TotalBytes;
        public long FreeBytes;
        public bool IsSystem;

        public bool IsNtfs
        {
            get { return string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsFixed
        {
            get { return string.Equals(DriveType, "Fixed", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>能否作为搬移目标：本机固定磁盘 + NTFS。联接只在 NTFS 上成立。</summary>
        public bool CanHostJunction
        {
            get { return IsNtfs && IsFixed; }
        }

        public string WhyNotHost()
        {
            if (!IsFixed) return "不是本机固定磁盘（可移动/网络磁盘不适合作为目标）";
            if (!IsNtfs) return "文件系统不是 NTFS，无法建立目录联接";
            return null;
        }
    }

    public static class Volumes
    {
        /// <summary>枚举所有带盘符的卷。</summary>
        public static List<VolumeInfo> Enumerate()
        {
            List<VolumeInfo> list = new List<VolumeInfo>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return list; }

            string systemRoot = PathUtil.GetVolumeRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            foreach (DriveInfo d in drives)
            {
                try
                {
                    if (!d.IsReady) continue;
                    VolumeInfo v = new VolumeInfo();
                    v.Letter = d.Name.TrimEnd('\\');
                    v.Label = d.VolumeLabel;
                    v.DriveType = d.DriveType.ToString();
                    v.TotalBytes = d.TotalSize;
                    v.FreeBytes = d.AvailableFreeSpace;
                    try { v.FileSystem = d.DriveFormat; } catch { v.FileSystem = "?"; }
                    v.IsSystem = string.Equals(v.Letter, systemRoot, StringComparison.OrdinalIgnoreCase);
                    list.Add(v);
                }
                catch { }
            }
            return list;
        }

        /// <summary>挑选默认目标盘：非系统盘、固定、NTFS、剩余空间最大的那个。</summary>
        public static VolumeInfo PickDefaultTarget(List<VolumeInfo> all)
        {
            VolumeInfo best = null;
            foreach (VolumeInfo v in all)
            {
                if (v.IsSystem) continue;
                if (!v.CanHostJunction) continue;
                if (best == null || v.FreeBytes > best.FreeBytes) best = v;
            }
            return best;
        }
    }
}
