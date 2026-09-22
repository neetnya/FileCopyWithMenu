using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace FileCopyWithMenu.Core
{
    /// <summary>树节点视图模型（按需懒加载子项）。</summary>
    public sealed class FileNode : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _isSelected;
        private bool _isCurrent;

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
                if (value) LoadChildren();
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
            Name = Path.GetFileName(FullPath);
            if (string.IsNullOrEmpty(Name)) Name = FullPath;
            Parent = parent;

            try
            {
                FileSystemInfo info = Directory.Exists(PathUtil.Long(FullPath))
                    ? (FileSystemInfo)new DirectoryInfo(PathUtil.Long(FullPath))
                    : new FileInfo(PathUtil.Long(FullPath));

                EntryKind kind = PathUtil.GetKind(info);
                IsLink = kind == EntryKind.Link;
                IsDirectory = kind == EntryKind.Directory || (IsLink && PathUtil.IsLinkToDirectory(FullPath));

                if (!IsDirectory)
                {
                    var fi = info as FileInfo;
                    if (fi != null)
                    {
                        _size = fi.Length;
                        _modified = fi.LastWriteTime;
                    }
                }
            }
            catch
            {
                IsDirectory = false;
            }

            // 只有真正含有内容的目录才挂占位子节点，这样空文件夹不会出现展开箭头
            if (IsDirectory && !IsLink && HasAnyEntry(FullPath))
                Children.Add(new FileNode());
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

        /// <summary>读取磁盘，填充子节点。</summary>
        public void LoadChildren()
        {
            if (IsLoaded) return;
            IsLoaded = true;
            if (!IsDirectory || IsLink || IsPlaceholder) return;

            Children.Clear();
            var dirs = new List<FileNode>();
            var links = new List<FileNode>();
            var files = new List<FileNode>();

            try
            {
                var di = new DirectoryInfo(PathUtil.Long(FullPath));
                foreach (FileSystemInfo fsi in di.EnumerateFileSystemInfos())
                {
                    EntryKind kind = PathUtil.GetKind(fsi);
                    if (kind == EntryKind.Directory)
                    {
                        var node = new FileNode(fsi.FullName, this);
                        if (node.IsLink) links.Add(node);
                        else dirs.Add(node);
                    }
                    else if (kind == EntryKind.Link)
                    {
                        links.Add(new FileNode(fsi.FullName, this));
                    }
                    else
                    {
                        files.Add(new FileNode(fsi.FullName, this));
                    }
                }
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                Children.Add(ErrorNode("⚠ 无法读取：" + ex.Message, this));
                return;
            }

            dirs.Sort(Compare);
            links.Sort(Compare);
            files.Sort(Compare);

            foreach (FileNode n in dirs) Children.Add(n);
            foreach (FileNode n in links) Children.Add(n);
            foreach (FileNode n in files) Children.Add(n);
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
