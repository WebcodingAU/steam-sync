using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PingHelper;

/// <summary>
/// \\.\pipe\GamingEnjoyer.Ping: once a second every connected client gets one line of JSON with the last second's
/// flows (FlowTable.TakeSnapshot). Read-only for people signed in at this PC (INTERACTIVE), closed to the network.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public const string PipeName = "GamingEnjoyer.Ping";
    private const int MaxClients = 8;

    private readonly FlowTable _flows;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<NamedPipeServerStream> _clients = [];
    private readonly object _gate = new();
    private Task? _accept, _publish;

    public PipeServer(FlowTable flows, string version)
    {
        _flows = flows;
        _version = version;
    }

    public void Start()
    {
        _accept = Task.Run(() => AcceptLoop(_stop.Token));
        _publish = Task.Run(() => PublishLoop(_stop.Token));
    }

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        // The service's own account creates and writes the pipe.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Anyone signed in at this PC may read it (the tray app runs as the signed-in user).
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
        // Named pipes are reachable over SMB too: never from another machine.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                lock (_gate)
                {
                    if (_clients.Count >= MaxClients) pipe = null;
                    else pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, MaxClients, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, Security());
                }
                if (pipe is null)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }
                await pipe.WaitForConnectionAsync(ct);
                lock (_gate) _clients.Add(pipe);
                pipe = null;
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Write($"Pipe accept failed: {ex.Message}");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PublishLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Always take the snapshot, so a second with no client doesn't pile into the next one.
                var line = _flows.TakeSnapshot(_version);
                NamedPipeServerStream[] clients;
                lock (_gate) clients = [.. _clients];
                foreach (var client in clients)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromMilliseconds(500)); // a client that stopped reading is dropped
                        await client.WriteAsync(line, timeout.Token);
                        await client.WriteAsync("\n"u8.ToArray(), timeout.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        if (ct.IsCancellationRequested) return;
                        lock (_gate) _clients.Remove(client);
                        client.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { Task.WaitAll([_accept ?? Task.CompletedTask, _publish ?? Task.CompletedTask], TimeSpan.FromSeconds(3)); } catch { }
        lock (_gate)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        _stop.Dispose();
    }
}
