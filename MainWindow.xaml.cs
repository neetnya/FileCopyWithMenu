using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        private CancellationTokenSource _cts;
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _log;

            LeftPanel.CopyRequested += (s, e) => { var _ = RunCopyAsync(); };
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

            Log("就绪。请先点左侧【浏览…】选择源目录，选中文件或文件夹（可多选）后，点中间的 → 复制到右侧。");
            Log("说明：选中文件夹会连同其下所有子文件夹与文件一起复制，并保留相对目录结构。");
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_busy) return;

            MessageBoxResult r = MessageBox.Show(
                "正在复制，确定要退出吗？", "FileCopyWithMenu",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (_cts != null) _cts.Cancel();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
            {
                var _ = RunCopyAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
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
            var _ = RunCopyAsync();
        }

        private async Task RunCopyAsync()
        {
            if (_busy) return;

            string left = LeftPanel.RootPath;
            string right = RightPanel.RootPath;

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
            if (PathUtil.IsWithin(right, left) && string.Equals(
                    PathUtil.Long(left).TrimEnd('\\'), PathUtil.Long(right).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("源目录与目标目录相同，无需复制。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> sources = PathUtil.NormalizeSources(LeftPanel.SelectedPaths());
            if (sources.Count == 0)
            {
                MessageBox.Show("请先在左侧选择要复制的文件或文件夹。", "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (PathUtil.IsWithin(right, left))
            {
                MessageBoxResult r = MessageBox.Show(
                    "目标目录位于所选源目录内部，可能产生重复嵌套。\n是否继续？",
                    "FileCopyWithMenu", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            var options = new CopyOptions
            {
                Policy = IndexToPolicyValue(PolicyBox.SelectedIndex),
                IncludeEmptyDirectories = ChkEmpty.IsChecked == true,
                KeepTimestamps = ChkMeta.IsChecked == true
            };

            _cts = new CancellationTokenSource();
            SetBusy(true);
            ProgressBar.Value = 0;
            ProgressText.Text = "准备中 …";

            Log("————————————————————————————————————————");
            Log("源目录：  " + left);
            Log("目标目录：" + right);
            Log(string.Format("选中 {0} 项；冲突策略={1}；复制空文件夹={2}；保留时间戳={3}",
                sources.Count, options.PolicyText,
                options.IncludeEmptyDirectories ? "是" : "否",
                options.KeepTimestamps ? "是" : "否"));

            var progress = new Progress<CopyMessage>(OnCopyMessage);
            CopyResult result = null;

            try
            {
                result = await Task.Run(() =>
                    CopyEngine.Run(sources, left, right, options, progress, _cts.Token), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("复制已取消。");
            }
            catch (Exception ex)
            {
                Log("发生未预期的错误：" + ex.Message, true);
            }

            if (result != null)
            {
                FinishCopy(result);
            }
            else
            {
                ProgressText.Text = "已取消";
                StatusText.Text = "已取消";
            }

            SetBusy(false);
        }

        private void OnCopyMessage(CopyMessage m)
        {
            if (m.Kind == CopyMessageKind.Log)
            {
                Log(m.Text, m.IsError);
            }
            else if (m.Kind == CopyMessageKind.Progress && m.Progress != null)
            {
                ProgressBar.Value = m.Progress.Percent;
                ProgressText.Text = m.Progress.Message;
                StatusText.Text = m.Progress.Message;
            }
        }

        private void FinishCopy(CopyResult r)
        {
            ProgressBar.Value = 100;
            string head = r.Cancelled ? "已取消，" : "";
            ProgressText.Text = string.Format("{0}成功 {1} / 跳过 {2} / 失败 {3}，用时 {4:0.0}s",
                head, r.Copied, r.Skipped, r.Failed, r.Elapsed.TotalSeconds);
            StatusText.Text = ProgressText.Text;

            Log(string.Format("{0}完成：成功 {1} 个文件（{2}），跳过 {3}，失败 {4}，新建目录 {5}，用时 {6:0.0}s",
                head, r.Copied, PathUtil.HumanSize(r.Bytes), r.Skipped, r.Failed, r.DirsCreated, r.Elapsed.TotalSeconds),
                r.Failed > 0);

            if (ChkRefresh.IsChecked == true && !r.Cancelled && r.FirstTargets.Count > 0 && RightPanel.RootPath != null)
            {
                RightPanel.Refresh();
                if (!RightPanel.RevealPath(r.FirstTargets[0]))
                    RightPanel.RevealPath(Path.GetDirectoryName(r.FirstTargets[0]));
            }

            if (r.Failed > 0)
            {
                MessageBox.Show(
                    string.Format("复制结束，但有 {0} 个文件失败，详见日志。\n成功 {1}，跳过 {2}。",
                        r.Failed, r.Copied, r.Skipped),
                    "FileCopyWithMenu", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_busy || _cts == null) return;
            _cts.Cancel();
            CancelButton.IsEnabled = false;
            Log("正在取消 …");
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            CopyButton.IsEnabled = !busy;
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
        //  其它
        // ================================================================= //

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            _log.Clear();
        }

        private void OpenTarget_Click(object sender, RoutedEventArgs e)
        {
            string path = RightPanel.RootPath;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
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
                Log("打开目录失败：" + ex.Message, true);
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
                Log("打开日志失败：" + ex.Message, true);
            }
        }

        private void Log(string text, bool isError = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Log(text, isError)));
                return;
            }

            if (_log.Count >= MaxLogLines) _log.RemoveAt(0);
            var entry = new LogEntry
            {
                Display = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text,
                IsError = isError,
                IsOk = !isError && (text.Contains("完成") || text.Contains("成功"))
            };
            _log.Add(entry);
            try { LogList.ScrollIntoView(entry); } catch { }
        }

        private static ConflictPolicy IndexToPolicyValue(int index)
        {
            switch (index)
            {
                case 1: return ConflictPolicy.Skip;
                case 2: return ConflictPolicy.Rename;
                default: return ConflictPolicy.Overwrite;
            }
        }
    }
}
