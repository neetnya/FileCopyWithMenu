using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FileCopyWithMenu.Core
{
    public enum EntryKind
    {
        Directory,
        File,
        Link
    }

    /// <summary>路径与文件系统相关的通用工具。</summary>
    public static class PathUtil
    {
        /// <summary>超过该长度时用 \\?\ 前缀（即使清单已声明 longPathAware，双保险）。</summary>
        public const int LongPathThreshold = 240;

        private const string LongPrefix = @"\\?\";
        private const string LongUncPrefix = @"\\?\UNC\";

        /// <summary>把路径转成可突破 260 字符限制的形式。</summary>
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (path.StartsWith(LongPrefix, StringComparison.Ordinal))
                return path;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                full = path;
            }

            if (full.Length < LongPathThreshold)
                return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return LongUncPrefix + full.Substring(2);
            return LongPrefix + full;
        }

        /// <summary>去掉 \\?\ 前缀，便于显示。</summary>
        public static string Short(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (path.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(LongUncPrefix.Length);
            if (path.StartsWith(LongPrefix, StringComparison.Ordinal))
                return path.Substring(LongPrefix.Length);
            return path;
        }

        private static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p))
                return string.Empty;
            p = Short(p);
            try
            {
                p = Path.GetFullPath(p);
            }
            catch
            {
                // 保持原样
            }
            return p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>child 是否位于 parent 之下（含自身）。</summary>
        public static bool IsWithin(string child, string parent)
        {
            string c = Norm(child), p = Norm(parent);
            if (c.Length == 0 || p.Length == 0)
                return false;
            if (c == p)
                return true;
            return c.StartsWith(p + "\\", StringComparison.Ordinal);
        }

        /// <summary>取相对路径；跨盘符时退化为文件名。</summary>
        public static string RelativeTo(string fullPath, string root)
        {
            try
            {
                string rel = Path.GetRelativePath(root, fullPath);
                if (rel == "." || rel == ".." || rel.StartsWith("..\\") || rel.StartsWith("../") || Path.IsPathRooted(rel))
                    return Path.GetFileName(fullPath);
                return rel;
            }
            catch
            {
                return Path.GetFileName(fullPath);
            }
        }

        /// <summary>去掉被其它选中目录包含的冗余项（先深后浅排序）。</summary>
        public static List<string> NormalizeSources(IEnumerable<string> sources)
        {
            var list = new List<string>(sources);
            list.Sort((a, b) =>
            {
                int d = Depth(a).CompareTo(Depth(b));
                return d != 0 ? d : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            var result = new List<string>();
            foreach (string s in list)
            {
                bool covered = false;
                foreach (string kept in result)
                {
                    if (IsWithin(s, kept) && Norm(s) != Norm(kept))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered && !result.Exists(x => Norm(x) == Norm(s)))
                    result.Add(s);
            }
            return result;
        }

        private static int Depth(string p)
        {
            return Short(p).Split('\\').Length;
        }

        /// <summary>判断条目类型：目录 / 文件 / 链接（符号链接、联接点）。</summary>
        public static EntryKind GetKind(FileSystemInfo info)
        {
            try
            {
                FileAttributes attr = info.Attributes;
                if ((attr & FileAttributes.ReparsePoint) != 0)
                    return EntryKind.Link;
                return (attr & FileAttributes.Directory) != 0 ? EntryKind.Directory : EntryKind.File;
            }
            catch
            {
                return EntryKind.File;
            }
        }

        /// <summary>链接是否指向目录（这类链接一律不递归，避免成环）。</summary>
        public static bool IsLinkToDirectory(string path)
        {
            try
            {
                return Directory.Exists(Long(path));
            }
            catch
            {
                return false;
            }
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }
            return i == 0 ? bytes + " B" : v.ToString("0.#") + " " + units[i];
        }
    }

    /// <summary>自然排序：让 "第2章" 排在 "第10章" 之前。</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string x, string y)
        {
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                bool dx = char.IsDigit(x[i]), dy = char.IsDigit(y[j]);
                if (dx && dy)
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string nx = x.Substring(si, i - si).TrimStart('0');
                    string ny = y.Substring(sj, j - sj).TrimStart('0');
                    if (nx.Length != ny.Length)
                        return nx.Length - ny.Length;
                    int cmp = string.CompareOrdinal(nx, ny);
                    if (cmp != 0)
                        return cmp;
                }
                else
                {
                    int cmp = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (cmp != 0)
                        return cmp;
                    i++;
                    j++;
                }
            }
            return (x.Length - i) - (y.Length - j);
        }
    }
}
