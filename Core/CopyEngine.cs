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
        Rename,
        /// <summary>仅当源文件更新或大小不同时覆盖，否则跳过</summary>
        OverwriteIfNewer
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
                    case ConflictPolicy.OverwriteIfNewer: return "仅覆盖较新/不同大小";
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
        public long TotalBytes;
        public int Failed;
        public string Message;

        public double Percent
        {
            get
            {
                if (TotalBytes > 0)
                    return Math.Min(100.0, Bytes * 100.0 / TotalBytes);
                return Total > 0 ? Math.Min(100.0, Done * 100.0 / Total) : 0;
            }
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
        public long TotalBytes;
        public int Total;
        public bool Cancelled;
        public TimeSpan Elapsed;
        public string TargetRoot;
        public List<string> Errors = new List<string>();
        public List<string> FirstTargets = new List<string>();
        public List<string> FailedSources = new List<string>();
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
        public bool IsOk;
        public CopyProgressInfo Progress;
        public CopyResult Result;

        public static CopyMessage Log(string text, bool isError = false, bool isOk = false)
        {
            return new CopyMessage { Kind = CopyMessageKind.Log, Text = text, IsError = isError, IsOk = isOk };
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
        private const int StreamBufferSize = 1024 * 1024; // 1 MB 流式缓冲区

        private sealed class DirEntry
        {
            public string Source;
            public string Relative;
        }

        private sealed class FileEntry
        {
            public string Source;
            public string Relative;
            public long Size;
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

            var dirs = new List<DirEntry>();
            var files = new List<FileEntry>();
            int linkCount = 0;

            foreach (string src in sources)
            {
                if (ct.IsCancellationRequested) break;

                string lpSrc = PathUtil.Long(src);
                if (Directory.Exists(lpSrc))
                {
                    if (PathUtil.GetKind(new DirectoryInfo(lpSrc)) == EntryKind.Link)
                    {
                        report.Report(CopyMessage.Log(
                            "提示：选中项是目录链接/联接点，将按它指向的实际内容复制：" + src));
                    }
                    string rel = PathUtil.RelativeTo(src, leftRoot);
                    if (rel.Length > 0)
                        dirs.Add(new DirEntry { Source = PathUtil.Short(src), Relative = rel });
                    ScanDirectory(src, rel, dirs, files, ref linkCount, ct, report);
                }
                else if (File.Exists(lpSrc))
                {
                    string rel = PathUtil.RelativeTo(src, leftRoot);
                    if (string.IsNullOrEmpty(rel)) rel = Path.GetFileName(src);
                    long size = 0;
                    try { size = new FileInfo(lpSrc).Length; } catch { }
                    files.Add(new FileEntry { Source = PathUtil.Short(src), Relative = rel, Size = size });
                }
                else
                {
                    report.Report(CopyMessage.Log("忽略（不存在或不可访问）：" + src, true));
                }
            }

            if (linkCount > 0)
                report.Report(CopyMessage.Log("跳过 " + linkCount + " 个目录链接/联接点（不递归，避免成环）。"));

            long totalBytes = 0;
            foreach (FileEntry f in files) totalBytes += f.Size;

            result.Total = files.Count;
            result.TotalBytes = totalBytes;
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
                dirs.RemoveAll(d => !keep.Contains(d.Relative));
            }

            dirs.Sort((a, b) =>
            {
                int d = Depth(a.Relative).CompareTo(Depth(b.Relative));
                return d != 0 ? d : string.Compare(a.Relative, b.Relative, StringComparison.OrdinalIgnoreCase);
            });

            report.Report(CopyMessage.Log(string.Format(
                "待复制：{0} 个文件（共 {1}），{2} 个文件夹。",
                files.Count, PathUtil.HumanSize(totalBytes), dirs.Count)));

            // 目标磁盘剩余空间预检（网络路径无法获取时自动忽略）
            CheckAndWarnFreeSpace(rightRoot, totalBytes, report);

            report.Report(CopyMessage.FromProgress(new CopyProgressInfo
            {
                Phase = CopyPhase.CreatingDirectories,
                Done = 0,
                Total = Math.Max(files.Count, 1),
                Bytes = 0,
                TotalBytes = totalBytes,
                Message = "正在创建目录结构 …"
            }));

            // ---------- 1. 建目录 ----------
            var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var skippedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirStamps = new List<string[]>();   // {源目录, 目标目录}，复制结束后由深到浅还原修改时间

            foreach (DirEntry de in dirs)
            {
                if (ct.IsCancellationRequested) break;

                string rel = de.Relative;
                if (IsUnderSkippedDir(rel, skippedDirs))
                {
                    result.Skipped++;
                    continue;
                }

                string dst = Path.Combine(rightRoot, ApplyRemap(rel, remap));
                string lp = PathUtil.Long(dst);

                if (Directory.Exists(lp)) continue;

                if (File.Exists(lp))
                {
                    if (options.Policy == ConflictPolicy.Skip)
                    {
                        result.Skipped++;
                        skippedDirs.Add(rel);
                        report.Report(CopyMessage.Log("目录名被同名文件占用，跳过整棵子树：" + rel, true));
                        continue;
                    }
                    if (options.Policy == ConflictPolicy.Overwrite || options.Policy == ConflictPolicy.OverwriteIfNewer)
                    {
                        try
                        {
                            File.SetAttributes(lp, FileAttributes.Normal);
                            File.Delete(lp);
                        }
                        catch (Exception ex)
                        {
                            result.Failed++;
                            skippedDirs.Add(rel);
                            AddError(result, report, "删除冲突文件失败 " + dst + "：" + ex.Message);
                            continue;
                        }
                    }
                    else
                    {
                        int idx = 1;
                        string cand;
                        do
                        {
                            cand = dst + " (" + idx + ")";
                            idx++;
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
                        dirStamps.Add(new[] { de.Source, dst });
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    skippedDirs.Add(rel);
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
                    TotalBytes = totalBytes,
                    Message = "正在复制文件 …"
                }));
            }

            // ---------- 2. 流式复制文件 ----------
            long doneBytes = 0;
            long copiedBytes = 0;
            var clock = Stopwatch.StartNew();
            var copySw = Stopwatch.StartNew();
            var buffer = new byte[StreamBufferSize];

            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                FileEntry fe = files[i];
                bool handled = false;

                if (IsUnderSkippedDir(fe.Relative, skippedDirs))
                {
                    result.Skipped++;
                    doneBytes += fe.Size;
                    handled = true;
                }

                if (!handled)
                {
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
                            result.FailedSources.Add(fe.Source);
                            AddError(result, report, "创建父目录失败 " + parent + "：" + ex.Message);
                            doneBytes += fe.Size;
                            handled = true;
                        }
                    }

                    if (!handled && PathUtil.IsSamePath(fe.Source, dst))
                    {
                        result.Skipped++;
                        report.Report(CopyMessage.Log("源与目标相同，跳过：" + rel));
                        doneBytes += fe.Size;
                        handled = true;
                    }

                    if (!handled)
                    {
                        string finalDst = dst;
                        string lpDst = PathUtil.Long(dst);
                        bool dstIsDir = Directory.Exists(lpDst);
                        bool dstIsFile = !dstIsDir && File.Exists(lpDst);

                        if (dstIsDir)
                        {
                            if (options.Policy == ConflictPolicy.Skip)
                            {
                                result.Skipped++;
                                report.Report(CopyMessage.Log("目标存在同名文件夹，跳过：" + rel, true));
                                doneBytes += fe.Size;
                                handled = true;
                            }
                            else if (options.Policy == ConflictPolicy.Rename)
                            {
                                finalDst = MakeUniqueName(dst);
                            }
                            else
                            {
                                result.Failed++;
                                result.FailedSources.Add(fe.Source);
                                AddError(result, report, "目标位置存在同名文件夹，无法用文件覆盖：" + rel);
                                doneBytes += fe.Size;
                                handled = true;
                            }
                        }
                        else if (dstIsFile)
                        {
                            if (options.Policy == ConflictPolicy.Skip)
                            {
                                result.Skipped++;
                                doneBytes += fe.Size;
                                handled = true;
                            }
                            else if (options.Policy == ConflictPolicy.OverwriteIfNewer &&
                                     IsSameOrNewerTarget(PathUtil.Long(fe.Source), lpDst))
                            {
                                result.Skipped++;
                                doneBytes += fe.Size;
                                handled = true;
                            }
                            else if (options.Policy == ConflictPolicy.Rename)
                            {
                                finalDst = MakeUniqueName(dst);
                            }
                        }

                        if (!handled)
                        {
                            try
                            {
                                long written = CopyFileStreaming(
                                    fe.Source, finalDst, options.KeepTimestamps, buffer, ct,
                                    delta =>
                                    {
                                        doneBytes += delta;
                                        copiedBytes += delta;
                                        if (clock.ElapsedMilliseconds >= 80)
                                        {
                                            clock.Restart();
                                            ReportCopyProgress(report, i, files.Count, doneBytes, totalBytes, copiedBytes, copySw, result.Failed);
                                        }
                                    });

                                if (ct.IsCancellationRequested) break;

                                result.Copied++;
                                if (result.FirstTargets.Count < 50) result.FirstTargets.Add(finalDst);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                result.Failed++;
                                result.FailedSources.Add(fe.Source);
                                AddError(result, report, "复制失败 " + rel + "：" + ex.Message);
                            }
                        }
                    }
                }

                if (i % 20 == 0 || clock.ElapsedMilliseconds >= 80 || i == files.Count - 1)
                {
                    clock.Restart();
                    ReportCopyProgress(report, i + 1, files.Count, doneBytes, totalBytes, copiedBytes, copySw, result.Failed);
                }
            }

            // ---------- 3. 还原文件夹的修改时间（要在文件写完之后，由深到浅） ----------
            if (options.KeepTimestamps && !ct.IsCancellationRequested)
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
            result.Bytes = copiedBytes;
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ------------------------------------------------------------------ //

        private static long CopyFileStreaming(
            string srcPath,
            string dstPath,
            bool keepTimestamps,
            byte[] buffer,
            CancellationToken ct,
            Action<long> onChunkWritten)
        {
            string lpSrc = PathUtil.Long(srcPath);
            string lpDst = PathUtil.Long(dstPath);
            string tmpPath = dstPath + ".~fcwm_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string lpTmp = PathUtil.Long(tmpPath);

            long written = 0;
            bool committed = false;

            try
            {
                using (var srcStream = new FileStream(
                    lpSrc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    buffer.Length, FileOptions.SequentialScan))
                using (var dstStream = new FileStream(
                    lpTmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.None))
                {
                    int read;
                    while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dstStream.Write(buffer, 0, read);
                        written += read;
                        if (onChunkWritten != null) onChunkWritten(read);
                    }
                }

                ct.ThrowIfCancellationRequested();

                if (keepTimestamps)
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(lpTmp, File.GetLastWriteTimeUtc(lpSrc));
                        File.SetCreationTimeUtc(lpTmp, File.GetCreationTimeUtc(lpSrc));
                    }
                    catch
                    {
                        // 时间戳失败不影响复制结果
                    }
                }

                if (File.Exists(lpDst))
                {
                    try { File.SetAttributes(lpDst, FileAttributes.Normal); } catch { }
                }

                File.Move(lpTmp, lpDst, true);
                committed = true;
                return written;
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (File.Exists(lpTmp)) File.Delete(lpTmp);
                    }
                    catch
                    {
                        // 清理临时文件失败忽略
                    }
                }
            }
        }

        private static bool IsSameOrNewerTarget(string lpSrc, string lpDst)
        {
            try
            {
                var srcInfo = new FileInfo(lpSrc);
                var dstInfo = new FileInfo(lpDst);
                if (srcInfo.Length != dstInfo.Length) return false;
                // 允许 2 秒误差（兼容 FAT32 / SMB 时间精度）
                return dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc.AddSeconds(-2);
            }
            catch
            {
                return false;
            }
        }

        private static void CheckAndWarnFreeSpace(string targetRoot, long totalBytes, IProgress<CopyMessage> report)
        {
            if (totalBytes <= 0) return;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(PathUtil.Short(targetRoot)));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                    return;
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < totalBytes)
                {
                    report.Report(CopyMessage.Log(string.Format(
                        "警告：目标磁盘 {0} 可用空间仅剩 {1}，小于待复制总大小 {2}，中途可能因空间不足失败！",
                        drive.Name, PathUtil.HumanSize(drive.AvailableFreeSpace), PathUtil.HumanSize(totalBytes)), true));
                }
            }
            catch
            {
                // 无法获取磁盘信息时忽略
            }
        }

        private static void ReportCopyProgress(
            IProgress<CopyMessage> report,
            int doneCount,
            int totalCount,
            long doneBytes,
            long totalBytes,
            long copiedBytes,
            Stopwatch copySw,
            int failed)
        {
            double secs = Math.Max(0.05, copySw.Elapsed.TotalSeconds);
            string speed = PathUtil.HumanSpeed(copiedBytes / secs);
            string msg = totalBytes > 0
                ? string.Format("正在复制 {0}/{1} · {2} / {3} ({4})",
                    doneCount, totalCount, PathUtil.HumanSize(doneBytes), PathUtil.HumanSize(totalBytes), speed)
                : string.Format("正在复制 {0}/{1}", doneCount, totalCount);

            report.Report(CopyMessage.FromProgress(new CopyProgressInfo
            {
                Phase = CopyPhase.CopyingFiles,
                Done = doneCount,
                Total = Math.Max(totalCount, 1),
                Bytes = doneBytes,
                TotalBytes = totalBytes,
                Failed = failed,
                Message = msg
            }));
        }

        private static void ScanDirectory(
            string src, string relBase,
            List<DirEntry> dirs, List<FileEntry> files,
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
                    if (ct.IsCancellationRequested) return;

                    string rel = cur.Value.Length == 0 ? fsi.Name : Path.Combine(cur.Value, fsi.Name);
                    EntryKind kind = PathUtil.GetKind(fsi);

                    if (kind == EntryKind.Directory)
                    {
                        dirs.Add(new DirEntry { Source = PathUtil.Short(fsi.FullName), Relative = rel });
                        stack.Push(new KeyValuePair<string, string>(fsi.FullName, rel));
                    }
                    else if (kind == EntryKind.Link)
                    {
                        if (PathUtil.IsLinkToDirectory(fsi.FullName))
                        {
                            linkCount++;
                        }
                        else
                        {
                            long size = 0;
                            try { size = new FileInfo(PathUtil.Long(fsi.FullName)).Length; } catch { }
                            files.Add(new FileEntry { Source = PathUtil.Short(fsi.FullName), Relative = rel, Size = size });
                        }
                    }
                    else
                    {
                        long size = 0;
                        try { size = ((FileInfo)fsi).Length; } catch { }
                        files.Add(new FileEntry { Source = PathUtil.Short(fsi.FullName), Relative = rel, Size = size });
                    }
                }
            }
        }

        private static bool IsUnderSkippedDir(string rel, HashSet<string> skippedDirs)
        {
            if (skippedDirs.Count == 0 || string.IsNullOrEmpty(rel)) return false;
            string[] parts = rel.Split('\\');
            for (int d = 1; d <= parts.Length; d++)
            {
                string prefix = string.Join("\\", parts, 0, d);
                if (skippedDirs.Contains(prefix)) return true;
            }
            return false;
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
