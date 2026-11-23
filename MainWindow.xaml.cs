using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FileCopyWithMenu.Core;

namespace FileCopyWithMenu
{
    /// <summary>日志行。</summary>
    public sealed class LogEntry
    {
        public string Display { get; set; }
        public bool IsError { get; set; }
        public bool IsOk { get; set; }
    }

    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 3000;

        private readonly ObservableCollection<LogEntry> _log = new ObservableCollection<LogEntry>();
        private ICollectionView _logView;
        private CancellationTokenSource _cts;
        private Task _copyTask;
        private bool _busy;

        private List<string> _lastFailedSources = new List<string>();
        private string _lastFailedLeftRoot;
        private string _lastFailedRightRoot;

        public MainWindow()
        {
            InitializeComponent();
            _logView = CollectionViewSource.GetDefaultView(_log);
            _logView.Filter = FilterLogEntry;
            LogList.ItemsSource = _logView;

            LeftPanel.CopyRequested += (s, e) => { _ = RunCopyAsync(); };
            LeftPanel.SelectionChanged += (s, e) => UpdateSelectionSummary();
        }

        // ================================================================= //

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LeftPanel.PanelTitle = "① 源目录（左侧）";
            RightPanel.PanelTitle = "② 目标目录（右侧）";

            // 本程序不做任何记忆：启动时两侧都是空的，均不预加载、不预选任何目录。
            // 窗口尺寸/位置、冲突策略、各复选框也都使用 XAML 中定义的默认值，不读上次的状态。
            LeftPanel.ClearRoot();
            RightPanel.ClearRoot();
            UpdateSelectionSummary();

            Log("就绪。请先点左侧【浏览…】或直接拖入文件夹作为源目录，选中后点中间 → 复制到右侧。");
            Log("说明：选中文件夹会连同其下所有子文件夹与文件一起复制，并保留相对目录结构。");
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_busy) return;

            MessageBoxResult r = MessageBox.Show(
                "正在复制，确定要中止并退出吗？", "FileCopyWithMenu",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            try { _cts?.Cancel(); } catch { }
            // 给流式复制留出短暂时间清理临时文件，避免强制终止残留 .~fcwm 文件
            try { _copyTask?.Wait(1500); } catch { }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _ = RunCopyAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5 && !_busy)
            {
                LeftPanel.Refresh();
                RightPanel.Refresh();
                e.Handled = true;
            }
            base.OnPreviewKeyDown(e);
        }

        // ================================================================= //
        //  复制
        // ================================================================= //

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            _ = RunCopyAsync();
        }

        private void RetryFailed_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _lastFailedSources.Count == 0) return;
            var retrySources = new List<string>(_lastFailedSources);
            _ = RunCopyAsync(retrySources, _lastFailedLeftRoot, _lastFailedRightRoot);
        }

        private void Swap_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string left = LeftPanel.RootPath;
            string right = RightPanel.RootPath;
            if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return;

            if (string.IsNullOrEmpty(right)) LeftPanel.ClearRoot();
            else LeftPanel.SetRoot(right, true);

            if (string.IsNullOrEmpty(left)) RightPanel.ClearRoot();
            else RightPanel.SetRoot(left, true);

            UpdateSelectionSummary();
        }

        private async Task RunCopyAsync(List<string> overrideSources = null, string overrideLeft = null, string overrideRight = null)
        {
            if (_busy) return;

            string left = overrideLeft ?? LeftPanel.RootPath;
            string right = overrideRight ?? RightPanel.RootPath;

            if (string.IsNullOrEmpty(left))
            {
                MessageBox.Show("请先在左侧指定源目录。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(right))
            {
                MessageBox.Show("请先在右侧指定目标目录。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (PathUtil.IsSamePath(left, right))
            {
                MessageBox.Show("源目录与目标目录相同，无需复制。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> sources = overrideSources != null
                ? PathUtil.NormalizeSources(overrideSources)
                : PathUtil.NormalizeSources(LeftPanel.SelectedPaths());

            if (sources.Count == 0)
            {
                MessageBox.Show("请先在左侧选择要复制的文件或文件夹。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 仅当目标目录实际位于本次选中的源项内部时才弹嵌套警告
            if (sources.Any(s => PathUtil.IsWithin(right, s)))
            {
                MessageBoxResult r = MessageBox.Show(
                    "目标目录位于所选源项内部，可能产生重复嵌套。\n是否继续？",
                    "FileCopyWithMenu", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            var options = new CopyOptions
            {
                Policy = IndexToPolicyValue(PolicyBox.SelectedIndex),
                IncludeEmptyDirectories = ChkEmpty.IsChecked == true,
                KeepTimestamps = ChkMeta.IsChecked == true
            };

            var cts = new CancellationTokenSource();
            _cts = cts;
            SetBusy(true);
            ProgressBar.Value = 0;
            ProgressText.Text = "准备中 …";

            Log("————————————————————————————————————————");
            Log((overrideSources != null ? "[重试失败项] " : "") + "源目录：  " + left);
            Log("目标目录：" + right);
            Log(string.Format("选中 {0} 项；冲突策略={1}；复制空文件夹={2}；保留时间戳={3}",
                sources.Count, options.PolicyText,
                options.IncludeEmptyDirectories ? "是" : "否",
                options.KeepTimestamps ? "是" : "否"));

            var progress = new Progress<CopyMessage>(OnCopyMessage);
            CopyResult result = null;

            try
            {
                Task<CopyResult> task = Task.Run(() =>
                    CopyEngine.Run(sources, left, right, options, progress, cts.Token));
                _copyTask = task;
                result = await task;
            }
            catch (OperationCanceledException)
            {
                Log("复制已取消。");
            }
            catch (Exception ex)
            {
                Log("发生未预期的错误：" + ex.Message, isError: true);
            }
            finally
            {
                _copyTask = null;
                if (_cts == cts) _cts = null;
                cts.Dispose();
            }

            try
            {
                if (result != null)
                {
                    FinishCopy(result, left, right);
                }
                else
                {
                    ProgressText.Text = "已取消";
                    StatusText.Text = "已取消";
                }
            }
            catch (Exception ex)
            {
                Log("收尾处理出错：" + ex.Message, isError: true);
            }
            finally
            {
                SetBusy(false);
                RetryButton.IsEnabled = _lastFailedSources.Count > 0;
            }
        }

        private void OnCopyMessage(CopyMessage m)
        {
            if (m.Kind == CopyMessageKind.Log)
            {
                Log(m.Text, m.IsError, m.IsOk);
            }
            else if (m.Kind == CopyMessageKind.Progress && m.Progress != null)
            {
                ProgressBar.Value = m.Progress.Percent;
                ProgressText.Text = m.Progress.Message;
                StatusText.Text = m.Progress.Message;
            }
        }

        private void FinishCopy(CopyResult r, string leftRoot, string rightRoot)
        {
            if (!r.Cancelled)
                ProgressBar.Value = 100;

            string head = r.Cancelled ? "已取消，" : "";
            ProgressText.Text = string.Format("{0}成功 {1} / 跳过 {2} / 失败 {3}，用时 {4:0.0}s",
                head, r.Copied, r.Skipped, r.Failed, r.Elapsed.TotalSeconds);
            StatusText.Text = ProgressText.Text;

            Log(string.Format("{0}完成：成功 {1} 个文件（{2}），跳过 {3}，失败 {4}，新建目录 {5}，用时 {6:0.0}s",
                head, r.Copied, PathUtil.HumanSize(r.Bytes), r.Skipped, r.Failed, r.DirsCreated, r.Elapsed.TotalSeconds),
                isError: r.Failed > 0,
                isOk: !r.Cancelled && r.Failed == 0);

            _lastFailedSources = r.FailedSources != null ? new List<string>(r.FailedSources) : new List<string>();
            _lastFailedLeftRoot = leftRoot;
            _lastFailedRightRoot = rightRoot;

            if (ChkRefresh.IsChecked == true && !r.Cancelled && r.FirstTargets.Count > 0 &&
                RightPanel.RootPath != null && PathUtil.IsSamePath(RightPanel.RootPath, rightRoot))
            {
                RightPanel.Refresh();
                if (!RightPanel.RevealPath(r.FirstTargets[0]))
                    RightPanel.RevealPath(Path.GetDirectoryName(r.FirstTargets[0]));
            }

            if (r.Failed > 0)
            {
                MessageBox.Show(
                    string.Format("复制结束，但有 {0} 个文件失败，详见日志（可点击“重试失败项”补传）。\n成功 {1}，跳过 {2}。",
                        r.Failed, r.Copied, r.Skipped),
                    "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy || _cts == null) return;
            try { _cts.Cancel(); } catch { }
            CancelButton.IsEnabled = false;
            Log("正在取消 …");
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            CopyButton.IsEnabled = !busy;
            SwapButton.IsEnabled = !busy;
            RetryButton.IsEnabled = !busy && _lastFailedSources.Count > 0;
            PolicyBox.IsEnabled = !busy;
            ChkEmpty.IsEnabled = !busy;
            ChkMeta.IsEnabled = !busy;
            LeftPanel.IsEnabled = !busy;
            RightPanel.IsEnabled = !busy;
            CancelButton.IsEnabled = busy;
            Cursor = busy ? Cursors.Wait : null;
            if (busy) StatusText.Text = "正在复制 …";
        }

        private void UpdateSelectionSummary()
        {
            int n = LeftPanel.SelectedPaths().Count;
            SelCountText.Text = n == 0 ? "未选择" : "已选 " + n + " 项";
        }

        // ================================================================= //
        //  日志与其它
        // ================================================================= //

        private bool FilterLogEntry(object obj)
        {
            if (ChkErrorOnly == null || ChkErrorOnly.IsChecked != true) return true;
            var entry = obj as LogEntry;
            return entry != null && entry.IsError;
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e)
        {
            _logView?.Refresh();
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _log.Clear();
        }

        private void OpenTarget_Click(object sender, RoutedEventArgs e)
        {
            string path = RightPanel.RootPath;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(PathUtil.Long(path)))
            {
                MessageBox.Show("目标目录不存在。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("打开目录失败：" + ex.Message, isError: true);
            }
        }

        private void OpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            string logPath = AppPaths.ErrorLog;
            try
            {
                if (File.Exists(logPath))
                {
                    Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
                }
                else
                {
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.DataDirectory + "\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Log("打开日志失败：" + ex.Message, isError: true);
            }
        }

        private void Log(string text, bool isError = false, bool isOk = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(text, isError, isOk)));
                return;
            }

            if (_log.Count >= MaxLogLines) _log.RemoveAt(0);
            var entry = new LogEntry
            {
                Display = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text,
                IsError = isError,
                IsOk = !isError && isOk
            };
            _log.Add(entry);
            try
            {
                if (ChkErrorOnly == null || ChkErrorOnly.IsChecked != true || entry.IsError)
                    LogList.ScrollIntoView(entry);
            }
            catch { }
        }

        private static ConflictPolicy IndexToPolicyValue(int index)
        {
            switch (index)
            {
                case 1: return ConflictPolicy.Skip;
                case 2: return ConflictPolicy.Rename;
                case 3: return ConflictPolicy.OverwriteIfNewer;
                default: return ConflictPolicy.Overwrite;
            }
        }
    }
}
