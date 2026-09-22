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
    /// <summary>一个支持多选、懒加载、全展开/全折叠的目录树面板。</summary>
    public partial class TreePanel : UserControl
    {
        private const int ExpandFolderCap = 40000;

        private readonly ObservableCollection<FileNode> _roots = new ObservableCollection<FileNode>();
        private readonly List<FileNode> _selected = new List<FileNode>();

        private FileNode _root;
        private FileNode _anchor;
        private FileNode _current;
        private CancellationTokenSource _countCts;
        private bool _bulk;
        private bool _expanding;

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
        //  载入目录
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

            _bulk = true;
            ClearSelection();
            _bulk = false;
            _countCts?.Cancel();

            _roots.Clear();
            _root = new FileNode(full, null);
            _roots.Add(_root);
            PathBox.Text = _root.FullPath;
            _current = null;
            _anchor = null;
            _root.IsExpanded = true;
            _root.LoadChildren();
            if (!string.IsNullOrEmpty(_root.LoadError))
                SetInfo("读取失败：" + _root.LoadError);
            else
                SetInfo("共 " + _root.Children.Count + " 项");
            ShowEmptyHint(false);
            return true;
        }

        /// <summary>显示/隐藏空状态提示（没有打开任何目录时给出引导文字）。</summary>
        public void ShowEmptyHint(bool show)
        {
            if (show)
            {
                EmptyHint.Text = IsSourcePanel ? "点【浏览…】选择源目录" : "点【浏览…】选择目标目录";
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
            _countCts?.Cancel();
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
            DirectoryInfo parent = Directory.GetParent(_root.FullPath);
            if (parent != null) SetRoot(parent.FullName);
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

        public async Task ExpandAllAsync()
        {
            if (_root == null || _expanding) return;
            _expanding = true;
            int loaded = 0;
            try
            {
                var queue = new Queue<FileNode>();
                queue.Enqueue(_root);
                while (queue.Count > 0)
                {
                    FileNode n = queue.Dequeue();
                    if (n.IsPlaceholder || !n.IsDirectory || n.IsLink) continue;

                    n.LoadChildren();
                    n.IsExpanded = true;
                    foreach (FileNode c in n.Children)
                        if (!c.IsPlaceholder) queue.Enqueue(c);
                    loaded++;

                    if (loaded % 80 == 0)
                    {
                        SetInfo("正在展开 … 已加载 " + loaded + " 个文件夹");
                        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    }
                    if (loaded >= ExpandFolderCap)
                    {
                        SetInfo("已展开 " + loaded + " 个文件夹，达到上限后停止");
                        _expanding = false;
                        return;
                    }
                }
                SetInfo("已展开全部 " + loaded + " 个文件夹");
            }
            finally
            {
                _expanding = false;
            }
            UpdateSelectionInfo();
        }

        public void CollapseAll()
        {
            if (_root == null) return;
            _bulk = true;
            foreach (FileNode n in VisibleNodes())
                if (n != _root && n.IsDirectory) n.IsExpanded = false;
            _bulk = false;
            // 已展开但不再可见的深层节点也一并折叠
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
            if (n != null)
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
                OnSelectionChanged();
                return;
            }
            _bulk = true;
            if (!keep) ClearSelection();
            int a = Math.Min(i1, i2), b = Math.Max(i1, i2);
            for (int i = a; i <= b; i++) AddSelection(visible[i]);
            _bulk = false;
            OnSelectionChanged();
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

            _countCts?.Cancel();

            List<string> paths = SelectedPaths();
            if (paths.Count == 0)
            {
                UpdateSelectionInfo();
                return;
            }

            var cts = new CancellationTokenSource();
            _countCts = cts;
            CancellationToken token = cts.Token;
            int n = paths.Count;

            SetInfo("已选 " + n + " 项 · 统计中 …");

            Task.Run(() =>
            {
                long dirs = 0, files = 0, bytes = 0;
                foreach (string p in paths)
                {
                    if (token.IsCancellationRequested) return;
                    try
                    {
                        if (Directory.Exists(PathUtil.Long(p)))
                        {
                            dirs++;
                            CountTree(p, ref dirs, ref files, ref bytes, token);
                        }
                        else if (File.Exists(PathUtil.Long(p)))
                        {
                            files++;
                            try { bytes += new FileInfo(PathUtil.Long(p)).Length; } catch { }
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
                        n, dirs, files, PathUtil.HumanSize(bytes)));
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
            if ((mods & ModifierKeys.Control) != 0) ToggleSelection(node);
            else if ((mods & ModifierKeys.Shift) != 0) SelectRangeTo(node, (mods & ModifierKeys.Control) != 0);
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
                if (_current != null && _current.IsDirectory && _current.IsExpanded && !_current.IsLink)
                    _current.IsExpanded = false;
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
            EnsureVisible(n);
        }

        private void EnsureVisible(FileNode node)
        {
            if (node == null) return;
            var item = Tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (item == null)
            {
                Tree.UpdateLayout();
                item = Tree.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            }
            if (item != null) item.BringIntoView();
        }

        /// <summary>展开到指定路径并选中它。</summary>
        public bool RevealPath(string target)
        {
            if (_root == null || string.IsNullOrEmpty(target)) return false;
            if (!PathUtil.IsWithin(target, _root.FullPath)) return false;

            string rel = PathUtil.RelativeTo(target, _root.FullPath);
            FileNode cur = _root;
            cur.IsExpanded = true;
            cur.LoadChildren();

            foreach (string seg in rel.Split('\\'))
            {
                if (string.IsNullOrEmpty(seg)) continue;
                FileNode next = null;
                foreach (FileNode c in cur.Children)
                {
                    if (string.Equals(c.Name, seg, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
                }
                if (next == null) break;
                next.IsExpanded = true;
                next.LoadChildren();
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
            ContextMenu menu = Tree.ContextMenu;
            if (menu != null && menu.Items.Count > 0)
            {
                var copyItem = menu.Items[0] as MenuItem;
                if (copyItem != null)
                    copyItem.Visibility = IsSourcePanel ? Visibility.Visible : Visibility.Collapsed;
            }
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

        private void MiExpandSubtree_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileNode n in SelectedNodes)
            {
                if (n.IsDirectory && !n.IsLink)
                {
                    n.IsExpanded = true;
                    ExpandRecursive(n, 0);
                }
            }
        }

        private void ExpandRecursive(FileNode n, int depth)
        {
            if (depth > 64) return;
            n.LoadChildren();
            n.IsExpanded = true;
            foreach (FileNode c in n.Children)
            {
                if (c.IsPlaceholder || !c.IsDirectory || c.IsLink) continue;
                ExpandRecursive(c, depth + 1);
            }
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
