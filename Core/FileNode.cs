using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace FileCopyWithMenu.Core
{
    /// <summary>树节点视图模型（按需懒加载子项，支持异步加载并复用 FileSystemInfo）。</summary>
    public sealed class FileNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isCurrent;
        private bool _isLoading;

        public string Name { get; private set; }
        public string FullPath { get; private set; }
        public bool IsDirectory { get; private set; }
        public bool IsLink { get; private set; }
        public bool IsPlaceholder { get; private set; }
        public bool IsLoaded { get; private set; }
        public string LoadError { get; private set; }
        public FileNode Parent { get; private set; }

        private readonly long _size;
        private readonly DateTime _modified;

        public ObservableCollection<FileNode> Children { get; } = new ObservableCollection<FileNode>();

        /// <summary>图标使用 Segoe Fluent Icons 字形。</summary>
        public string IconGlyph
        {
            get
            {
                if (IsPlaceholder) return "\uE7BA"; // Warning
                if (IsLink) return "\uE71B";        // Link
                if (IsDirectory) return "\uE8B7";   // Folder
                return "\uE8A5";                    // Document
            }
        }

        public string MetaText
        {
            get
            {
                if (IsPlaceholder) return "";
                if (IsDirectory)
                    return IsLink ? "目录链接" : "";
                return PathUtil.HumanSize(_size) + "   " + _modified.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged();
                if (value && !IsLoaded && !_isLoading)
                {
                    _ = LoadChildrenAsync();
                }
            }
        }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        /// <summary>键盘/鼠标焦点所在行（用于画焦点框）。</summary>
        public bool IsCurrent
        {
            get { return _isCurrent; }
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                OnPropertyChanged();
            }
        }

        public FileNode(string fullPath, FileNode parent)
        {
            FullPath = PathUtil.Short(fullPath);
            Name = Path.GetFileName(FullPath.TrimEnd('\\'));
            if (string.IsNullOrEmpty(Name)) Name = FullPath;
            Parent = parent;

            try
            {
                string lp = PathUtil.Long(FullPath);
                FileSystemInfo info = Directory.Exists(lp)
                    ? (FileSystemInfo)new DirectoryInfo(lp)
                    : new FileInfo(lp);

                long sz; DateTime mod; InitFromInfo(info, out sz, out mod); _size = sz; _modified = mod;
            }
            catch
            {
                IsDirectory = false;
            }

            if (IsDirectory && !IsLink && HasAnyEntry(FullPath))
                Children.Add(new FileNode());
        }

        /// <summary>直接复用枚举得到的 FileSystemInfo，避免重复磁盘 stat。</summary>
        private FileNode(FileSystemInfo info, FileNode parent)
        {
            FullPath = PathUtil.Short(info.FullName);
            Name = info.Name;
            if (string.IsNullOrEmpty(Name)) Name = FullPath;
            Parent = parent;

            try
            {
                long sz; DateTime mod; InitFromInfo(info, out sz, out mod); _size = sz; _modified = mod;
            }
            catch
            {
                IsDirectory = false;
            }

            if (IsDirectory && !IsLink && HasAnyEntry(FullPath))
                Children.Add(new FileNode());
        }

        private void InitFromInfo(FileSystemInfo info, out long size, out DateTime modified)
        {
            size = 0;
            modified = default;
            EntryKind kind = PathUtil.GetKind(info);
            IsLink = kind == EntryKind.Link;
            IsDirectory = kind == EntryKind.Directory || (IsLink && PathUtil.IsLinkToDirectory(FullPath));

            if (!IsDirectory)
            {
                var fi = info as FileInfo;
                if (fi != null)
                {
                    size = fi.Length;
                    modified = fi.LastWriteTime;
                }
            }
        }

        /// <summary>目录下是否至少有一个条目（只取第一个，开销极小）。</summary>
        private static bool HasAnyEntry(string path)
        {
            try
            {
                var di = new DirectoryInfo(PathUtil.Long(path));
                foreach (FileSystemInfo _ in di.EnumerateFileSystemInfos())
                    return true;
                return false;
            }
            catch
            {
                // 读不了的就当作有内容，展开时再给出具体错误
                return true;
            }
        }

        private FileNode()
        {
            IsPlaceholder = true;
            Name = "…";
            FullPath = string.Empty;
        }

        /// <summary>同步读取磁盘并填充子节点（供定位/初始化使用）。</summary>
        public void LoadChildren()
        {
            if (IsLoaded) return;
            IsLoaded = true;
            _isLoading = false;
            if (!IsDirectory || IsLink || IsPlaceholder) return;

            string error;
            List<FileNode> items = ScanChildren(out error);
            ApplyChildren(items, error);
        }

        /// <summary>在后台线程读取磁盘，完成后回到 UI 线程填充子节点，避免展开大目录或网络盘时卡顿。</summary>
        public async Task LoadChildrenAsync(CancellationToken ct = default)
        {
            if (IsLoaded || _isLoading) return;
            if (!IsDirectory || IsLink || IsPlaceholder)
            {
                IsLoaded = true;
                return;
            }

            _isLoading = true;
            string error = null;
            List<FileNode> items = null;

            try
            {
                items = await Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return null;
                    string err;
                    var list = ScanChildren(out err);
                    error = err;
                    return list;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                _isLoading = false;
                return;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _isLoading = false;
            if (ct.IsCancellationRequested || IsLoaded) return;

            IsLoaded = true;
            ApplyChildren(items, error);
        }

        /// <summary>强制重新加载当前节点的直接子项（例如新建文件夹后刷新局部）。</summary>
        public void ReloadChildren()
        {
            IsLoaded = false;
            _isLoading = false;
            LoadChildren();
        }

        private List<FileNode> ScanChildren(out string error)
        {
            error = null;
            var dirs = new List<FileNode>();
            var links = new List<FileNode>();
            var files = new List<FileNode>();

            try
            {
                var di = new DirectoryInfo(PathUtil.Long(FullPath));
                foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                {
                    EntryKind kind = PathUtil.GetKind(fsi);
                    var node = new FileNode(fsi, this);
                    if (kind == EntryKind.Directory && !node.IsLink)
                        dirs.Add(node);
                    else if (kind == EntryKind.Link || node.IsLink)
                        links.Add(node);
                    else
                        files.Add(node);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }

            dirs.Sort(Compare);
            links.Sort(Compare);
            files.Sort(Compare);

            var all = new List<FileNode>(dirs.Count + links.Count + files.Count);
            all.AddRange(dirs);
            all.AddRange(links);
            all.AddRange(files);
            return all;
        }

        private void ApplyChildren(List<FileNode> items, string error)
        {
            Children.Clear();
            if (!string.IsNullOrEmpty(error))
            {
                LoadError = error;
                Children.Add(ErrorNode("⚠ 无法读取：" + error, this));
                return;
            }
            LoadError = null;
            if (items == null) return;
            foreach (FileNode n in items) Children.Add(n);
        }

        private static int Compare(FileNode a, FileNode b)
        {
            return NaturalComparer.Instance.Compare(a.Name, b.Name);
        }

        private static FileNode ErrorNode(string text, FileNode parent)
        {
            var n = new FileNode();
            n.Name = text;
            n.FullPath = string.Empty;
            n.IsPlaceholder = true;
            n.Parent = parent;
            return n;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }
}
