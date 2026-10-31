using System.IO.Pipes;
using System.Text.Json;
using NetBinder.Shared.Models;
using NetBinder.Shared.Protocol;

namespace NetBinder.Service.Services;

/// <summary>
/// Named Pipe server that listens for commands from the UI process.
/// Pipe name: "NetBinderService" - bidirectional, message-based.
/// Protocol: 4-byte length prefix (little-endian) + JSON payload.
/// Integrates with WfpFilterManager for dynamic filter management
/// and Socks5ProxyManager for per-interface proxy instances.
/// </summary>
public class PipeServer : IDisposable
{
    private const string PipeName = "NetBinderService";
    private const int BufferSize = 65536;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// Maps the JSON "type" discriminator to the concrete PipeRequest subclass
    /// so System.Text.Json can instantiate the correct type.
    /// </summary>
    private static readonly Dictionary<string, Type> RequestTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GetAdapters"]  = typeof(GetAdaptersRequest),
        ["GetProcesses"] = typeof(GetProcessesRequest),
        ["AddBinding"]   = typeof(AddBindingRequest),
        ["RemoveBinding"]= typeof(RemoveBindingRequest),
        ["UpdateBinding"]= typeof(UpdateBindingRequest),
        ["ToggleBinding"]= typeof(ToggleBindingRequest),
        ["GetBindings"]  = typeof(GetBindingsRequest),
        ["SetMetric"]    = typeof(SetMetricRequest),
        ["ResetMetric"]  = typeof(ResetMetricRequest),
        ["GetConfig"]    = typeof(GetConfigRequest),
        ["GetProxyInfo"] = typeof(GetProxyInfoRequest),
        ["StartProxy"]   = typeof(StartProxyRequest),
        ["StopProxy"]    = typeof(StopProxyRequest),
        ["SetRoutingMethod"] = typeof(SetRoutingMethodRequest),
    };

    private readonly ConfigManager _configManager;
    private readonly NativeInterop.WfpFilterManager _wfpManager;
    private readonly Socks5ProxyManager _proxyManager;
    private readonly RedirectorService _redirector;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public PipeServer(ConfigManager configManager, NativeInterop.WfpFilterManager wfpManager, Socks5ProxyManager proxyManager, RedirectorService redirector)
    {
        _configManager = configManager;
        _wfpManager = wfpManager;
        _proxyManager = proxyManager;
        _redirector = redirector;
    }

    /// <summary>Start listening for pipe connections.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = ListenAsync(_cts.Token);
        Console.WriteLine("[PipeServer] Started listening on pipe: " + PipeName);
    }

    /// <summary>Stop listening and disconnect.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listenTask?.Wait(TimeSpan.FromSeconds(5));
        Console.WriteLine("[PipeServer] Stopped.");
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipeServer = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    BufferSize,
                    BufferSize);

                Console.WriteLine("[PipeServer] Waiting for connection...");
                await pipeServer.WaitForConnectionAsync(ct);
                Console.WriteLine("[PipeServer] Client connected.");

                await ProcessClientAsync(pipeServer, ct);

                pipeServer.WaitForPipeDrain();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PipeServer] Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ProcessClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Console.WriteLine("[PipeServer] Entering ProcessClientAsync.");
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                Console.WriteLine("[PipeServer] Waiting for request...");
                var payloadBuffer = await PipeFraming.ReadMessageAsync(pipe, ct);
                if (payloadBuffer == null) break;

                string json = System.Text.Encoding.UTF8.GetString(payloadBuffer);
                Console.WriteLine($"[PipeServer] Received {payloadBuffer.Length} bytes: {json[..Math.Min(json.Length, 200)]}");

                // Peek at the "type" field to determine the concrete request class.
                // PipeRequest is abstract, so System.Text.Json cannot instantiate it directly.
                string? requestType = null;
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("type", out var typeProp))
                        requestType = typeProp.GetString();
                }

                Console.WriteLine($"[PipeServer] Request type: {requestType}");
                if (requestType == null || !RequestTypeMap.TryGetValue(requestType, out var concreteType))
                {
                    var errResp = new GenericResponse { Success = false, Error = $"Unknown or missing request type: {requestType}" };
                    await SendResponseAsync(pipe, errResp, ct);
                    continue;
                }

                var request = (PipeRequest?)JsonSerializer.Deserialize(json, concreteType, JsonOptions);
                if (request == null) continue;

                Console.WriteLine($"[PipeServer] Handling {requestType}...");
                var response = HandleRequest(request, json);
                Console.WriteLine($"[PipeServer] {requestType} handled. Success={response.Success}");
                await SendResponseAsync(pipe, response, ct);
                Console.WriteLine($"[PipeServer] Response sent for {requestType}.");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[PipeServer] Client loop cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PipeServer] Client error: {ex}");
        }
        finally
        {
            Console.WriteLine("[PipeServer] Exiting ProcessClientAsync.");
        }
    }

    private PipeResponse HandleRequest(PipeRequest request, string rawJson)
    {
        try
        {
            return request.Type switch
            {
                "GetAdapters" => HandleGetAdapters(),
                "GetProcesses" => HandleGetProcesses(),
                "GetBindings" => HandleGetBindings(),
                "GetConfig" => HandleGetConfig(),
                "AddBinding" => HandleAddBinding(rawJson),
                "RemoveBinding" => HandleRemoveBinding(rawJson),
                "UpdateBinding" => HandleUpdateBinding(rawJson),
                "ToggleBinding" => HandleToggleBinding(rawJson),
                "SetMetric" => HandleSetMetric(rawJson),
                "ResetMetric" => HandleResetMetric(rawJson),
                "GetProxyInfo" => HandleGetProxyInfo(),
                "StartProxy" => HandleStartProxy(rawJson),
                "StopProxy" => HandleStopProxy(rawJson),
                "SetRoutingMethod" => HandleSetRoutingMethod(rawJson),
                _ => new GenericResponse { Success = false, Error = $"Unknown request type: {request.Type}" }
            };
        }
        catch (Exception ex)
        {
            return new GenericResponse { Success = false, Error = ex.Message };
        }
    }

    private GetAdaptersResponse HandleGetAdapters()
    {
        var adapters = NativeInterop.IphlpApiWrapper.GetNetworkAdapters();
        return new GetAdaptersResponse { Adapters = adapters };
    }

    private GetProcessesResponse HandleGetProcesses()
    {
        var processes = NativeInterop.TcpTableWrapper.GetProcessesWithConnections();
        foreach (var proc in processes)
        {
            if (!string.IsNullOrEmpty(proc.FilePath) && !proc.FilePath.StartsWith("<"))
            {
                var binding = _configManager.Config.Bindings.FirstOrDefault(b =>
                    b.ExecutablePath.Equals(proc.FilePath, StringComparison.OrdinalIgnoreCase));
                if (binding != null)
                {
                    proc.IsBound = true;
                    proc.BoundInterfaceName = binding.InterfaceName;
                }
            }
        }
        return new GetProcessesResponse { Processes = processes };
    }

    private GetBindingsResponse HandleGetBindings()
    {
        // Enrich bindings with proxy info before sending
        foreach (var binding in _configManager.Config.Bindings)
        {
            binding.ProxyAddress = _proxyManager.GetProxyAddressForInterface(binding.InterfaceIndex);
            binding.ProxyPort = _proxyManager.GetPortForInterface(binding.InterfaceIndex);
        }
        return new GetBindingsResponse { Bindings = _configManager.Config.Bindings };
    }

    private GetConfigResponse HandleGetConfig()
    {
        return new GetConfigResponse { Config = _configManager.Config };
    }

    private GenericResponse HandleAddBinding(string rawJson)
    {
        var req = JsonSerializer.Deserialize<AddBindingRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid AddBinding request" };

        var binding = _configManager.AddBinding(req.ExecutablePath, req.ProcessName, req.InterfaceIndex, req.InterfaceName, req.RoutingMethod);

        // Start proxy for this interface if not already running
        var proxy = _proxyManager.StartProxy(binding.InterfaceIndex, binding.InterfaceName);
        if (proxy != null)
        {
            binding.ProxyAddress = proxy.ProxyAddress;
            binding.ProxyPort = proxy.ListenPort;
            _configManager.Save();
        }

        // Activate WFP filter (only if Transparent method)
        if (_wfpManager.IsInitialized && binding.IsActive && binding.RoutingMethod == RoutingMethod.Transparent)
        {
            bool filterOk = _wfpManager.AddBindingFilter(binding);
            if (!filterOk)
            {
                Console.WriteLine($"[PipeServer] Warning: WFP filter failed for binding {binding.ProcessName}. " +
                                  "SOCKS5 proxy is still available.");
            }
        }

        // Update WinDivert redirector bindings
        _redirector.UpdateBindings(_configManager.Config.Bindings);

        return new GenericResponse { Success = true };
    }

    private GenericResponse HandleRemoveBinding(string rawJson)
    {
        var req = JsonSerializer.Deserialize<RemoveBindingRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid RemoveBinding request" };

        if (_wfpManager.IsInitialized)
            _wfpManager.RemoveBindingFilter(req.BindingId);

        _configManager.RemoveBinding(req.BindingId);

        // Update WinDivert redirector bindings
        _redirector.UpdateBindings(_configManager.Config.Bindings);

        return new GenericResponse { Success = true };
    }

    private GenericResponse HandleUpdateBinding(string rawJson)
    {
        var req = JsonSerializer.Deserialize<UpdateBindingRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid UpdateBinding request" };

        _configManager.UpdateBinding(req.BindingId, req.NewInterfaceIndex, req.NewInterfaceName);

        if (_wfpManager.IsInitialized)
        {
            var binding = _configManager.Config.Bindings.FirstOrDefault(b => b.Id == req.BindingId);
            if (binding != null && binding.IsActive)
                _wfpManager.AddBindingFilter(binding);
        }

        // Update WinDivert redirector bindings
        _redirector.UpdateBindings(_configManager.Config.Bindings);

        return new GenericResponse { Success = true };
    }

    private GenericResponse HandleToggleBinding(string rawJson)
    {
        var req = JsonSerializer.Deserialize<ToggleBindingRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid ToggleBinding request" };

        _configManager.ToggleBinding(req.BindingId, req.IsActive);

        if (_wfpManager.IsInitialized)
        {
            if (req.IsActive)
            {
                var binding = _configManager.Config.Bindings.FirstOrDefault(b => b.Id == req.BindingId);
                if (binding != null && binding.RoutingMethod == RoutingMethod.Transparent)
                    _wfpManager.AddBindingFilter(binding);
            }
            else
            {
                _wfpManager.RemoveBindingFilter(req.BindingId);
            }
        }

        // Update WinDivert redirector bindings
        _redirector.UpdateBindings(_configManager.Config.Bindings);

        return new GenericResponse { Success = true };
    }

    private GenericResponse HandleSetMetric(string rawJson)
    {
        var req = JsonSerializer.Deserialize<SetMetricRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid SetMetric request" };

        var adapter = NativeInterop.IphlpApiWrapper.GetNetworkAdapters()
            .FirstOrDefault(a => a.InterfaceIndex == req.InterfaceIndex);
        if (adapter == null)
            return new GenericResponse { Success = false, Error = $"Network interface {req.InterfaceIndex} was not found" };

        // Preserve the system metric from before the first override. Subsequent edits
        // must not replace it with the already-customized metric.
        var existingOverride = _configManager.Config.MetricOverrides
            .FirstOrDefault(m => m.InterfaceIndex == req.InterfaceIndex);
        uint originalMetric = existingOverride?.OriginalMetric ?? adapter.InterfaceMetric;
        bool success = NativeInterop.IphlpApiWrapper.SetInterfaceMetric(req.InterfaceIndex, req.Metric);
        if (success)
        {
            _configManager.SetMetricOverride(req.InterfaceIndex, adapter.FriendlyName, originalMetric, req.Metric);
        }

        return new GenericResponse { Success = success, Error = success ? null : "Failed to set interface metric" };
    }

    private GenericResponse HandleResetMetric(string rawJson)
    {
        var req = JsonSerializer.Deserialize<ResetMetricRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid ResetMetric request" };

        var metricOverride = _configManager.Config.MetricOverrides
            .FirstOrDefault(m => m.InterfaceIndex == req.InterfaceIndex);
        if (metricOverride == null)
            return new GenericResponse { Success = false, Error = "No metric override exists for this interface" };

        bool success = NativeInterop.IphlpApiWrapper.SetInterfaceMetric(req.InterfaceIndex, metricOverride.OriginalMetric);
        if (success)
            _configManager.ResetMetricOverride(req.InterfaceIndex);

        return new GenericResponse { Success = success, Error = success ? null : "Failed to restore the original interface metric" };
    }

    private GetProxyInfoResponse HandleGetProxyInfo()
    {
        var result = new List<ProxyInfo>();
        foreach (var proxy in _proxyManager.Proxies.Values)
        {
            result.Add(new ProxyInfo
            {
                InterfaceIndex = proxy.InterfaceIndex,
                InterfaceName = proxy.InterfaceName,
                ProxyAddress = proxy.ProxyAddress,
                Port = proxy.ListenPort,
                IsRunning = proxy.IsRunning
            });
        }
        return new GetProxyInfoResponse { Proxies = result };
    }

    private GenericResponse HandleStartProxy(string rawJson)
    {
        var req = JsonSerializer.Deserialize<StartProxyRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid StartProxy request" };

        var proxy = _proxyManager.StartProxy(req.InterfaceIndex, req.InterfaceName);
        return new GenericResponse { Success = proxy != null, Error = proxy != null ? null : "Failed to start proxy" };
    }

    private GenericResponse HandleStopProxy(string rawJson)
    {
        var req = JsonSerializer.Deserialize<StopProxyRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid StopProxy request" };

        _proxyManager.StopProxy(req.InterfaceIndex);
        return new GenericResponse { Success = true };
    }

    private GenericResponse HandleSetRoutingMethod(string rawJson)
    {
        var req = JsonSerializer.Deserialize<SetRoutingMethodRequest>(rawJson, JsonOptions);
        if (req == null)
            return new GenericResponse { Success = false, Error = "Invalid SetRoutingMethod request" };

        bool ok = _configManager.SetRoutingMethod(req.BindingId, req.RoutingMethod);
        if (ok)
        {
            var binding = _configManager.Config.Bindings.FirstOrDefault(b => b.Id == req.BindingId);
            if (binding != null && _wfpManager.IsInitialized)
            {
                if (binding.IsActive && binding.RoutingMethod == RoutingMethod.Transparent)
                {
                    _wfpManager.AddBindingFilter(binding);
                }
                else
                {
                    _wfpManager.RemoveBindingFilter(binding.Id);
                }
            }

            _redirector.UpdateBindings(_configManager.Config.Bindings);
        }

        return new GenericResponse { Success = ok, Error = ok ? null : "Failed to set routing method" };
    }

    private static async Task SendResponseAsync(NamedPipeServerStream pipe, PipeResponse response, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(response, response.GetType(), JsonOptions);
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        await PipeFraming.WriteMessageAsync(pipe, payload, ct);
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