using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FileCopyWithMenu.Core
{
    public enum ConflictPolicy
    {
        /// <summary>覆盖已存在的同名文件</summary>
        Overwrite,
        /// <summary>跳过已存在的同名文件</summary>
        Skip,
        /// <summary>自动重命名为 "名称 (1).ext"</summary>
        Rename
    }

    public sealed class CopyOptions
    {
        public ConflictPolicy Policy = ConflictPolicy.Overwrite;
        public bool IncludeEmptyDirectories = true;
        public bool KeepTimestamps = true;

        public string PolicyText
        {
            get
            {
                switch (Policy)
                {
                    case ConflictPolicy.Skip: return "跳过";
                    case ConflictPolicy.Rename: return "自动重命名";
                    default: return "覆盖";
                }
            }
        }
    }

    public enum CopyPhase
    {
        Scanning,
        CreatingDirectories,
        CopyingFiles,
        Done
    }

    public sealed class CopyProgressInfo
    {
        public CopyPhase Phase;
        public int Done;
        public int Total;
        public long Bytes;
        public int Failed;
        public string Message;

        public double Percent
        {
            get { return Total > 0 ? Math.Min(100.0, Done * 100.0 / Total) : 0; }
        }
    }

    public sealed class CopyResult
    {
        public int Copied;
        public int Skipped;
        public int Failed;
        public int DirsCreated;
        public int SkippedLinks;
        public long Bytes;
        public int Total;
        public bool Cancelled;
        public TimeSpan Elapsed;
        public string TargetRoot;
        public List<string> Errors = new List<string>();
        public List<string> FirstTargets = new List<string>();
    }

    public enum CopyMessageKind
    {
        Log,
        Progress,
        Done
    }

    public sealed class CopyMessage
    {
        public CopyMessageKind Kind;
        public string Text;
        public bool IsError;
        public CopyProgressInfo Progress;
        public CopyResult Result;

        public static CopyMessage Log(string text, bool isError = false)
        {
            return new CopyMessage { Kind = CopyMessageKind.Log, Text = text, IsError = isError };
        }

        public static CopyMessage FromProgress(CopyProgressInfo info)
        {
            return new CopyMessage { Kind = CopyMessageKind.Progress, Progress = info };
        }

        public static CopyMessage FromResult(CopyResult result)
        {
            return new CopyMessage { Kind = CopyMessageKind.Done, Result = result };
        }
    }

    /// <summary>把左侧选中项（含目录结构）复制到右侧目录。纯后台逻辑，不依赖 UI。</summary>
    public static class CopyEngine
    {
        private const int MaxErrors = 300;

        private sealed class FileEntry
        {
            public string Source;
            public string Relative;
        }

        public static CopyResult Run(
            IList<string> sources,
            string leftRoot,
            string rightRoot,
            CopyOptions options,
            IProgress<CopyMessage> report,
            CancellationToken ct)
        {
            var result = new CopyResult { TargetRoot = rightRoot };
            var sw = Stopwatch.StartNew();

            report.Report(CopyMessage.Log("开始扫描源目录结构 …"));

            var dirs = new List<string>();
            var files = new List<FileEntry>();
            int linkCount = 0;

            foreach (string src in sources)
            {
                if (ct.IsCancellationRequested) break;

                if (Directory.Exists(PathUtil.Long(src)))
                {
                    if (PathUtil.GetKind(new DirectoryInfo(PathUtil.Long(src))) == EntryKind.Link)
                    {
                        report.Report(CopyMessage.Log(
                            "提示：选中项是目录链接/联接点，将按它指向的实际内容复制：" + src));
                    }
                    string rel = PathUtil.RelativeTo(src, leftRoot);
                    if (rel.Length > 0) dirs.Add(rel);
                    ScanDirectory(src, rel, dirs, files, ref linkCount, ct, report);
                }
                else if (File.Exists(PathUtil.Long(src)))
                {
                    string rel = PathUtil.RelativeTo(src, leftRoot);
                    if (string.IsNullOrEmpty(rel)) rel = Path.GetFileName(src);
                    files.Add(new FileEntry { Source = src, Relative = rel });
                }
                else
                {
                    report.Report(CopyMessage.Log("忽略（不存在或不可访问）：" + src, true));
                }
            }

            if (linkCount > 0)
                report.Report(CopyMessage.Log("跳过 " + linkCount + " 个目录链接/联接点（不递归，避免成环）。"));

            result.Total = files.Count;
            result.SkippedLinks = linkCount;

            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                result.Elapsed = sw.Elapsed;
                return result;
            }

            // 未勾选“复制空文件夹”时，只保留最终包含文件的目录
            if (!options.IncludeEmptyDirectories)
            {
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileEntry f in files)
                {
                    string d = Path.GetDirectoryName(f.Relative);
                    while (!string.IsNullOrEmpty(d))
                    {
                        keep.Add(d);
                        string up = Path.GetDirectoryName(d);
                        if (up == d) break;
                        d = up;
                    }
                }
                dirs.RemoveAll(d => !keep.Contains(d));
            }

            dirs.Sort((a, b) =>
            {
                int d = Depth(a).CompareTo(Depth(b));
                return d != 0 ? d : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            report.Report(CopyMessage.Log("待复制： " + files.Count + " 个文件，" + dirs.Count + " 个文件夹。"));
            report.Report(CopyMessage.FromProgress(new CopyProgressInfo
            {
                Phase = CopyPhase.CreatingDirectories,
                Done = 0,
                Total = Math.Max(files.Count, 1),
                Message = "正在创建目录结构 …"
            }));

            // ---------- 1. 建目录 ----------
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dirStamps = new List<string[]>();   // {源目录, 目标目录}，复制结束后由深到浅还原修改时间
            foreach (string rel in dirs)
            {
                if (ct.IsCancellationRequested) break;

                string dst = Path.Combine(rightRoot, ApplyRemap(rel, remap));
                string lp = PathUtil.Long(dst);

                if (Directory.Exists(lp)) continue;

                if (File.Exists(lp))
                {
                    if (options.Policy == ConflictPolicy.Skip)
                    {
                        result.Skipped++;
                        report.Report(CopyMessage.Log("目录名被同名文件占用，跳过：" + rel, true));
                        continue;
                    }
                    if (options.Policy == ConflictPolicy.Overwrite)
                    {
                        try
                        {
                            File.SetAttributes(lp, FileAttributes.Normal);
                            File.Delete(lp);
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            AddError(result, report, "删除冲突文件失败 " + dst + "：" + ex.Message);
                            continue;
                        }
                    }
                    else
                    {
                        int i = 1;
                        string cand;
                        do
                        {
                            cand = dst + " (" + i + ")";
                            i++;
                        }
                        while (File.Exists(PathUtil.Long(cand)) || Directory.Exists(PathUtil.Long(cand)));
                        remap[rel] = PathUtil.RelativeTo(cand, rightRoot);
                        report.Report(CopyMessage.Log("目录重命名：" + rel + " → " + PathUtil.Short(cand)));
                        dst = cand;
                    }
                }

                try
                {
                    Directory.CreateDirectory(PathUtil.Long(dst));
                    result.DirsCreated++;
                    if (options.KeepTimestamps)
                        dirStamps.Add(new[] { Path.Combine(leftRoot, rel), dst });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    AddError(result, report, "创建目录失败 " + dst + "：" + ex.Message);
                }
            }

            if (!ct.IsCancellationRequested)
            {
                report.Report(CopyMessage.FromProgress(new CopyProgressInfo
                {
                    Phase = CopyPhase.CopyingFiles,
                    Done = 0,
                    Total = Math.Max(files.Count, 1),
                    Bytes = 0,
                    Message = "正在复制文件 …"
                }));
            }

            // ---------- 2. 复制文件 ----------
            long doneBytes = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                FileEntry fe = files[i];
                string rel = ApplyRemap(fe.Relative, remap);
                string dst = Path.Combine(rightRoot, rel);

                string parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent))
                {
                    try
                    {
                        Directory.CreateDirectory(PathUtil.Long(parent));
                    }
                    catch (Exception ex)
                    {
                        result.Failed++;
                        AddError(result, report, "创建父目录失败 " + parent + "：" + ex.Message);
                        continue;
                    }
                }

                if (string.Equals(PathUtil.Long(fe.Source).TrimEnd('\\'),
                                  PathUtil.Long(dst).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    result.Skipped++;
                    report.Report(CopyMessage.Log("源与目标相同，跳过：" + rel));
                    continue;
                }

                string finalDst = dst;
                if (File.Exists(PathUtil.Long(dst)) || Directory.Exists(PathUtil.Long(dst)))
                {
                    if (options.Policy == ConflictPolicy.Skip)
                    {
                        result.Skipped++;
                        continue;
                    }
                    if (options.Policy == ConflictPolicy.Rename)
                    {
                        finalDst = MakeUniqueName(dst);
                    }
                }

                try
                {
                    File.Copy(PathUtil.Long(fe.Source), PathUtil.Long(finalDst), true);
                    if (options.KeepTimestamps)
                    {
                        try
                        {
                            File.SetLastWriteTimeUtc(PathUtil.Long(finalDst), File.GetLastWriteTimeUtc(PathUtil.Long(fe.Source)));
                            File.SetCreationTimeUtc(PathUtil.Long(finalDst), File.GetCreationTimeUtc(PathUtil.Long(fe.Source)));
                        }
                        catch
                        {
                            // 时间戳失败不影响复制结果
                        }
                    }

                    result.Copied++;
                    if (result.FirstTargets.Count < 50) result.FirstTargets.Add(finalDst);
                    try
                    {
                        doneBytes += new FileInfo(PathUtil.Long(finalDst)).Length;
                    }
                    catch
                    {
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    AddError(result, report, "复制失败 " + rel + "：" + ex.Message);
                }

                if (i % 20 == 0 || clock.ElapsedMilliseconds > 80 || i == files.Count - 1)
                {
                    clock.Restart();
                    report.Report(CopyMessage.FromProgress(new CopyProgressInfo
                    {
                        Phase = CopyPhase.CopyingFiles,
                        Done = i + 1,
                        Total = Math.Max(files.Count, 1),
                        Bytes = doneBytes,
                        Failed = result.Failed,
                        Message = string.Format("正在复制 {0}/{1} · {2}", i + 1, files.Count, PathUtil.HumanSize(doneBytes))
                    }));
                }
            }

            // ---------- 3. 还原文件夹的修改时间（要在文件写完之后，由深到浅） ----------
            if (options.KeepTimestamps)
            {
                for (int i = dirStamps.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        string s = PathUtil.Long(dirStamps[i][0]);
                        string d = PathUtil.Long(dirStamps[i][1]);
                        if (Directory.Exists(s) && Directory.Exists(d))
                            Directory.SetLastWriteTimeUtc(d, Directory.GetLastWriteTimeUtc(s));
                    }
                    catch
                    {
                        // 时间戳还原失败不影响复制结果
                    }
                }
            }

            result.Cancelled = ct.IsCancellationRequested;
            result.Bytes = doneBytes;
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ------------------------------------------------------------------ //

        private static void ScanDirectory(
            string src, string relBase,
            List<string> dirs, List<FileEntry> files,
            ref int linkCount, CancellationToken ct,
            IProgress<CopyMessage> report)
        {
            var stack = new Stack<KeyValuePair<string, string>>();
            stack.Push(new KeyValuePair<string, string>(src, relBase ?? string.Empty));

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;

                KeyValuePair<string, string> cur = stack.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    var di = new DirectoryInfo(PathUtil.Long(cur.Key));
                    entries = new List<FileSystemInfo>(di.EnumerateFileSystemInfos());
                }
                catch (Exception ex)
                {
                    AddErrorThrottled(report, "读取目录失败 " + PathUtil.Short(cur.Key) + "：" + ex.Message);
                    continue;
                }

                foreach (FileSystemInfo fsi in entries)
                {
                    string rel = cur.Value.Length == 0 ? fsi.Name : Path.Combine(cur.Value, fsi.Name);
                    EntryKind kind = PathUtil.GetKind(fsi);

                    if (kind == EntryKind.Directory)
                    {
                        dirs.Add(rel);
                        stack.Push(new KeyValuePair<string, string>(fsi.FullName, rel));
                    }
                    else if (kind == EntryKind.Link)
                    {
                        if (PathUtil.IsLinkToDirectory(fsi.FullName)) linkCount++;
                        else files.Add(new FileEntry { Source = fsi.FullName, Relative = rel });
                    }
                    else
                    {
                        files.Add(new FileEntry { Source = fsi.FullName, Relative = rel });
                    }
                }
            }
        }

        private static string MakeUniqueName(string dst)
        {
            string dir = Path.GetDirectoryName(dst);
            string name = Path.GetFileNameWithoutExtension(dst);
            string ext = Path.GetExtension(dst);
            int i = 1;
            while (true)
            {
                string cand = Path.Combine(dir ?? "", string.Format("{0} ({1}){2}", name, i, ext));
                if (!File.Exists(PathUtil.Long(cand)) && !Directory.Exists(PathUtil.Long(cand)))
                    return cand;
                i++;
            }
        }

        /// <summary>把路径中被重命名过的目录前缀替换成实际落地的名字。</summary>
        private static string ApplyRemap(string rel, Dictionary<string, string> remap)
        {
            if (remap.Count == 0 || string.IsNullOrEmpty(rel)) return rel;

            string[] parts = rel.Split('\\');
            for (int d = parts.Length; d > 0; d--)
            {
                string prefix = string.Join("\\", parts, 0, d);
                string mapped;
                if (remap.TryGetValue(prefix, out mapped))
                    return mapped + rel.Substring(prefix.Length);
            }
            return rel;
        }

        private static int Depth(string p)
        {
            return p.Split('\\').Length;
        }

        private static void AddError(CopyResult r, IProgress<CopyMessage> report, string text)
        {
            if (r.Errors.Count < MaxErrors) r.Errors.Add(text);
            else if (r.Errors.Count == MaxErrors) r.Errors.Add("…（错误过多，后续不再记录）");
            report.Report(CopyMessage.Log(text, true));
        }

        private static void AddErrorThrottled(IProgress<CopyMessage> report, string text)
        {
            report.Report(CopyMessage.Log(text, true));
        }
    }
}
