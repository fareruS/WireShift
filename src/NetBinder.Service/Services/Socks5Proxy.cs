using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetBinder.Service.Services;

/// <summary>
/// A lightweight SOCKS5 proxy server that binds its outbound sockets to a specific
/// network interface using IP_UNICAST_IF. This is the user-mode fallback for
/// per-app interface binding since WFP user-mode API cannot modify socket options.
///
/// ARCHITECTURE:
/// - One Socks5Proxy instance per network interface, each listening on a unique port
/// - The proxy's outbound socket sets IP_UNICAST_IF to the interface index,
///   forcing all outgoing traffic through that interface
/// - Apps that support proxy configuration (browsers, download managers) can
///   point to the proxy address (127.0.0.1:PORT) to route traffic through the interface
/// - For apps that don't support proxy, the WFP filter (Phase 2) combined with
///   a future transparent redirect (WinDivert/Phase 6 kernel driver) would be needed
///
/// SOCKS5 PROTOCOL (RFC 1928):
/// - Supports CONNECT command only (no BIND or UDP ASSOCIATE)
/// - Supports NO AUTH authentication only
/// - Supports IPv4 and domain address types
/// </summary>
public class Socks5Proxy : IDisposable
{
    #region P/Invoke for IP_UNICAST_IF

    private const string Ws2_32 = "ws2_32.dll";

    /// <summary>Socket option level for IP.</summary>
    private const int IPPROTO_IP = 0;

    /// <summary>Socket option to set the outgoing interface for unicast traffic.</summary>
    private const int IP_UNICAST_IF = 31;

    /// <summary>Socket option level for IPv6.</summary>
    private const int IPPROTO_IPV6 = 41;

    /// <summary>Socket option to set the outgoing interface for IPv6 unicast traffic.</summary>
    private const int IPV6_UNICAST_IF = 31;

    [DllImport(Ws2_32)]
    private static extern int setsockopt(IntPtr socketHandle, int level, int optionName, ref int optionValue, int optionLength);

    #endregion

    private Socket? _listenSocket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private bool _disposed;
    private readonly List<Task> _activeConnections = [];

    /// <summary>The interface index this proxy is bound to.</summary>
    public int InterfaceIndex { get; }

    /// <summary>The interface name (for display purposes).</summary>
    public string InterfaceName { get; }

    /// <summary>The local port this proxy listens on.</summary>
    public int ListenPort { get; }

    /// <summary>The local IP address this proxy listens on (always 127.0.0.1).</summary>
    public string ListenAddress => "127.0.0.1";

    /// <summary>Whether the proxy is currently running.</summary>
    public bool IsRunning => _listenSocket != null && _cts != null && !_cts.IsCancellationRequested;

    /// <summary>The SOCKS5 proxy address string (e.g., "127.0.0.1:10800").</summary>
    public string ProxyAddress => $"{ListenAddress}:{ListenPort}";

    public Socks5Proxy(int interfaceIndex, string interfaceName, int listenPort)
    {
        InterfaceIndex = interfaceIndex;
        InterfaceName = interfaceName;
        ListenPort = listenPort;
    }

    /// <summary>
    /// Starts the SOCKS5 proxy server.
    /// Binds to 127.0.0.1 on the specified port.
    /// </summary>
    public bool Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, ListenPort));
            _listenSocket.Listen(32);

            _acceptTask = AcceptLoopAsync(_cts.Token);
            Console.WriteLine($"[Socks5Proxy] Started on {ProxyAddress} for interface '{InterfaceName}' (index {InterfaceIndex})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Socks5Proxy] Failed to start on port {ListenPort}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stops the SOCKS5 proxy server and disconnects all clients.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listenSocket?.Close();
        _listenSocket = null;

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }

        Console.WriteLine($"[Socks5Proxy] Stopped proxy for interface '{InterfaceName}'");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listenSocket != null)
        {
            try
            {
                var clientSocket = await _listenSocket!.AcceptAsync(ct);
                var connTask = HandleClientAsync(clientSocket, ct);
                _activeConnections.Add(connTask);

                // Clean up completed connections
                _activeConnections.RemoveAll(t => t.IsCompleted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Socks5Proxy] Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Handles a single SOCKS5 client connection.
    /// Implements RFC 1928 (SOCKS5) with NO AUTH and CONNECT command.
    /// </summary>
    private async Task HandleClientAsync(Socket clientSocket, CancellationToken ct)
    {
        try
        {
            // Step 1: Client greeting
            // Client sends: VER (1) | NMETHODS (1) | METHODS (NMETHODS)
            var greeting = new byte[2];
            int read = await clientSocket.ReceiveAsync(greeting.AsMemory(0, 2), ct);
            if (read < 2 || greeting[0] != 0x05) // Must be SOCKS5
            {
                clientSocket.Close();
                return;
            }

            int numMethods = greeting[1];
            var methods = new byte[numMethods];
            read = await clientSocket.ReceiveAsync(methods.AsMemory(0, numMethods), ct);
            if (read < numMethods)
            {
                clientSocket.Close();
                return;
            }

            // Step 2: Server chooses NO AUTH (0x00)
            var authResponse = new byte[] { 0x05, 0x00 };
            await clientSocket.SendAsync(authResponse.AsMemory(), ct);

            // Step 3: Client request
            // VER (1) | CMD (1) | RSV (1) | ATYP (1) | DST.ADDR (variable) | DST.PORT (2)
            var requestHeader = new byte[4];
            read = await clientSocket.ReceiveAsync(requestHeader.AsMemory(0, 4), ct);
            if (read < 4 || requestHeader[0] != 0x05)
            {
                SendSocksReply(clientSocket, 0x01); // General failure
                clientSocket.Close();
                return;
            }

            byte cmd = requestHeader[1];
            byte atyp = requestHeader[3];

            if (cmd != 0x01) // Only CONNECT supported
            {
                SendSocksReply(clientSocket, 0x07); // Command not supported
                clientSocket.Close();
                return;
            }

            // Parse destination address
            IPAddress? destAddr = null;
            string? destHost = null;
            int addrBytesToRead;

            switch (atyp)
            {
                case 0x01: // IPv4
                    addrBytesToRead = 4;
                    var ipv4Bytes = new byte[4];
                    read = await clientSocket.ReceiveAsync(ipv4Bytes.AsMemory(), ct);
                    if (read < 4) { clientSocket.Close(); return; }
                    destAddr = new IPAddress(ipv4Bytes);
                    break;

                case 0x03: // Domain name
                    var domainLen = new byte[1];
                    read = await clientSocket.ReceiveAsync(domainLen.AsMemory(), ct);
                    if (read < 1) { clientSocket.Close(); return; }
                    addrBytesToRead = domainLen[0];
                    var domainBytes = new byte[addrBytesToRead];
                    read = await clientSocket.ReceiveAsync(domainBytes.AsMemory(), ct);
                    if (read < addrBytesToRead) { clientSocket.Close(); return; }
                    destHost = System.Text.Encoding.ASCII.GetString(domainBytes);
                    break;

                case 0x04: // IPv6
                    addrBytesToRead = 16;
                    var ipv6Bytes = new byte[16];
                    read = await clientSocket.ReceiveAsync(ipv6Bytes.AsMemory(), ct);
                    if (read < 16) { clientSocket.Close(); return; }
                    destAddr = new IPAddress(ipv6Bytes);
                    break;

                default:
                    SendSocksReply(clientSocket, 0x08); // Address type not supported
                    clientSocket.Close();
                    return;
            }

            // Parse destination port
            var portBytes = new byte[2];
            read = await clientSocket.ReceiveAsync(portBytes.AsMemory(), ct);
            if (read < 2) { clientSocket.Close(); return; }
            int destPort = (portBytes[0] << 8) | portBytes[1];

            // Determine Address Family dynamically based on destination address or resolved host
            AddressFamily family = AddressFamily.InterNetwork;
            if (destAddr != null)
            {
                family = destAddr.AddressFamily;
            }
            else if (destHost != null)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(destHost);
                    // Prefer IPv4 for compatibility, but allow IPv6 if it's the only option
                    var selectedAddr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                                      ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                    if (selectedAddr == null)
                    {
                        SendSocksReply(clientSocket, 0x04); // Host unreachable
                        clientSocket.Close();
                        return;
                    }
                    destAddr = selectedAddr;
                    family = destAddr.AddressFamily;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Socks5Proxy] DNS resolution failed for host '{destHost}': {ex.Message}");
                    SendSocksReply(clientSocket, 0x04); // Host unreachable
                    clientSocket.Close();
                    return;
                }
            }

            // Step 4: Connect to destination through the target interface
            var remoteSocket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);

            // Bind the outbound socket to the local IP of the target interface.
            // On Windows, setsockopt(IP_UNICAST_IF) is not supported for TCP (SOCK_STREAM) sockets,
            // so we must bind the socket to the interface's local IP address instead.
            var localIp = GetLocalIpAddress(family);
            if (localIp != null)
            {
                try
                {
                    remoteSocket.Bind(new IPEndPoint(localIp, 0));
                    Console.WriteLine($"[Socks5Proxy] Bound outbound socket to local IP {localIp} for interface '{InterfaceName}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Socks5Proxy] Warning: Failed to bind outbound socket to local IP {localIp}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[Socks5Proxy] Warning: Could not resolve local IP address for interface '{InterfaceName}' (Family: {family})");
            }

            try
            {
                // We resolved destAddr above if it was a host, so it is guaranteed to be non-null here
                await remoteSocket.ConnectAsync(new IPEndPoint(destAddr!, destPort), ct);

                // Step 5: Send success reply conforming to SOCKS5 (RFC 1928) for IPv4/IPv6
                var localEndPoint = (IPEndPoint)remoteSocket.LocalEndPoint!;
                byte[] reply;
                if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    reply = new byte[22];
                    reply[0] = 0x05; // VER
                    reply[1] = 0x00; // REP: succeeded
                    reply[2] = 0x00; // RSV
                    reply[3] = 0x04; // ATYP: IPv6
                    var localIpBytes = localEndPoint.Address.GetAddressBytes();
                    Buffer.BlockCopy(localIpBytes, 0, reply, 4, 16);
                    reply[20] = (byte)(localEndPoint.Port >> 8);
                    reply[21] = (byte)(localEndPoint.Port & 0xFF);
                }
                else
                {
                    reply = new byte[10];
                    reply[0] = 0x05; // VER
                    reply[1] = 0x00; // REP: succeeded
                    reply[2] = 0x00; // RSV
                    reply[3] = 0x01; // ATYP: IPv4
                    var localIpBytes = localEndPoint.Address.GetAddressBytes();
                    Buffer.BlockCopy(localIpBytes, 0, reply, 4, 4);
                    reply[8] = (byte)(localEndPoint.Port >> 8);
                    reply[9] = (byte)(localEndPoint.Port & 0xFF);
                }
                await clientSocket.SendAsync(reply.AsMemory(), ct);

                // Step 6: Relay data bidirectionally
                await RelayDataAsync(clientSocket, remoteSocket, ct);
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[Socks5Proxy] Connect to {destAddr ?? (object?)destHost}:{destPort} failed: {ex.Message}");
                byte repCode = ex.SocketErrorCode == SocketError.HostUnreachable ? (byte)0x04 :
                               ex.SocketErrorCode == SocketError.ConnectionRefused ? (byte)0x05 :
                               (byte)0x01; // General failure
                SendSocksReply(clientSocket, repCode);
                clientSocket.Close();
                remoteSocket.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Socks5Proxy] Client handling error: {ex.Message}");
        }
        finally
        {
            try { clientSocket.Close(); } catch { }
        }
    }

    private IPAddress? GetLocalIpAddress(AddressFamily family)
    {
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var nic = nics.FirstOrDefault(n => n.Name.Equals(InterfaceName, System.StringComparison.OrdinalIgnoreCase));
            if (nic != null)
            {
                var ipProps = nic.GetIPProperties();
                var unicastAddrs = ipProps.UnicastAddresses;
                var addr = unicastAddrs.FirstOrDefault(a => a.Address.AddressFamily == family);
                return addr?.Address;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Relays data between client and remote socket bidirectionally.</summary>
    private static async Task RelayDataAsync(Socket client, Socket remote, CancellationToken ct)
    {
        var clientToRemote = RelayOneDirectionAsync(client, remote, ct);
        var remoteToClient = RelayOneDirectionAsync(remote, client, ct);
        await Task.WhenAny(clientToRemote, remoteToClient);

        try { client.Close(); } catch { }
        try { remote.Close(); } catch { }
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

    /// <summary>Sends a SOCKS5 reply with an error code.</summary>
    private static void SendSocksReply(Socket socket, byte replyCode)
    {
        try
        {
            var reply = new byte[] { 0x05, replyCode, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            socket.Send(reply);
        }
        catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }
    }
}

/// <summary>
/// Manages multiple SOCKS5 proxy instances, one per network interface.
/// Assigns ports starting from a base port (10800) and increments.
/// Integrates with ConfigManager for persistent port assignments.
/// </summary>
public class Socks5ProxyManager : IDisposable
{
    /// <summary>Base port for SOCKS5 proxies (each interface gets a unique port).</summary>
    public const int BasePort = 10800;

    /// <summary>Maximum number of proxy instances (also max number of interfaces).</summary>
    public const int MaxProxies = 32;

    private readonly Dictionary<int, Socks5Proxy> _proxiesByInterfaceIndex = [];
    private readonly Dictionary<int, int> _portAssignments = []; // interfaceIndex -> port
    private int _nextPort = BasePort;
    private bool _disposed;

    /// <summary>Gets proxy info for all active proxies.</summary>
    public IReadOnlyDictionary<int, Socks5Proxy> Proxies => _proxiesByInterfaceIndex;

    /// <summary>
    /// Starts a SOCKS5 proxy for the specified interface.
    /// If a proxy is already running for this interface, returns its info.
    /// </summary>
    public Socks5Proxy? StartProxy(int interfaceIndex, string interfaceName, int? preferredPort = null)
    {
        // Check if already running
        if (_proxiesByInterfaceIndex.TryGetValue(interfaceIndex, out var existing))
        {
            return existing;
        }

        // Try the remembered/preferred port first, then fall back to any free port in
        // the reserved range. Ports are a shared resource: another process (or a previous
        // run that has not released the socket yet) may already own the preferred one.
        foreach (int port in CandidatePorts(interfaceIndex, preferredPort))
        {
            var candidate = new Socks5Proxy(interfaceIndex, interfaceName, port);
            if (candidate.Start())
            {
                _proxiesByInterfaceIndex[interfaceIndex] = candidate;
                _portAssignments[interfaceIndex] = port;
                _nextPort = port + 1;

                if (preferredPort.HasValue && preferredPort.Value != port)
                {
                    Console.WriteLine($"[Socks5ProxyManager] Port {preferredPort.Value} was unavailable; " +
                                      $"interface '{interfaceName}' uses {port} instead.");
                }

                return candidate;
            }

            candidate.Dispose();
        }

        Console.WriteLine($"[Socks5ProxyManager] No free port in {BasePort}-{BasePort + MaxProxies - 1} " +
                          $"for interface '{interfaceName}' (index {interfaceIndex}).");
        return null;
    }

    /// <summary>
    /// Ports to attempt, in order: explicitly preferred, then the remembered assignment,
    /// then the reserved range starting after the last handed-out port.
    /// </summary>
    private IEnumerable<int> CandidatePorts(int interfaceIndex, int? preferredPort)
    {
        var tried = new HashSet<int>();

        if (preferredPort is >= BasePort && preferredPort < BasePort + MaxProxies &&
            tried.Add(preferredPort.Value))
        {
            yield return preferredPort.Value;
        }

        if (_portAssignments.TryGetValue(interfaceIndex, out int savedPort) && tried.Add(savedPort))
        {
            yield return savedPort;
        }

        int start = _nextPort;

        // Walk the reserved range once, wrapping back to the base port.
        for (int offset = 0; offset < MaxProxies; offset++)
        {
            int port = BasePort + ((start - BasePort + offset) % MaxProxies);
            if (tried.Add(port))
            {
                yield return port;
            }
        }
    }

    /// <summary>Stops the proxy for the specified interface.</summary>
    public void StopProxy(int interfaceIndex)
    {
        if (_proxiesByInterfaceIndex.TryGetValue(interfaceIndex, out var proxy))
        {
            proxy.Stop();
            proxy.Dispose();
            _proxiesByInterfaceIndex.Remove(interfaceIndex);
        }
    }

    /// <summary>Stops all running proxies.</summary>
    public void StopAll()
    {
        foreach (var proxy in _proxiesByInterfaceIndex.Values)
        {
            proxy.Stop();
            proxy.Dispose();
        }
        _proxiesByInterfaceIndex.Clear();
    }

    /// <summary>Gets the port assigned to an interface, or -1 if not assigned.</summary>
    public int GetPortForInterface(int interfaceIndex)
    {
        return _portAssignments.GetValueOrDefault(interfaceIndex, -1);
    }

    /// <summary>Gets the proxy address for an interface, or empty string if no proxy.</summary>
    public string GetProxyAddressForInterface(int interfaceIndex)
    {
        if (_proxiesByInterfaceIndex.TryGetValue(interfaceIndex, out var proxy))
        {
            return proxy.ProxyAddress;
        }
        return string.Empty;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            StopAll();
        }
    }
}
