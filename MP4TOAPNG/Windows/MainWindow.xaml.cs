using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Reflection;

namespace MP4TOAPNG;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    // 文件队列，用于存储待处理的文件
    private readonly ConcurrentQueue<string> _fileQueue = new ConcurrentQueue<string>();
    // 正在处理的文件数
    private int _processingCount = 0;
    // 总文件数
    private int _totalFiles = 0;
    // 存储每个文件的总时长（秒）
    private readonly ConcurrentDictionary<string, double> _fileDurations = new ConcurrentDictionary<string, double>();
    // 当前并行处理数
    private int _currentParallelCount = 1;
    // 用于指示是否应该继续处理文件
    private bool _shouldProcessFiles = true;
    // 存储文件信息的UI元素，方便更新
    private readonly ConcurrentDictionary<string, FileUIInfo> _fileUIInfos = new ConcurrentDictionary<string, FileUIInfo>();
    // 存储正在运行的进程，以便在需要时终止
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new ConcurrentDictionary<string, Process>();
    // 存储每个文件的取消令牌源
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _fileCancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
    // 用于保护并发数检查的锁
    private readonly object _processingLock = new object();
    // ffmpeg.exe的临时路径
    private readonly string _ffmpegPath;
    
    // 文件UI信息类
    private class FileUIInfo
    {
        public TextBlock StatusText { get; set; }
        public ProgressBar ProgressBar { get; set; }
        public TextBox OutputText { get; set; }
        public TextBlock DurationText { get; set; }
        public Button CloseButton { get; set; }
        public Border ContainerBorder { get; set; }
    }
    
    public MainWindow()
    {
        InitializeComponent();
        string[] args = Environment.GetCommandLineArgs();
        
        // 提取ffmpeg.exe到临时目录
        _ffmpegPath = ExtractFFmpeg();
        
        // 启动文件处理任务
        _ = ProcessFilesContinuously();
    }
    
    /// <summary>
    /// 从嵌入的资源中提取ffmpeg.exe到临时目录
    /// </summary>
    /// <returns>ffmpeg.exe的完整路径</returns>
    private string ExtractFFmpeg()
    {
        try
        {
            string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MP4TOAPNG");
            if (!System.IO.Directory.Exists(tempPath))
            {
                System.IO.Directory.CreateDirectory(tempPath);
            }
            
            string ffmpegPath = System.IO.Path.Combine(tempPath, "ffmpeg.exe");
            
            // 检查文件是否已存在且是最新的
            if (!System.IO.File.Exists(ffmpegPath))
            {
                // 从嵌入的资源中提取ffmpeg.exe
                using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("MP4TOAPNG.ASS.ffmpeg.exe"))
                {
                    if (resource != null)
                    {
                        using (var file = new System.IO.FileStream(ffmpegPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            resource.CopyTo(file);
                        }
                    }
                }
            }
            
            return ffmpegPath;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法提取ffmpeg.exe: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // 获取所有拖入的文件
            string[] files = (string[])((System.Array)e.Data.GetData(DataFormats.FileDrop));
            
            // 添加文件到队列
            foreach (string file in files)
            {
                _fileQueue.Enqueue(file);
                // 在UI上显示文件
                AddFileToList(file);
            }
            
            // 更新总文件数
            _totalFiles += files.Length;
        }
    }
    
    private void ParallelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParallelValueText != null)
        {
            int newParallelCount = (int)e.NewValue;
            ParallelValueText.Text = newParallelCount.ToString();
            
            // 更新当前并行数
            _currentParallelCount = newParallelCount;
        }
    }
    
    /// <summary>
    /// 持续处理文件的任务
    /// </summary>
    private async Task ProcessFilesContinuously()
    {
        while (_shouldProcessFiles)
        {
            try
            {
                // 检查是否有文件需要处理
                if (_fileQueue.IsEmpty)
                {
                    await Task.Delay(100); // 短暂等待
                    continue;
                }

                // 使用锁确保并发数检查的准确性
                bool shouldProcess = false;
                lock (_processingLock)
                {
                    if (_processingCount < _currentParallelCount)
                    {
                        _processingCount++; // 预先增加计数
                        shouldProcess = true;
                    }
                }

                // 如果不能处理更多文件，等待
                if (!shouldProcess)
                {
                    await Task.Delay(100);
                    continue;
                }

                // 检查是否还有文件需要处理
                if (!_fileQueue.TryDequeue(out string filePath))
                {
                    // 没有文件需要处理，减少计数
                    lock (_processingLock)
                    {
                        _processingCount--;
                    }
                    await Task.Delay(100);
                    continue;
                }

                // 在后台处理文件
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 处理单个文件
                        await ProcessSingleFile(filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"处理文件时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    finally
                    {
                        // 减少正在处理的文件数
                        lock (_processingLock)
                        {
                            _processingCount--;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // 出错时也要确保计数正确
                lock (_processingLock)
                {
                    _processingCount--;
                }
                
                Console.WriteLine($"处理文件队列时出错: {ex.Message}");
                await Task.Delay(1000); // 出错时等待更长时间
            }
        }
    }

    private async Task ProcessSingleFile(string filePath)
    {
        // 更新UI显示正在处理
        UpdateFileStatus(filePath, "处理中...");
        
        try
        {
            // 处理单个文件
            bool success = await TurnVideoAsync(filePath);
            
            // 更新UI显示处理结果
            UpdateFileStatus(filePath, success ? "完成" : "失败");
        }
        catch (Exception ex)
        {
            UpdateFileStatus(filePath, $"错误: {ex.Message}");
        }
    }

    private async Task<bool> TurnVideoAsync(string path)
    {
        Process p = null;
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        // 将取消令牌源添加到字典中
        _fileCancellationTokens.TryAdd(path, cancellationTokenSource);
        
        try
        {
            string outputFileName = System.IO.Path.GetFileNameWithoutExtension(path) + ".png";
            string strInput =
                $"-hwaccel opencl -hide_banner -y -i \"{path}\" -vf fps=60,scale=-1:-1:flags=lanczos -plays 0 -f apng \"{outputFileName}\"";

            // 直接启动 ffmpeg 进程而不是 cmd.exe
            p = new Process();
            p.StartInfo.FileName = _ffmpegPath; // 使用提取的ffmpeg路径
            p.StartInfo.Arguments = strInput;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardInput = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            
            // 设置为子进程，主进程退出时自动终止子进程
            p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;

            // 存储进程引用，以便在需要时终止
            _runningProcesses.TryAdd(path, p);

            // 启动进程
            p.Start();
            
            // 设置进程优先级
            try
            {
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // 忽略权限不足的异常
            }

            // 异步读取错误输出（ffmpeg的进度信息通常在stderr）
            string line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                // 检查是否应该继续处理或已被取消
                if (cancellationTokenSource.Token.IsCancellationRequested)
                {
                    // 直接强制终止进程
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill();
                            await p.WaitForExitAsync();
                        }
                    }
                    catch
                    {
                        // 忽略终止进程时的异常
                    }
                    UpdateFileStatus(path, "已取消");
                    return false;
                }
                
                // 将输出添加到对应文件的文本框
                UpdateFileOutput(path, line);
                
                // 解析帧信息以更新进度条
                ParseFrameInfo(path, line);
            }

            // 等待进程完成
            await p.WaitForExitAsync();

            // 根据进程退出代码更新状态
            bool success = p.ExitCode == 0;
            if (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                UpdateFileStatus(path, success ? "完成" : "失败");
            }
            else
            {
                UpdateFileStatus(path, "已取消");
            }
            
            return success && !cancellationTokenSource.Token.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            // 检查是否是由于取消操作导致的异常
            if (cancellationTokenSource.Token.IsCancellationRequested)
            {
                UpdateFileStatus(path, "已取消");
                return false;
            }
            
            // 记录异常信息
            UpdateFileOutput(path, $"处理过程中发生异常: {ex.Message}");
            UpdateFileStatus(path, "失败");
            return false;
        }
        finally
        {
            // 移除进程引用
            if (p != null)
            {
                _runningProcesses.TryRemove(path, out _);
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                    }
                    p.Dispose();
                }
                catch
                {
                    // 忽略dispose时的异常
                }
            }
            
            // 从字典中移除取消令牌源
            _fileCancellationTokens.TryRemove(path, out _);
            cancellationTokenSource.Dispose();
        }
    }
    
    private void AddFileToList(string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            // 创建文件项的UI元素
            Border border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["小黑绿"],
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(5)
            };

            // 创建相对布局容器
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            
            StackPanel panel = new StackPanel();
            
            // 创建标题行容器
            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(0, GridUnitType.Auto) });
            
            // 文件名
            TextBlock fileNameText = new TextBlock
            {
                Text = System.IO.Path.GetFileName(filePath),
                Foreground = (Brush)Application.Current.Resources["小黑绿"],
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            
            // 关闭按钮
            Button closeButton = new Button
            {
                Content = "×",
                Width = 20,
                Height = 20,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["小黑绿"],
                Background = Brushes.Transparent,
                BorderBrush = (Brush)Application.Current.Resources["小黑绿"],
                BorderThickness = new Thickness(1),
                Style = (Style)Application.Current.Resources["HEIButtonStyle"]
            };
            closeButton.Click += (s, e) => RemoveFileCard(filePath);
            
            Grid.SetColumn(fileNameText, 0);
            Grid.SetColumn(closeButton, 1);
            headerGrid.Children.Add(fileNameText);
            headerGrid.Children.Add(closeButton);
            panel.Children.Add(headerGrid);
            
            // 视频时长
            TextBlock durationText = new TextBlock
            {
                Text = "时长: 未知",
                Foreground = Brushes.Gray,
                Name = $"Duration_{GetSafeName(filePath)}",
                Margin = new Thickness(0, 0, 0, 2)
            };
            
            // 状态
            TextBlock statusText = new TextBlock
            {
                Text = "等待中...",
                Foreground = Brushes.Gray,
                Name = $"Status_{GetSafeName(filePath)}",
                Margin = new Thickness(0, 0, 0, 2)
            };
            
            // 进度条
            ProgressBar fileProgressBar = new ProgressBar
            {
                Style = (Style)Application.Current.Resources["HEIProgressBarStyle"],
                Height = 16,
                Margin = new Thickness(0, 0, 0, 2),
                Name = $"Progress_{GetSafeName(filePath)}",
                Value = 0
            };
            
            // 输出信息
            TextBox outputText = new TextBox
            {
                Style = (Style)Application.Current.Resources["HEITextBoxStyle"],
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 100,
                FontSize = 10,
                Name = $"Output_{GetSafeName(filePath)}"
            };

            panel.Children.Add(durationText);
            panel.Children.Add(statusText);
            panel.Children.Add(fileProgressBar);
            panel.Children.Add(outputText);
            
            grid.Children.Add(panel);
            border.Child = grid;

            FileListPanel.Children.Add(border);
            
            // 存储UI元素引用以便后续更新
            _fileUIInfos.TryAdd(filePath, new FileUIInfo
            {
                StatusText = statusText,
                ProgressBar = fileProgressBar,
                OutputText = outputText,
                DurationText = durationText,
                CloseButton = closeButton,
                ContainerBorder = border
            });
        });
        
        // 启动获取视频时长的操作
        _ = GetVideoDurationAsync(filePath);
    }
    
    private async Task GetVideoDurationAsync(string filePath)
    {
        try
        {
            // 使用ffmpeg获取视频信息
            Process p = new Process();
            p.StartInfo.FileName = _ffmpegPath; // 使用提取的ffmpeg路径
            p.StartInfo.Arguments = $"-i \"{filePath}\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            
            p.Start();
            
            // 读取错误输出（ffmpeg将信息输出到stderr）
            string output = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            
            // 解析时长信息
            var durationMatch = Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
            if (durationMatch.Success)
            {
                int hours = int.Parse(durationMatch.Groups[1].Value);
                int minutes = int.Parse(durationMatch.Groups[2].Value);
                double seconds = double.Parse(durationMatch.Groups[3].Value);
                double totalSeconds = hours * 3600 + minutes * 60 + seconds;
                
                // 存储文件总时长
                _fileDurations.TryAdd(filePath, totalSeconds);
                
                // 更新UI显示时长
                UpdateFileDuration(filePath, totalSeconds);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取视频时长时出错: {ex.Message}");
            // 即使出错也继续执行，不影响主要功能
        }
    }
    
    /// <summary>
    /// 移除文件卡片并终止相关进程（如果正在运行）
    /// </summary>
    /// <param name="filePath">文件路径</param>
    private void RemoveFileCard(string filePath)
    {
        // 请求取消该文件的处理
        if (_fileCancellationTokens.TryGetValue(filePath, out CancellationTokenSource cancellationTokenSource))
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch
            {
                // 忽略取消时的异常
            }
        }
        
        // 如果有正在运行的进程，终止它
        if (_runningProcesses.TryGetValue(filePath, out Process process))
        {
            try
            {
                if (!process.HasExited)
                {
                    // 直接强制终止进程
                    process.Kill();
                    process.WaitForExit(3000); // 等待3秒确保进程被杀死
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"终止进程时出错: {ex.Message}");
                
                // 如果Kill失败，尝试通过进程ID查找并终止所有相关的ffmpeg进程
                try
                {
                    // 查找所有ffmpeg进程并终止它们
                    Process[] ffmpegProcesses = Process.GetProcessesByName("ffmpeg");
                    foreach (Process ffmpegProcess in ffmpegProcesses)
                    {
                        try
                        {
                            // 简单地尝试终止所有ffmpeg进程（在生产环境中应该有更好的方法来识别相关进程）
                            ffmpegProcess.Kill();
                            ffmpegProcess.WaitForExit(2000);
                        }
                        catch
                        {
                            // 忽略单个进程终止失败
                        }
                    }
                }
                catch
                {
                    // 忽略查找和终止进程时的异常
                }
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // 忽略dispose时的异常
                }
                _runningProcesses.TryRemove(filePath, out _);
            }
        }

        // 移除UI元素
        if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo uiInfo))
        {
            Dispatcher.Invoke(() =>
            {
                FileListPanel.Children.Remove(uiInfo.ContainerBorder);
            });
            _fileUIInfos.TryRemove(filePath, out _);
        }

        // 从队列中移除文件（如果还在队列中）
        var tempQueue = new ConcurrentQueue<string>();
        string file;
        while (_fileQueue.TryDequeue(out file))
        {
            if (file != filePath)
            {
                tempQueue.Enqueue(file);
            }
        }

        // 将剩余的文件放回队列
        while (tempQueue.TryDequeue(out file))
        {
            _fileQueue.Enqueue(file);
        }

        // 减少处理计数（如果该文件正在处理中）
        lock (_processingLock)
        {
            // 检查该文件是否正在处理中（通过检查UI状态）
            if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo info) && 
                info.StatusText.Text == "处理中...")
            {
                _processingCount = Math.Max(0, _processingCount - 1);
            }
        }
        
        // 移除取消令牌源
        if (_fileCancellationTokens.TryRemove(filePath, out CancellationTokenSource cts))
        {
            try
            {
                cts.Dispose();
            }
            catch
            {
                // 忽略dispose时的异常
            }
        }
    }
    
    private void UpdateFileStatus(string filePath, string status)
    {
        Dispatcher.Invoke(() =>
        {
            if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo uiInfo))
            {
                uiInfo.StatusText.Text = status;
                
                // 根据状态设置颜色
                switch (status)
                {
                    case "等待中...":
                        uiInfo.StatusText.Foreground = Brushes.Gray;
                        break;
                    case "处理中...":
                        uiInfo.StatusText.Foreground = Brushes.Yellow;
                        break;
                    case "完成":
                        uiInfo.StatusText.Foreground = Brushes.Green;
                        break;
                    case "失败":
                    default:
                        if (status.StartsWith("错误:"))
                            uiInfo.StatusText.Foreground = Brushes.Red;
                        else
                            uiInfo.StatusText.Foreground = Brushes.Gray;
                        break;
                }
            }
        });
    }
    
    private void UpdateFileOutput(string filePath, string output)
    {
        Dispatcher.Invoke(() =>
        {
            if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo uiInfo))
            {
                uiInfo.OutputText.AppendText(output + Environment.NewLine);
                uiInfo.OutputText.ScrollToEnd();
            }
        });
    }
    
    private void UpdateFileProgress(string filePath, double progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo uiInfo))
            {
                uiInfo.ProgressBar.Value = Math.Min(progress, 100);
            }
        });
    }
    
    private void UpdateFileDuration(string filePath, double durationSeconds)
    {
        Dispatcher.Invoke(() =>
        {
            if (_fileUIInfos.TryGetValue(filePath, out FileUIInfo uiInfo))
            {
                // 格式化时长显示
                TimeSpan duration = TimeSpan.FromSeconds(durationSeconds);
                string durationText = $"时长: {duration:mm\\:ss}";
                if (duration.Hours > 0)
                {
                    durationText = $"时长: {duration:h\\:mm\\:ss}";
                }
                
                uiInfo.DurationText.Text = durationText;
            }
        });
    }
    
    private void ParseFrameInfo(string filePath, string data)
    {
        // 提取视频总时长
        var durationMatch = Regex.Match(data, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
        if (durationMatch.Success)
        {
            int hours = int.Parse(durationMatch.Groups[1].Value);
            int minutes = int.Parse(durationMatch.Groups[2].Value);
            double seconds = double.Parse(durationMatch.Groups[3].Value);
            double totalSeconds = hours * 3600 + minutes * 60 + seconds;
            
            // 存储文件总时长
            _fileDurations.TryAdd(filePath, totalSeconds);
            
            // 更新UI显示时长
            UpdateFileDuration(filePath, totalSeconds);
        }
        
        // 匹帧数信息，例如: frame=   81 fps=5.2 q=-0.0 Lsize=   78000KiB time=00:00:01.35 bitrate=473314.5kbits/s speed=0.0866x
        if (data.Contains("time="))
        {
            // 使用正则表达式提取时间
            var match = Regex.Match(data, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
            if (match.Success)
            {
                int hours = int.Parse(match.Groups[1].Value);
                int minutes = int.Parse(match.Groups[2].Value);
                double seconds = double.Parse(match.Groups[3].Value);
                double currentSeconds = hours * 3600 + minutes * 60 + seconds;
                
                // 计算进度百分比
                if (_fileDurations.TryGetValue(filePath, out double totalSeconds) && totalSeconds > 0)
                {
                    double progress = (currentSeconds / totalSeconds) * 100;
                    UpdateFileProgress(filePath, progress);
                }
            }
        }
    }
    
    // 工具方法：获取文件路径的安全名称（用于控件命名）
    private string GetSafeName(string filePath)
    {
        return System.IO.Path.GetFileNameWithoutExtension(filePath)
            .Replace(" ", "_")
            .Replace(".", "_")
            .Replace("-", "_");
    }
    
    // 窗口关闭时清理资源
    protected override void OnClosed(EventArgs e)
    {
        // 停止处理新文件
        _shouldProcessFiles = false;

        // 取消所有正在进行的任务
        foreach (var kvp in _fileCancellationTokens)
        {
            try
            {
                kvp.Value.Cancel();
            }
            catch
            {
                // 忽略取消时的异常
            }
        }
        
        // 清理临时文件
        try
        {
            if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(_ffmpegPath)))
            {
                System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(_ffmpegPath), true);
            }
        }
        catch
        {
            // 忽略删除临时文件时的异常
        }

        base.OnClosed(e);
    }
}
