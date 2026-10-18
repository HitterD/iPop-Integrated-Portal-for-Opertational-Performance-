using System.IO.Pipes;
using System.Text;

namespace IPop.Notifier.Core;

/// Named mutex + named pipe: the first process owns the mutex and listens; later ones send one line and quit.
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stop = new();
    public bool IsFirst { get; }
    public event Action<string>? Received;

    public SingleInstance(string name)
    {
        mutex = new Mutex(true, @"Local\" + name, out var created);
        IsFirst = created;
        pipeName = name;
    }

    public void SendToFirst(string line)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(line);
            pipe.Write(bytes);
        }
        catch { /* the first instance is starting or closing; nothing to do */ }
    }

    public void Listen() => _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly: another Windows account on the same PC can't drive this app.
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = (await reader.ReadToEndAsync(stop.Token)).Trim();
                if (line.Length is > 0 and < 4096) Received?.Invoke(line);
            }
            catch (OperationCanceledException) { return; }
            catch { await Task.Delay(500); }
        }
    });

    public void Dispose()
    {
        stop.Cancel();
        if (IsFirst) { try { mutex.ReleaseMutex(); } catch { } }
        mutex.Dispose();
    }
}
