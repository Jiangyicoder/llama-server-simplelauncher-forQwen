using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace QwenTrayLauncher;

internal static class Program
{
    private const string MutexName = @"Local\Qwen3_8_27B_TrayLauncher";
    private const string ExitEventName = @"Local\Qwen3_8_27B_TrayLauncher_Exit";

    [STAThread]
    private static void Main(string[] arguments)
    {
        if (arguments.Any(argument =>
                string.Equals(argument, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            SignalExistingInstanceToExit();
            return;
        }

        ApplicationConfiguration.Initialize();

        using var instanceMutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Qwen3.8 27B 托盘启动器已经在运行。",
                "Qwen3.8 27B",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var exitSignal = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ExitEventName);
            using var context = new TrayApplicationContext(exitSignal);
            Application.Run(context);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"启动模型失败：{exception.Message}\n\n详情已写入 qwen3.8-tray.log。",
                "Qwen3.8 27B",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex is released automatically if startup failed early.
            }
        }
    }

    private static void SignalExistingInstanceToExit()
    {
        try
        {
            using var exitSignal = EventWaitHandle.OpenExisting(ExitEventName);
            exitSignal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No tray instance is currently running.
        }
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ArgumentsFileName = "llama-server.args";
    private static readonly Uri HealthEndpoint = new("http://127.0.0.1:1234/health");
    private static readonly Uri WebEndpoint = new("http://127.0.0.1:1234/");

    private readonly string _baseDirectory = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);
    private readonly string _logPath;
    private readonly object _logLock = new();
    private readonly EventWaitHandle _exitSignal;
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openWebItem;
    private readonly ToolStripMenuItem _openLogItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private readonly HttpClient _httpClient;

    private StreamWriter? _logWriter;
    private Process? _serverProcess;
    private IntPtr _jobHandle = IntPtr.Zero;
    private bool _serverAssignedToJob;
    private bool _ready;
    private bool _stopping;
    private bool _shutdownComplete;
    private bool _exitNoticeShown;

    public TrayApplicationContext(EventWaitHandle exitSignal)
    {
        _exitSignal = exitSignal;
        _logPath = Path.Combine(_baseDirectory, "qwen3.8-tray.log");
        InitializeLog();

        _statusItem = new ToolStripMenuItem("状态：正在加载模型")
        {
            Enabled = false,
        };
        _openWebItem = new ToolStripMenuItem("打开 Web 界面", null, (_, _) => OpenWebInterface())
        {
            Enabled = false,
        };
        _openLogItem = new ToolStripMenuItem("查看启动日志", null, (_, _) => OpenLog());
        _exitItem = new ToolStripMenuItem("退出并关闭模型", null, (_, _) => RequestExit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openWebItem);
        menu.Items.Add(_openLogItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _applicationIcon = LoadApplicationIcon(out var iconSource);
        WriteLog($"[launcher] 托盘图标来源：{iconSource}。");
        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _applicationIcon,
            Text = "Qwen3.8 27B - 正在加载",
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => OpenWebInterface();

        _httpClient = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(1),
        };

        _statusTimer = new System.Windows.Forms.Timer
        {
            Interval = 2000,
        };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();

        try
        {
            StartServer();
            _statusTimer.Start();
        }
        catch (Exception exception)
        {
            WriteLog($"[launcher] 启动失败：{exception}");
            _trayIcon.Visible = false;
            ShutdownServer();
            throw;
        }
    }

    private static Icon LoadApplicationIcon(out string source)
    {
        const string resourceName = "QwenTrayLauncher.qwen-color.ico";

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                using var embeddedIcon = new Icon(stream);
                source = "内嵌 qwen-color.ico";
                return (Icon)embeddedIcon.Clone();
            }

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                using var executableIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (executableIcon is not null)
                {
                    source = "EXE 图标资源";
                    return (Icon)executableIcon.Clone();
                }
            }
        }
        catch
        {
            // Fall back to a system icon only if the embedded resource cannot be read.
        }

        source = "Windows 默认图标（回退）";
        return (Icon)SystemIcons.Application.Clone();
    }

    private void InitializeLog()
    {
        try
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 10 * 1024 * 1024)
            {
                File.Move(_logPath, _logPath + ".previous", true);
            }

            _logWriter = new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            WriteLog(string.Empty);
            WriteLog($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 托盘启动器启动 =====");
        }
        catch
        {
            _logWriter = null;
        }
    }

    private void StartServer()
    {
        var serverPath = ResolveServerPath();
        var modelPath = Path.Combine(
            _baseDirectory,
            "Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-NVFP4-mixed.gguf");
        var draftModelPath = Path.Combine(
            _baseDirectory,
            "Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-FastMTP-32K.gguf");
        var mmprojPath = Path.Combine(
            _baseDirectory,
            "mmproj-Qwen3.8-27B-Uncensored-HauhauCS-Aggressive-BF16.gguf");

        EnsureFileExists(serverPath, "llama-server.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            WorkingDirectory = _baseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var argumentsPath = Path.Combine(_baseDirectory, ArgumentsFileName);
        if (File.Exists(argumentsPath))
        {
            AddExternalArguments(startInfo.ArgumentList, argumentsPath);
            WriteLog($"[launcher] 检测到外部参数文件，使用外部参数：{argumentsPath}。");
        }
        else
        {
            EnsureFileExists(modelPath, "主模型");
            EnsureFileExists(draftModelPath, "MTP 草稿模型");
            EnsureFileExists(mmprojPath, "多模态投影模型");
            AddBuiltInArguments(startInfo.ArgumentList, modelPath, draftModelPath, mmprojPath);
            WriteLog("[launcher] 未找到外部参数文件，使用内置 llama-server 参数。");
        }

        _serverProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        _serverProcess.OutputDataReceived += (_, eventArgs) => WriteServerLine(eventArgs.Data);
        _serverProcess.ErrorDataReceived += (_, eventArgs) => WriteServerLine(eventArgs.Data);
        _serverProcess.Exited += (_, _) =>
        {
            try
            {
                WriteLog($"[launcher] llama-server 已退出，退出码 {_serverProcess.ExitCode}。");
            }
            catch
            {
                WriteLog("[launcher] llama-server 已退出。");
            }
        };

        _jobHandle = NativeJob.CreateKillOnCloseJob();
        if (!_serverProcess.Start())
        {
            throw new InvalidOperationException("Windows 未能启动 llama-server.exe。");
        }

        try
        {
            if (_jobHandle == IntPtr.Zero || !NativeJob.AssignProcess(_jobHandle, _serverProcess.Handle))
            {
                WriteLog(
                    $"[launcher] 警告：无法加入 Windows 作业对象，退出时将改用进程树终止。Win32={Marshal.GetLastWin32Error()}");
            }
            else
            {
                _serverAssignedToJob = true;
            }
        }
        catch (Exception exception)
        {
            WriteLog($"[launcher] 警告：设置进程树保护失败：{exception.Message}");
        }

        _serverProcess.BeginOutputReadLine();
        _serverProcess.BeginErrorReadLine();
        WriteLog($"[launcher] llama-server 已启动，PID={_serverProcess.Id}。");
    }

    private string ResolveServerPath()
    {
        var nestedServerPath = Path.Combine(_baseDirectory, "llama-server", "llama-server.exe");
        if (File.Exists(nestedServerPath))
        {
            WriteLog($"[launcher] 使用 llama-server：{nestedServerPath}。");
            return nestedServerPath;
        }

        var adjacentServerPath = Path.Combine(_baseDirectory, "llama-server.exe");
        if (File.Exists(adjacentServerPath))
        {
            WriteLog($"[launcher] 子目录未找到 llama-server，改用同目录版本：{adjacentServerPath}。");
            return adjacentServerPath;
        }

        throw new FileNotFoundException(
            $"找不到 llama-server.exe：{nestedServerPath} 或 {adjacentServerPath}",
            adjacentServerPath);
    }

    private static void AddBuiltInArguments(
        System.Collections.ObjectModel.Collection<string> arguments,
        string modelPath,
        string draftModelPath,
        string mmprojPath)
    {
        string[] values =
        [
            "-lv", "3",
            "--model", modelPath,
            "--spec-draft-model", draftModelPath,
            "--spec-draft-ngl", "all",
            "--spec-type", "draft-mtp",
            "--spec-draft-n-max", "3",
            "--spec-draft-p-min", "0",
            "--mmproj", mmprojPath,
            "--image-min-tokens", "1024",
            "--reasoning-preserve",
            "--reasoning-effort", "xhigh",
            "--reasoning-format", "deepseek",
            "--reasoning-budget", "12288",
            "--n-predict", "24576",
            "--jinja",
            "--no-mmap",
            "-ngl", "all",
            "-c", "131072",
            "--parallel", "1",
            "--batch-size", "2048",
            "--ubatch-size", "512",
            "--flash-attn", "on",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--temp", "1",
            "--top-p", "0.95",
            "--top-k", "20",
            "--min-p", "0",
            "--repeat-penalty", "1.0",
            "--presence-penalty", "0",
            "--host", "127.0.0.1",
            "--port", "1234",
        ];

        foreach (var value in values)
        {
            arguments.Add(value);
        }
    }

    private void AddExternalArguments(
        System.Collections.ObjectModel.Collection<string> arguments,
        string argumentsPath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(argumentsPath, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"llama-server 启动参数文件不是有效的 UTF-8：{argumentsPath}",
                exception);
        }

        var parsedArguments = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            foreach (var argument in TokenizeArgumentLine(lines[index], index + 1))
            {
                parsedArguments.Add(argument
                    .Replace("{BASE_DIR}", _baseDirectory, StringComparison.OrdinalIgnoreCase)
                    .Replace("%BASE_DIR%", _baseDirectory, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (parsedArguments.Count == 0)
        {
            throw new InvalidDataException(
                $"llama-server 启动参数文件为空：{argumentsPath}");
        }

        foreach (var argument in parsedArguments)
        {
            arguments.Add(argument);
        }

        WriteLog($"[launcher] 已读取 {parsedArguments.Count} 个外部 llama-server 参数。");
    }

    private static IEnumerable<string> TokenizeArgumentLine(string line, int lineNumber)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var tokenStarted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(character);
                }

                tokenStarted = true;
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }

            if ((character is '#' or ';') &&
                (index == 0 || char.IsWhiteSpace(line[index - 1])))
            {
                break;
            }

            if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (quote != '\0')
        {
            throw new InvalidDataException(
                $"llama-server 启动参数文件第 {lineNumber} 行的引号未闭合。");
        }

        if (tokenStarted)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static void EnsureFileExists(string path, string displayName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到{displayName}：{path}", path);
        }
    }

    private async Task RefreshStatusAsync()
    {
        _statusTimer.Stop();
        try
        {
            if (_stopping)
            {
                return;
            }

            if (_exitSignal.WaitOne(0))
            {
                RequestExit();
                return;
            }

            if (_serverProcess is null || _serverProcess.HasExited)
            {
                MarkServerStopped();
                return;
            }

            try
            {
                using var response = await _httpClient.GetAsync(HealthEndpoint);
                if (response.IsSuccessStatusCode && !_ready)
                {
                    _ready = true;
                    _statusItem.Text = $"状态：运行中（PID {_serverProcess.Id}）";
                    _trayIcon.Text = "Qwen3.8 27B - 运行中";
                    _openWebItem.Enabled = true;
                    _trayIcon.ShowBalloonTip(
                        3000,
                        "Qwen3.8 27B",
                        "模型已加载完成，服务地址为 http://127.0.0.1:1234/",
                        ToolTipIcon.Info);
                    WriteLog("[launcher] 健康检查通过，模型服务已就绪。");
                }
            }
            catch (HttpRequestException)
            {
                // Connection failures are expected while the model is loading.
            }
            catch (TaskCanceledException)
            {
                // A one-second health-check timeout is expected during heavy loading.
            }
        }
        finally
        {
            if (!_stopping && _serverProcess is { HasExited: false })
            {
                _statusTimer.Start();
            }
        }
    }

    private void MarkServerStopped()
    {
        _statusTimer.Stop();
        _statusItem.Text = "状态：模型进程已停止";
        _trayIcon.Text = "Qwen3.8 27B - 已停止";
        _openWebItem.Enabled = false;

        if (_exitNoticeShown || _stopping)
        {
            return;
        }

        _exitNoticeShown = true;
        _trayIcon.ShowBalloonTip(
            5000,
            "Qwen3.8 27B",
            "模型进程已停止。可右键查看启动日志或退出托盘。",
            ToolTipIcon.Warning);
    }

    private void OpenWebInterface()
    {
        if (!_ready || _stopping)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = WebEndpoint.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            WriteLog($"[launcher] 无法打开 Web 界面：{exception.Message}");
        }
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _logPath,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法打开日志：{exception.Message}\n\n日志路径：{_logPath}",
                "Qwen3.8 27B",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void RequestExit()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _statusItem.Text = "状态：正在关闭模型";
        _exitItem.Enabled = false;
        _statusTimer.Stop();
        ShutdownServer();
        _trayIcon.Visible = false;
        ExitThread();
    }

    private void ShutdownServer()
    {
        if (_shutdownComplete)
        {
            return;
        }

        _shutdownComplete = true;
        _stopping = true;
        _statusTimer?.Stop();
        WriteLog("[launcher] 正在关闭模型进程树。");

        if (_jobHandle != IntPtr.Zero)
        {
            NativeJob.Close(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }

        if (_serverProcess is not null)
        {
            try
            {
                if (!_serverAssignedToJob && !_serverProcess.HasExited)
                {
                    _serverProcess.Kill(entireProcessTree: true);
                }

                if (!_serverProcess.WaitForExit(5000))
                {
                    _serverProcess.Kill(entireProcessTree: true);
                    _serverProcess.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
            catch (Exception exception)
            {
                WriteLog($"[launcher] 终止模型进程时出现错误：{exception.Message}");
            }
        }

        WriteLog("[launcher] 模型进程树已关闭。");
    }

    private void WriteServerLine(string? line)
    {
        if (line is not null)
        {
            WriteLog(line);
        }
    }

    private void WriteLog(string text)
    {
        lock (_logLock)
        {
            try
            {
                _logWriter?.WriteLine(text);
            }
            catch
            {
                // Logging must never bring down the tray launcher.
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ShutdownServer();
            _statusTimer.Dispose();
            _httpClient.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Dispose();
            _applicationIcon.Dispose();
            _serverProcess?.Dispose();

            lock (_logLock)
            {
                _logWriter?.Dispose();
                _logWriter = null;
            }
        }

        base.Dispose(disposing);
    }
}

internal static class NativeJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    public static IntPtr CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)size))
            {
                CloseHandle(handle);
                return IntPtr.Zero;
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static bool AssignProcess(IntPtr jobHandle, IntPtr processHandle) =>
        AssignProcessToJobObject(jobHandle, processHandle);

    public static void Close(IntPtr handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr jobHandle,
        int jobObjectInformationClass,
        IntPtr jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr jobHandle, IntPtr processHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
