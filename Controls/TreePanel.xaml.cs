using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileCopyWithMenu.Core;
using Microsoft.Win32;

namespace FileCopyWithMenu.Controls
{
    /// <summary>一个支持多选、异步懒加载、拖放、全展开/全折叠的目录树面板。</summary>
    public partial class TreePanel : UserControl
    {
        private const int ExpandFolderCap = 40000;

        private readonly ObservableCollection<FileNode> _roots = new ObservableCollection<FileNode>();
        private readonly List<FileNode> _selected = new List<FileNode>();

        private FileNode _root;
        private FileNode _anchor;
        private FileNode _current;
        private CancellationTokenSource _countCts;
        private CancellationTokenSource _expandCts;
        private bool _bulk;

        public event EventHandler SelectionChanged;
        public event EventHandler CopyRequested;

        public bool IsSourcePanel { get; set; } = true;

        public TreePanel()
        {
            InitializeComponent();
            Tree.ItemsSource = _roots;
        }

        public string RootPath
        {
            get { return _root == null ? null : _root.FullPath; }
        }

        public string PanelTitle
        {
            get { return HeaderText.Text; }
            set { HeaderText.Text = value; }
        }

        public void SetInfo(string text)
        {
            InfoText.Text = text;
        }

        public IList<FileNode> SelectedNodes
        {
            get { return new List<FileNode>(_selected); }
        }

        public List<string> SelectedPaths()
        {
            var list = new List<string>();
            foreach (FileNode n in _selected)
            {
                if (!n.IsPlaceholder && !string.IsNullOrEmpty(n.FullPath))
                    list.Add(n.FullPath);
            }
            return list;
        }

        // ================================================================= //
        //  载入目录 & 拖放
        // ================================================================= //

        public bool SetRoot(string path, bool silent = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string full;
            try { full = Path.GetFullPath(path.Trim().Trim('"')); }
            catch { full = path.Trim().Trim('"'); }

            if (!Directory.Exists(PathUtil.Long(full)))
            {
                if (!silent)
                {
                    MessageBox.Show("目录不存在或不可访问：\n" + full, "FileCopyWithMenu",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return false;
            }

            CancelExpand();
            CancelCount();

            _bulk = true;
            ClearSelection();
            _bulk = false;

            _roots.Clear();
            _root = new FileNode(full, null);
            _root.LoadChildren();
            _root.IsExpanded = true;
            _roots.Add(_root);
            PathBox.Text = _root.FullPath;
            _current = null;
            _anchor = null;

            if (!string.IsNullOrEmpty(_root.LoadError))
                SetInfo("读取失败：" + _root.LoadError);
            else
                SetInfo("共 " + _root.Children.Count + " 项");
            ShowEmptyHint(false);
            OnSelectionChanged();
            return true;
        }

        /// <summary>显示/隐藏空状态提示（没有打开任何目录时给出引导文字）。</summary>
        public void ShowEmptyHint(bool show)
        {
            if (show)
            {
                EmptyHint.Text = IsSourcePanel
                    ? "点【浏览…】或拖入文件夹作为源目录"
                    : "点【浏览…】或拖入文件夹作为目标目录";
                EmptyHint.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }

        public void Refresh()
        {
            if (_root != null) SetRoot(_root.FullPath, true);
        }

        /// <summary>
        /// 清空当前根目录，回到“未选择目录”的空状态。
        /// 启动时两侧都会调用它：本程序不做任何记忆，不预加载、不恢复上次用过的目录。
        /// </summary>
        public void ClearRoot()
        {
            CancelExpand();
            CancelCount();

            _bulk = true;
            ClearSelection();
            _bulk = false;
            _roots.Clear();
            _root = null;
            _current = null;
            _anchor = null;
            PathBox.Text = string.Empty;
            SetInfo("");
            ShowEmptyHint(true);
            UpdateSelectionInfo();
        }

        private void Panel_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped != null && dropped.Length > 0 && Directory.Exists(PathUtil.Long(dropped[0])))
                {
                    e.Effects = DragDropEffects.Link;
                    e.Handled = true;
                    return;
                }
            }
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Panel_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || dropped.Length == 0) return;

            string target = dropped[0];
            if (Directory.Exists(PathUtil.Long(target)))
            {
                SetRoot(target);
            }
            else if (File.Exists(PathUtil.Long(target)))
            {
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) SetRoot(parent);
            }
            e.Handled = true;
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择目录",
                Multiselect = false
            };
            if (_root != null) dlg.InitialDirectory = _root.FullPath;
            if (dlg.ShowDialog() == true)
                SetRoot(dlg.FolderName);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_root == null) return;
            try
            {
                DirectoryInfo parent = Directory.GetParent(_root.FullPath);
                if (parent != null) SetRoot(parent.FullName);
            }
            catch
            {
                // 已在根路径或不可访问时忽略
            }
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SetRoot(PathBox.Text);
                e.Handled = true;
            }
        }

        // ================================================================= //
        //  展开 / 折叠
        // ================================================================= //

        public Task ExpandAllAsync()
        {
            if (_root == null) return Task.CompletedTask;
            return ExpandNodesAsync(new[] { _root });
        }

        private async Task ExpandNodesAsync(IEnumerable<FileNode> startNodes)
        {
            CancelExpand();
            var cts = new CancellationTokenSource();
            _expandCts = cts;
            CancellationToken ct = cts.Token;

            int loaded = 0;
            try
            {
                var queue = new Queue<FileNode>();
                foreach (FileNode s in startNodes)
                {
                    if (s != null && !s.IsPlaceholder && s.IsDirectory && !s.IsLink)
                        queue.Enqueue(s);
                }

                while (queue.Count > 0)
                {
                    if (ct.IsCancellationRequested) return;

                    FileNode n = queue.Dequeue();
                    if (n.IsPlaceholder || !n.IsDirectory || n.IsLink) continue;

                    await n.LoadChildrenAsync(ct);
                    if (ct.IsCancellationRequested) return;

                    n.IsExpanded = true;
                    foreach (FileNode c in n.Children)
                    {
                        if (!c.IsPlaceholder && c.IsDirectory && !c.IsLink)
                            queue.Enqueue(c);
                    }
                    loaded++;

                    if (loaded % 60 == 0)
                    {
                        SetInfo("正在展开 … 已加载 " + loaded + " 个文件夹");
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    if (loaded >= ExpandFolderCap)
                    {
                        SetInfo("已展开 " + loaded + " 个文件夹，达到上限后停止");
                        return;
                    }
                }
                if (!ct.IsCancellationRequested)
                    SetInfo("已展开 " + loaded + " 个文件夹");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (_expandCts == cts)
                {
                    _expandCts = null;
                }
                cts.Dispose();
            }

            if (!ct.IsCancellationRequested)
                UpdateSelectionInfo();
        }

        public void CollapseAll()
        {
            if (_root == null) return;
            CancelExpand();
            _bulk = true;
            foreach (FileNode n in VisibleNodes())
                if (n != _root && n.IsDirectory) n.IsExpanded = false;
            _bulk = false;
            CollapseRecursive(_root, true);
            _root.IsExpanded = true;
            UpdateSelectionInfo();
        }

        private void CollapseRecursive(FileNode n, bool skipSelf)
        {
            if (!skipSelf && n.IsDirectory) n.IsExpanded = false;
            foreach (FileNode c in n.Children)
            {
                if (c.IsPlaceholder) continue;
                CollapseRecursive(c, false);
            }
        }

        private async void ExpandAll_Click(object sender, RoutedEventArgs e)
        {
            await ExpandAllAsync();
        }

        private void CollapseAll_Click(object sender, RoutedEventArgs e)
        {
            CollapseAll();
        }

        private void CancelExpand()
        {
            if (_expandCts != null)
            {
                try { _expandCts.Cancel(); } catch { }
                try { _expandCts.Dispose(); } catch { }
                _expandCts = null;
            }
        }

        private void CancelCount()
        {
            if (_countCts != null)
            {
                try { _countCts.Cancel(); } catch { }
                try { _countCts.Dispose(); } catch { }
                _countCts = null;
            }
        }

        // ================================================================= //
        //  选择
        // ================================================================= //

        public List<FileNode> VisibleNodes()
        {
            var list = new List<FileNode>();
            if (_root != null) Walk(_root, list);
            return list;
        }

        private static void Walk(FileNode n, List<FileNode> acc)
        {
            if (n.IsPlaceholder) return;
            acc.Add(n);
            if (!n.IsExpanded) return;
            foreach (FileNode c in n.Children)
                Walk(c, acc);
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            SelectAllVisible();
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            OnSelectionChanged();
        }

        public void SelectAllVisible()
        {
            _bulk = true;
            foreach (FileNode n in VisibleNodes())
            {
                if (!_selected.Contains(n))
                {
                    _selected.Add(n);
                    n.IsSelected = true;
                }
            }
            _bulk = false;
            OnSelectionChanged();
        }

        public void ClearSelection()
        {
            foreach (FileNode n in _selected) n.IsSelected = false;
            _selected.Clear();
            if (!_bulk) UpdateSelectionInfo();
        }

        public void SelectNode(FileNode n)
        {
            _bulk = true;
            ClearSelection();
            if (n != null && !n.IsPlaceholder)
            {
                _selected.Add(n);
                n.IsSelected = true;
                _anchor = n;
            }
            _bulk = false;
            OnSelectionChanged();
        }

        private void AddSelection(FileNode n)
        {
            if (n == null || n.IsPlaceholder) return;
            if (_selected.Contains(n)) return;
            _selected.Add(n);
            n.IsSelected = true;
        }

        private void RemoveSelection(FileNode n)
        {
            if (n == null) return;
            if (!_selected.Remove(n)) return;
            n.IsSelected = false;
        }

        private void ToggleSelection(FileNode n)
        {
            if (_selected.Contains(n)) RemoveSelection(n);
            else { AddSelection(n); _anchor = n; }
        }

        private void SelectSingle(FileNode n)
        {
            _bulk = true;
            ClearSelection();
            AddSelection(n);
            _bulk = false;
            _anchor = n;
        }

        private void SelectRangeTo(FileNode n, bool keep)
        {
            List<FileNode> visible = VisibleNodes();
            FileNode reference = _anchor ?? _current ?? _root;
            int i1 = reference == null ? -1 : visible.IndexOf(reference);
            int i2 = visible.IndexOf(n);
            if (i1 < 0 || i2 < 0)
            {
                SelectSingle(n);
                return;
            }
            _bulk = true;
            if (!keep) ClearSelection();
            int a = Math.Min(i1, i2), b = Math.Max(i1, i2);
            for (int i = a; i <= b; i++) AddSelection(visible[i]);
            _bulk = false;
        }

        private void SetCurrent(FileNode n)
        {
            if (_current != null) _current.IsCurrent = false;
            _current = n;
            if (_current != null) _current.IsCurrent = true;
        }

        private void OnSelectionChanged()
        {
            EventHandler h = SelectionChanged;
            if (h != null) h(this, EventArgs.Empty);

            CancelCount();

            List<string> rawPaths = SelectedPaths();
            if (rawPaths.Count == 0)
            {
                UpdateSelectionInfo();
                return;
            }

            // 按实际复制规则去重后再统计，避免父子同时选中导致重复计算
            List<string> normalized = PathUtil.NormalizeSources(rawPaths);

            var cts = new CancellationTokenSource();
            _countCts = cts;
            CancellationToken token = cts.Token;
            int displayCount = rawPaths.Count;

            SetInfo("已选 " + displayCount + " 项 · 统计中 …");

            Task.Run(() =>
            {
                long dirs = 0, files = 0, bytes = 0;
                foreach (string p in normalized)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        string lp = PathUtil.Long(p);
                        if (Directory.Exists(lp))
                        {
                            dirs++;
                            CountTree(p, ref dirs, ref files, ref bytes, token);
                        }
                        else if (File.Exists(lp))
                        {
                            files++;
                            try { bytes += new FileInfo(lp).Length; } catch { }
                        }
                    }
                    catch
                    {
                        // 统计失败不影响选择
                    }
                }
                if (token.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                    SetInfo(string.Format("已选 {0} 项（{1} 文件夹 / {2} 文件，合计 {3}）",
                        displayCount, dirs, files, PathUtil.HumanSize(bytes)));
                }), DispatcherPriority.Background);
            }, token);
        }

        private static void CountTree(string path, ref long dirs, ref long files, ref long bytes, CancellationToken ct)
        {
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return;
                string cur = stack.Pop();
                List<FileSystemInfo> entries;
                try
                {
                    entries = new List<FileSystemInfo>(new DirectoryInfo(PathUtil.Long(cur)).EnumerateFileSystemInfos());
                }
                catch
                {
                    continue;
                }
                foreach (FileSystemInfo fsi in entries)
                {
                    if (ct.IsCancellationRequested) return;
                    switch (PathUtil.GetKind(fsi))
                    {
                        case EntryKind.Directory:
                            dirs++;
                            stack.Push(fsi.FullName);
                            break;
                        case EntryKind.Link:
                            // 与 CopyEngine 保持一致：目录链接跳过，文件链接按文件计入
                            if (!PathUtil.IsLinkToDirectory(fsi.FullName))
                            {
                                files++;
                                try { bytes += new FileInfo(PathUtil.Long(fsi.FullName)).Length; } catch { }
                            }
                            break;
                        default:
                            files++;
                            try { bytes += ((FileInfo)fsi).Length; } catch { }
                            break;
                    }
                }
            }
        }

        private void UpdateSelectionInfo()
        {
            if (_selected.Count > 0)
            {
                SetInfo("已选 " + _selected.Count + " 项");
                return;
            }
            if (_root == null) SetInfo("未选择目录");
            else SetInfo("未选择（根目录下 " + _root.Children.Count + " 项）");
        }

        // ================================================================= //
        //  鼠标 / 键盘
        // ================================================================= //

        private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject src = e.OriginalSource as DependencyObject;

            // 点在展开箭头上 -> 交给箭头自己处理
            if (FindAncestor<ToggleButton>(src) != null) return;

            TreeViewItem item = FindAncestor<TreeViewItem>(src);
            FileNode node = item == null ? null : item.DataContext as FileNode;

            if (e.ClickCount == 2)
            {
                if (node != null && node.IsDirectory && !node.IsLink)
                    node.IsExpanded = !node.IsExpanded;
                e.Handled = true;
                return;
            }

            if (node == null || node.IsPlaceholder)
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    ClearSelection();
                    OnSelectionChanged();
                }
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;

            if (shift) SelectRangeTo(node, ctrl);
            else if (ctrl) ToggleSelection(node);
            else SelectSingle(node);

            SetCurrent(node);
            if (item != null) item.Focus();
            OnSelectionChanged();
            e.Handled = true;
        }

        private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.A && ctrl)
            {
                SelectAllVisible();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClearSelection();
                OnSelectionChanged();
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                if (_current != null) { ToggleSelection(_current); OnSelectionChanged(); }
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ExtendCurrent(1);
                else MoveCurrent(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ExtendCurrent(-1);
                else MoveCurrent(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                if (_current != null && _current.IsDirectory && !_current.IsLink)
                {
                    if (!_current.IsExpanded) _current.IsExpanded = true;
                    else MoveCurrent(1);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                if (_current != null)
                {
                    if (_current.IsDirectory && _current.IsExpanded && !_current.IsLink)
                    {
                        _current.IsExpanded = false;
                    }
                    else if (_current.Parent != null)
                    {
                        SelectNode(_current.Parent);
                        SetCurrent(_current.Parent);
                        EnsureVisible(_current);
                    }
                }
                e.Handled = true;
            }
        }

        private void MoveCurrent(int delta)
        {
            List<FileNode> list = VisibleNodes();
            if (list.Count == 0) return;
            int idx = _current == null ? -1 : list.IndexOf(_current);
            if (idx < 0) idx = delta > 0 ? -1 : 0;
            int next = Math.Max(0, Math.Min(list.Count - 1, idx + delta));
            FileNode n = list[next];

            _bulk = true;
            ClearSelection();
            AddSelection(n);
            _bulk = false;
            _anchor = n;
            SetCurrent(n);
            OnSelectionChanged();
            EnsureVisible(n);
        }

        /// <summary>Shift + ↑/↓：保持已有选择，把选区扩展到相邻节点。</summary>
        private void ExtendCurrent(int delta)
        {
            List<FileNode> list = VisibleNodes();
            if (list.Count == 0) return;
            int idx = _current == null ? -1 : list.IndexOf(_current);
            if (idx < 0) idx = delta > 0 ? -1 : 0;
            int next = Math.Max(0, Math.Min(list.Count - 1, idx + delta));
            if (next == idx) return;

            FileNode n = list[next];
            SetCurrent(n);
            SelectRangeTo(n, true);
            OnSelectionChanged();
            EnsureVisible(n);
        }

        /// <summary>沿祖先链逐层定位 TreeViewItem 并滚动到可见区域（支持任意深层子节点）。</summary>
        private void EnsureVisible(FileNode node)
        {
            if (node == null) return;

            var chain = new Stack<FileNode>();
            FileNode p = node;
            while (p != null)
            {
                chain.Push(p);
                p = p.Parent;
            }

            ItemsControl containerParent = Tree;
            TreeViewItem targetItem = null;

            while (chain.Count > 0 && containerParent != null)
            {
                FileNode cur = chain.Pop();
                containerParent.ApplyTemplate();
                containerParent.UpdateLayout();

                TreeViewItem item = containerParent.ItemContainerGenerator.ContainerFromItem(cur) as TreeViewItem;
                if (item == null)
                {
                    if (containerParent is TreeViewItem parentTvi)
                        parentTvi.BringIntoView();
                    containerParent.UpdateLayout();
                    item = containerParent.ItemContainerGenerator.ContainerFromItem(cur) as TreeViewItem;
                }

                if (item == null) break;
                item.BringIntoView();
                targetItem = item;
                containerParent = item;
            }

            if (targetItem != null)
                targetItem.BringIntoView();
        }

        /// <summary>展开到指定路径并选中它。</summary>
        public bool RevealPath(string target)
        {
            if (_root == null || string.IsNullOrEmpty(target)) return false;
            if (!PathUtil.IsWithin(target, _root.FullPath)) return false;

            if (PathUtil.IsSamePath(target, _root.FullPath))
            {
                SelectNode(_root);
                SetCurrent(_root);
                EnsureVisible(_root);
                return true;
            }

            string rel = PathUtil.RelativeTo(target, _root.FullPath);
            FileNode cur = _root;
            cur.LoadChildren();
            cur.IsExpanded = true;

            foreach (string seg in rel.Split('\\'))
            {
                if (string.IsNullOrEmpty(seg)) continue;
                FileNode next = null;
                foreach (FileNode c in cur.Children)
                {
                    if (string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                }
                if (next == null) break;
                if (next.IsDirectory && !next.IsLink)
                {
                    next.LoadChildren();
                    next.IsExpanded = true;
                }
                cur = next;
            }

            SelectNode(cur);
            SetCurrent(cur);
            EnsureVisible(cur);
            return true;
        }

        // ================================================================= //
        //  右键菜单
        // ================================================================= //

        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            FileNode node = NodeAtMouse();
            if (node == null)
            {
                e.Handled = true;
                return;
            }
            if (!_selected.Contains(node)) SelectNode(node);

            Visibility vis = IsSourcePanel ? Visibility.Visible : Visibility.Collapsed;
            if (MiCopy != null) MiCopy.Visibility = vis;
            if (MiCopySep != null) MiCopySep.Visibility = vis;
        }

        private FileNode NodeAtMouse()
        {
            DependencyObject d = Mouse.DirectlyOver as DependencyObject;
            TreeViewItem item = FindAncestor<TreeViewItem>(d);
            if (item == null) return null;
            return item.DataContext as FileNode;
        }

        private void MiCopy_Click(object sender, RoutedEventArgs e)
        {
            EventHandler h = CopyRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        private void MiToggle_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileNode n in SelectedNodes)
            {
                if (n.IsDirectory && !n.IsLink) n.IsExpanded = !n.IsExpanded;
            }
        }

        private async void MiExpandSubtree_Click(object sender, RoutedEventArgs e)
        {
            List<FileNode> targets = SelectedNodes
                .Where(n => n != null && !n.IsPlaceholder && n.IsDirectory && !n.IsLink)
                .ToList();
            if (targets.Count > 0)
                await ExpandNodesAsync(targets);
        }

        private void MiNewFolder_Click(object sender, RoutedEventArgs e)
        {
            FileNode target = _selected.FirstOrDefault(n => !n.IsPlaceholder) ?? _root;
            if (target == null) return;

            FileNode parentDir = (target.IsDirectory && !target.IsLink) ? target : target.Parent;
            if (parentDir == null) return;

            string folderName = PromptFolderName(parentDir.FullPath);
            if (string.IsNullOrWhiteSpace(folderName)) return;

            folderName = folderName.Trim();
            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("文件夹名称包含非法字符。", "FileCopyWithMenu",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string newPath = Path.Combine(parentDir.FullPath, folderName);
            try
            {
                Directory.CreateDirectory(PathUtil.Long(newPath));
                parentDir.ReloadChildren();
                parentDir.IsExpanded = true;
                RevealPath(newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("新建文件夹失败：\n" + ex.Message, "FileCopyWithMenu",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string PromptFolderName(string parentPath)
        {
            var dlg = new Window
            {
                Title = "新建文件夹",
                Width = 380,
                Height = 155,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
                Background = (Brush)TryFindResource("AppBgBrush") ?? Brushes.White
            };

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = "在 " + parentPath + " 下新建：",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = parentPath
            };
            Grid.SetRow(label, 0);

            var box = new TextBox { Text = "新建文件夹", Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(box, 1);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 72, IsCancel = true };
            okBtn.Click += (s, args) => { dlg.DialogResult = true; };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 2);

            root.Children.Add(label);
            root.Children.Add(box);
            root.Children.Add(btnPanel);
            dlg.Content = root;

            dlg.Loaded += (s, args) => { box.Focus(); box.SelectAll(); };

            return dlg.ShowDialog() == true ? box.Text : null;
        }

        private void MiOpen_Click(object sender, RoutedEventArgs e)
        {
            List<string> paths = SelectedPaths();
            if (paths.Count == 0) return;
            try
            {
                string first = paths[0];
                if (paths.Count == 1 && File.Exists(PathUtil.Long(first)))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + first + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + first + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法打开资源管理器：" + ex.Message, "FileCopyWithMenu",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MiCopyPath_Click(object sender, RoutedEventArgs e)
        {
            List<string> paths = SelectedPaths();
            if (paths.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
                SetInfo("已复制 " + paths.Count + " 条路径到剪贴板");
            }
            catch
            {
                // 剪贴板偶发占用失败，忽略
            }
        }

        // ================================================================= //

        private static T FindAncestor<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                T t = d as T;
                if (t != null) return t;

                DependencyObject parent = null;
                try { parent = VisualTreeHelper.GetParent(d); }
                catch { }
                if (parent == null)
                {
                    try { parent = LogicalTreeHelper.GetParent(d); }
                    catch { }
                }
                d = parent;
            }
            return null;
        }
    }
}
