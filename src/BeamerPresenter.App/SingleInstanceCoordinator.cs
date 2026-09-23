using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BeamerPresenter.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ShowCommand = "show";
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private readonly bool _isPrimary;
    private Task? _listenerTask;

    private SingleInstanceCoordinator(string applicationId)
    {
        var userIdentity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var userHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity)))[..16];
        var instanceName = $"{applicationId}.{userHash}";
        _pipeName = instanceName;
        _mutex = new Mutex(initiallyOwned: false, $"Local\\{instanceName}", out _isPrimary);
    }

    public bool IsPrimary => _isPrimary;

    public static SingleInstanceCoordinator Acquire(string applicationId = "BeamerPresenterForLanParties") => new(applicationId);

    public void StartListening(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation requests.");
        }

        ArgumentNullException.ThrowIfNull(activationRequested);
        _listenerTask ??= ListenAsync(activationRequested, _stopping.Token);
    }

    public async Task<bool> SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimary)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(500, timeout.Token);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: false) { AutoFlush = true };
                await writer.WriteLineAsync(ShowCommand.AsMemory(), timeout.Token);
                return true;
            }
            catch (TimeoutException)
            {
                await Task.Delay(100, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return false;
    }

    private async Task ListenAsync(Action activationRequested, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, ShowCommand, StringComparison.Ordinal))
                {
                    activationRequested();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _listenerTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _listenerTask = null;
        }

        _stopping.Dispose();
        _mutex.Dispose();
    }
}
