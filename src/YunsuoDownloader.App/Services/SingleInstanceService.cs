using System.IO.Pipes;
using System.Text;

namespace YunsuoDownloader.App.Services;

/// <summary>
/// 单实例控制。
/// 第一个实例创建互斥体并启动命名管道服务；后续实例把命令行参数（可能是链接）
/// 通过管道发给第一个实例后自行退出——这样"用本程序打开链接"永远不会开出第二个窗口。
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\YunsuoDownloader.SingleInstance.v1";
    private const string PipeName = "YunsuoDownloader.Ipc.v1";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;
    private bool _isPrimary;

    /// <summary>收到来自第二个实例的参数。</summary>
    public event EventHandler<string[]>? ArgumentsReceived;

    /// <summary>
    /// 过滤掉空参数。
    /// 注意：这里传入的是 WPF 的 <c>StartupEventArgs.Args</c>，它**不包含** exe 路径，
    /// 所以不能跳过第一个参数（跳过会把唯一的那个链接/开关丢掉）。
    /// </summary>
    public static string[] ParseArgs(string[] raw) =>
        raw.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();

    /// <summary>尝试成为主实例。返回 false 表示已有实例在运行（参数已转发过去）。</summary>
    public bool TryAcquire(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out var created);
        _isPrimary = created;

        if (created)
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            return true;
        }

        // 已有实例：把参数转发过去
        TrySendToPrimary(args);
        return false;
    }

    private static void TrySendToPrimary(string[] args)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(800);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (var a in args) writer.WriteLine(a);
                return;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var list = new List<string>();
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line)) list.Add(line);
                }
                if (list.Count > 0) ArgumentsReceived?.Invoke(this, list.ToArray());
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_isPrimary) _mutex?.ReleaseMutex();
        }
        catch { }
        try { _mutex?.Dispose(); } catch { }
        _cts?.Dispose();
    }
}
