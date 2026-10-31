using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetBinder.Service.NativeInterop;

namespace NetBinder.Service.Services;

/// <summary>
/// A raw TCP Relay Server that listens on a loopback port.
/// It intercepts client connections rewritten by WinDivert, queries a NAT table
/// to find their original remote destination and target interface, binds a new outbound
/// socket to the target adapter's IP, and bidirectionally proxies all bytes.
/// </summary>
public class TransparentProxy : IDisposable
{
    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;
    private readonly List<Task> _activeConnections = [];
    private readonly object _lock = new();

    /// <summary>
    /// Delegate to lookup the original destination and target adapter index for a given client port.
    /// </summary>
    private readonly Func<ushort, (IPEndPoint OriginalRemoteEndpoint, int InterfaceIndex)?> _natLookup;

    /// <summary>
    /// The port this relay server is listening on.
    /// </summary>
    public int ListenPort { get; private set; }

    public bool IsRunning => _listenSocket != null && _cts != null && !_cts.IsCancellationRequested;

    public TransparentProxy(Func<ushort, (IPEndPoint, int)?> natLookup)
    {
        _natLookup = natLookup ?? throw new ArgumentNullException(nameof(natLookup));
    }

    /// <summary>
    /// Starts the transparent proxy listener on a dynamic loopback port.
    /// </summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (IsRunning) return true;

            try
            {
                _cts = new CancellationTokenSource();
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                
                // RedirectorService rewrites captured connections to 127.0.0.1,
                // so keep the relay listener on loopback rather than exposing it to
                // other machines on the LAN.
                _listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _listenSocket.Listen(100);

                var localEp = (IPEndPoint)_listenSocket.LocalEndPoint!;
                ListenPort = localEp.Port;

                _acceptTask = AcceptLoopAsync(_cts.Token);
                Console.WriteLine($"[TransparentProxy] Started listening on 0.0.0.0:{ListenPort}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Failed to start: {ex.Message}");
                Stop();
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the transparent proxy listener and cancels all active connections.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();

            try
            {
                _listenSocket?.Close();
            }
            catch { }
            _listenSocket = null;

            // Wait for accept loop to exit
            if (_acceptTask != null)
            {
                try { _acceptTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
                _acceptTask = null;
            }

            // Copy list to avoid concurrent modification issues
            List<Task> connectionsToWait;
            lock (_activeConnections)
            {
                connectionsToWait = new List<Task>(_activeConnections);
            }

            try
            {
                Task.WhenAll(connectionsToWait).Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            lock (_activeConnections)
            {
                _activeConnections.Clear();
            }

            _cts?.Dispose();
            _cts = null;
            Console.WriteLine("[TransparentProxy] Stopped.");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listenSocket = _listenSocket;
        if (listenSocket == null) return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket clientSocket = await listenSocket.AcceptAsync(ct);
                var clientEp = (IPEndPoint)clientSocket.RemoteEndPoint!;
                var localEp = (IPEndPoint)clientSocket.LocalEndPoint!;
                ushort clientPort = (ushort)clientEp.Port;
                Console.WriteLine($"[TransparentProxy] Accepted connection from {clientEp} on {localEp}, client port={clientPort}");

                var connTask = Task.Run(() => HandleClientAsync(clientSocket, clientPort, ct), ct);
                
                lock (_activeConnections)
                {
                    _activeConnections.Add(connTask);
                }

                // Cleanup completed connection tasks
                _ = connTask.ContinueWith(t =>
                {
                    lock (_activeConnections)
                    {
                        _activeConnections.Remove(t);
                    }
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[TransparentProxy] Error in accept loop: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(Socket clientSocket, ushort clientPort, CancellationToken ct)
    {
        try
        {
            // Lookup original destination and target interface for the connection
            var mapping = _natLookup(clientPort);
            if (mapping == null)
            {
                Console.WriteLine($"[TransparentProxy] Warning: No NAT mapping found for client port {clientPort}. Closing connection.");
                clientSocket.Close();
                return;
            }

            var (originalRemoteEp, interfaceIndex) = mapping.Value;
            
            // Get local IP address associated with the target interface index
            string? targetIpStr = null;
            try
            {
                var adapters = IphlpApiWrapper.GetNetworkAdapters();
                foreach (var adapter in adapters)
                {
                    if (adapter.InterfaceIndex == interfaceIndex)
                    {
                        targetIpStr = adapter.IpAddress;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error retrieving IP address for interface index {interfaceIndex}: {ex.Message}");
            }

            if (string.IsNullOrEmpty(targetIpStr))
            {
                Console.WriteLine($"[TransparentProxy] Error: Could not find IP address for target interface index {interfaceIndex}. Closing connection.");
                clientSocket.Close();
                return;
            }

            // Create outbound socket and bind it to the target interface's IP
            IPAddress targetIp = IPAddress.Parse(targetIpStr);
            using var targetSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            
            try
            {
                // Try setting IP_UNICAST_IF (31) to force traffic through the interface
                int indexNetOrder = IPAddress.HostToNetworkOrder(interfaceIndex);
                targetSocket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, indexNetOrder);
                Console.WriteLine($"[TransparentProxy] Set IP_UNICAST_IF to {interfaceIndex} for {originalRemoteEp}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Warning: Failed to set IP_UNICAST_IF: {ex.Message}");
            }

            try
            {
                targetSocket.Bind(new IPEndPoint(targetIp, 0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error: Failed to bind outbound socket to IP {targetIpStr}: {ex.Message}");
                clientSocket.Close();
                return;
            }

            // Connect to the original destination
            try
            {
                await targetSocket.ConnectAsync(originalRemoteEp, ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransparentProxy] Error: Failed to connect to original destination {originalRemoteEp} via interface index {interfaceIndex}: {ex.Message}");
                clientSocket.Close();
                return;
            }

            // Bidirectionally relay data
            await RelayDataAsync(clientSocket, targetSocket, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransparentProxy] Error handling client connection: {ex.Message}");
        }
        finally
        {
            try { clientSocket.Close(); } catch { }
        }
    }

    private static async Task RelayDataAsync(Socket client, Socket target, CancellationToken ct)
    {
        var clientToTarget = RelayOneDirectionAsync(client, target, ct);
        var targetToClient = RelayOneDirectionAsync(target, client, ct);
        await Task.WhenAny(clientToTarget, targetToClient);

        try { client.Close(); } catch { }
        try { target.Close(); } catch { }
    }

    private static async Task RelayOneDirectionAsync(Socket from, Socket to, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await from.ReceiveAsync(buffer.AsMemory(), ct);
                if (read == 0) break;

                await to.SendAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
        }
    }
}
