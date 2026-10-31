using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using NetBinder.Shared.Models;
using NetBinder.Shared.Protocol;
using NetBinder.UI.Services;

namespace NetBinder.UI.ViewModels;

/// <summary>
/// UI Settings class for persisting state across application restarts.
/// </summary>
public class UiSettings
{
    public int SelectedTabIndex { get; set; }
    public string ProcessSearchFilter { get; set; } = string.Empty;
    public string DefaultRoutingMethod { get; set; } = "Transparent";
}

/// <summary>
/// Main ViewModel for the NetBinder application.
/// Manages the state of network adapters, processes, binding mappings, and proxy info.
/// Communicates with the service via the PipeClient.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _pipeClient;
    private readonly List<NetworkProcessInfo> _allProcesses = [];
    private bool _isConnected;
    private bool _isLoading;
    private string _statusMessage = "Ready";
    private NetworkAdapterInfo? _selectedAdapter;
    private NetworkProcessInfo? _selectedProcess;
    private BindingMapping? _selectedBinding;
    private int _selectedAdapterIndex = -1;
    private int _selectedProcessIndex = -1;
    private int _selectedBindingIndex = -1;
    private string _metricInput = "";
    private bool _isMetricInputValid = true;

    // Toast and Search State
    private string _toastMessage = "";
    private bool _isToastVisible;
    private string _processesLastUpdated = "Never";
    private string _processSearchFilter = "";
    private int _selectedTabIndex;
    private RoutingMethod _defaultRoutingMethod = RoutingMethod.Transparent;

    public MainViewModel()
    {
        _pipeClient = new PipeClient();

        // Load UI settings
        LoadUiSettings();

        RefreshAdaptersCommand = new AsyncRelayCommand(RefreshAdaptersAsync, () => !IsLoading && IsConnected);
        RefreshProcessesCommand = new AsyncRelayCommand(RefreshProcessesAsync, () => !IsLoading && IsConnected);
        BindCommand = new AsyncRelayCommand(BindAsync, () => CanBind() && IsConnected);
        RemoveBindingCommand = new AsyncRelayCommand(RemoveBindingAsync, () => SelectedBinding != null && IsConnected);
        ToggleBindingCommand = new AsyncRelayCommand(ToggleBindingAsync, () => SelectedBinding != null && IsConnected);
        ApplyMetricCommand = new AsyncRelayCommand(ApplyMetricAsync, () => SelectedAdapter != null && IsMetricInputValid && IsConnected);
        ResetMetricCommand = new AsyncRelayCommand(ResetMetricAsync, () => SelectedAdapter != null && IsConnected);
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsLoading && IsConnected);
        ConnectCommand = new AsyncRelayCommand(ManualConnectToServiceAsync, () => !IsConnected && !IsLoading);
        RemoveSpecificBindingCommand = new AsyncRelayCommand<BindingMapping>(RemoveSpecificBindingAsync, _ => IsConnected);
        ClearSearchCommand = new AsyncRelayCommand(() => { ProcessSearchFilter = ""; return Task.CompletedTask; });
        ToggleRoutingMethodCommand = new AsyncRelayCommand<BindingMapping>(ToggleRoutingMethodAsync, _ => IsConnected);

        // Start background auto-reconnect loop
        _ = AutoConnectLoopAsync();
    }

    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<NetworkProcessInfo> Processes { get; } = [];
    public ObservableCollection<BindingMapping> Bindings { get; } = [];
    public ObservableCollection<ProxyInfo> Proxies { get; } = [];

    public bool IsConnected { get => _isConnected; set { _isConnected = value; OnPropertyChanged(); } }
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }
    public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }
    public NetworkAdapterInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            _selectedAdapter = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
            if (_selectedAdapter != null)
            {
                MetricInput = _selectedAdapter.InterfaceMetric.ToString();
            }
        }
    }
    public NetworkProcessInfo? SelectedProcess { get => _selectedProcess; set { _selectedProcess = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
    public BindingMapping? SelectedBinding { get => _selectedBinding; set { _selectedBinding = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }
    public int SelectedAdapterIndex { get => _selectedAdapterIndex; set { _selectedAdapterIndex = value; OnPropertyChanged(); } }
    public int SelectedProcessIndex { get => _selectedProcessIndex; set { _selectedProcessIndex = value; OnPropertyChanged(); } }
    public int SelectedBindingIndex { get => _selectedBindingIndex; set { _selectedBindingIndex = value; OnPropertyChanged(); } }
    
    public string MetricInput
    {
        get => _metricInput;
        set
        {
            _metricInput = value;
            OnPropertyChanged();
            IsMetricInputValid = uint.TryParse(value, out var val) && val >= 1 && val <= 9999;
        }
    }
    public bool IsMetricInputValid { get => _isMetricInputValid; set { _isMetricInputValid = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); } }

    // Toast Notifications
    public string ToastMessage { get => _toastMessage; set { _toastMessage = value; OnPropertyChanged(); } }
    public bool IsToastVisible { get => _isToastVisible; set { _isToastVisible = value; OnPropertyChanged(); } }

    // Search and Auto-Refresh info
    public string ProcessesLastUpdated { get => _processesLastUpdated; set { _processesLastUpdated = value; OnPropertyChanged(); } }
    public string ProcessSearchFilter
    {
        get => _processSearchFilter;
        set
        {
            _processSearchFilter = value;
            OnPropertyChanged();
            ApplyProcessFilter();
            SaveUiSettings();
        }
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            _selectedTabIndex = value;
            OnPropertyChanged();
            SaveUiSettings();
        }
    }

    public RoutingMethod DefaultRoutingMethod
    {
        get => _defaultRoutingMethod;
        set
        {
            if (_defaultRoutingMethod != value)
            {
                _defaultRoutingMethod = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDefaultRoutingTransparent));
                OnPropertyChanged(nameof(IsDefaultRoutingProxyOnly));
                SaveUiSettings();
            }
        }
    }

    public bool IsDefaultRoutingTransparent
    {
        get => DefaultRoutingMethod == RoutingMethod.Transparent;
        set
        {
            if (value) DefaultRoutingMethod = RoutingMethod.Transparent;
        }
    }

    public bool IsDefaultRoutingProxyOnly
    {
        get => DefaultRoutingMethod == RoutingMethod.ProxyOnly;
        set
        {
            if (value) DefaultRoutingMethod = RoutingMethod.ProxyOnly;
        }
    }

    public ICommand RefreshAdaptersCommand { get; }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand BindCommand { get; }
    public ICommand RemoveBindingCommand { get; }
    public ICommand ToggleBindingCommand { get; }
    public ICommand ApplyMetricCommand { get; }
    public ICommand ResetMetricCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand RemoveSpecificBindingCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ToggleRoutingMethodCommand { get; }

    // Floating non-blocking notification helper
    public void ShowToast(string message, bool isError = false)
    {
        ToastMessage = (isError ? "⚠️ " : "ℹ️ ") + message;
        IsToastVisible = true;

        // Auto-hide after 3 seconds
        Task.Delay(3000).ContinueWith(_ =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (ToastMessage == (isError ? "⚠️ " : "ℹ️ ") + message)
                {
                    IsToastVisible = false;
                }
            });
        });
    }

    // Settings persistence helpers
    private void LoadUiSettings()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetBinder");
            var path = Path.Combine(dir, "ui_settings.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<UiSettings>(json);
                if (settings != null)
                {
                    _selectedTabIndex = settings.SelectedTabIndex;
                    _processSearchFilter = settings.ProcessSearchFilter;
                    if (Enum.TryParse<RoutingMethod>(settings.DefaultRoutingMethod, out var method))
                    {
                        _defaultRoutingMethod = method;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveUiSettings()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetBinder");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "ui_settings.json");
            var settings = new UiSettings
            {
                SelectedTabIndex = SelectedTabIndex,
                ProcessSearchFilter = ProcessSearchFilter,
                DefaultRoutingMethod = DefaultRoutingMethod.ToString()
            };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    // Process list local filtering
    private void ApplyProcessFilter()
    {
        Processes.Clear();
        var filter = ProcessSearchFilter.Trim();
        foreach (var p in _allProcesses)
        {
            if (string.IsNullOrEmpty(filter) ||
                p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Pid.ToString().Contains(filter) ||
                p.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Processes.Add(p);
            }
        }
    }

    // Auto-Connect Loop
    private readonly ServiceProcessLauncher _serviceLauncher = new();
    private DateTime _lastServiceStartAttemptUtc = DateTime.MinValue;
    private static readonly TimeSpan ServiceStartAttemptInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Launches the background service when the UI cannot reach it, so the user only
    /// has to start the UI. Retried at most every ten seconds so a broken or missing
    /// executable cannot spawn a new process on every poll.
    /// </summary>
    private async Task<bool> TryStartServiceAsync()
    {
        if (ServiceProcessLauncher.IsServiceRunning())
        {
            return false;
        }

        if (DateTime.UtcNow - _lastServiceStartAttemptUtc < ServiceStartAttemptInterval)
        {
            return false;
        }

        _lastServiceStartAttemptUtc = DateTime.UtcNow;
        StatusMessage = "Starting NetBinder Service...";

        bool connected = await _serviceLauncher.EnsureStartedAsync(_pipeClient, TimeSpan.FromSeconds(8));
        if (!connected && _serviceLauncher.LastError != null)
        {
            // Surface the reason instead of leaving a generic "not reachable" message:
            // a UI-only failure here is almost always a missing or non-startable service exe.
            StatusMessage = _serviceLauncher.LastError;
        }

        return true;
    }

    private async Task AutoConnectLoopAsync()
    {
        while (true)
        {
            if (!IsConnected && !IsLoading)
            {
                try
                {
                    await TryStartServiceAsync();

                    await _pipeClient.ConnectAsync();
                    IsConnected = true;
                    StatusMessage = "Connected to NetBinder Service.";
                    ShowToast("Connected to NetBinder Service");
                    await RefreshAllAsync();
                }
                catch
                {
                    IsConnected = false;
                    StatusMessage = "Service not reachable. Retrying in 5s...";
                }
            }
            await Task.Delay(5000);
        }
    }

    private async Task ManualConnectToServiceAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Connecting...";

            // A manual reconnect should not wait out the automatic retry window.
            _lastServiceStartAttemptUtc = DateTime.MinValue;
            await TryStartServiceAsync();

            await _pipeClient.ConnectAsync();
            IsConnected = true;
            StatusMessage = "Connected to NetBinder Service.";
            ShowToast("Connected to NetBinder Service");
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = $"Failed to connect: {ex.Message}";
            ShowToast($"Failed to connect: {ex.Message}", true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Pipe error handling wrapper
    private async Task ExecutePipeCallAsync(Func<Task> action, string successMessage, string startMessage = "")
    {
        if (!IsConnected)
        {
            ShowToast("Not connected to service", true);
            return;
        }

        try
        {
            IsLoading = true;
            if (!string.IsNullOrEmpty(startMessage))
            {
                StatusMessage = startMessage;
            }
            await action();
            if (!string.IsNullOrEmpty(successMessage))
            {
                StatusMessage = successMessage;
                ShowToast(successMessage);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            IsConnected = false;
            StatusMessage = "Connection lost. Reconnecting...";
            ShowToast("Connection to service lost", true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            ShowToast(ex.Message, true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GetAdaptersResponse>(new GetAdaptersRequest());
            Adapters.Clear();
            foreach (var a in r.Adapters) Adapters.Add(a);
        }, "", "Refreshing network adapters...");
    }

    private async Task RefreshProcessesAsync()
    {
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GetProcessesResponse>(new GetProcessesRequest());
            _allProcesses.Clear();
            _allProcesses.AddRange(r.Processes);
            ApplyProcessFilter();
            ProcessesLastUpdated = DateTime.Now.ToString("HH:mm:ss");
        }, "", "Refreshing running processes...");
    }

    private bool CanBind() => SelectedProcess != null && SelectedAdapter != null && !string.IsNullOrEmpty(SelectedProcess.FilePath) && !SelectedProcess.FilePath.StartsWith("<");

    private async Task BindAsync()
    {
        if (SelectedProcess == null || SelectedAdapter == null) return;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new AddBindingRequest
            {
                ExecutablePath = SelectedProcess.FilePath,
                ProcessName = SelectedProcess.ProcessName,
                InterfaceIndex = SelectedAdapter.InterfaceIndex,
                InterfaceName = SelectedAdapter.FriendlyName,
                RoutingMethod = DefaultRoutingMethod
            });
            if (r.Success)
            {
                await RefreshBindingsAsync();
                await RefreshProcessesAsync();
                await RefreshProxyInfoAsync();
            }
            else
            {
                throw new Exception($"Failed to bind: {r.Error}");
            }
        }, $"Bound {SelectedProcess.ProcessName} -> {SelectedAdapter.FriendlyName}", $"Binding {SelectedProcess.ProcessName}...");
    }

    private async Task RemoveBindingAsync()
    {
        if (SelectedBinding == null) return;
        var processName = SelectedBinding.ProcessName;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new RemoveBindingRequest { BindingId = SelectedBinding.Id });
            if (r.Success)
            {
                await RefreshBindingsAsync();
                await RefreshProcessesAsync();
                await RefreshProxyInfoAsync();
            }
            else
            {
                throw new Exception($"Failed to remove: {r.Error}");
            }
        }, $"Removed rule for {processName}", $"Removing binding for {processName}...");
    }

    private async Task RemoveSpecificBindingAsync(BindingMapping binding)
    {
        if (binding == null) return;
        var processName = binding.ProcessName;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new RemoveBindingRequest { BindingId = binding.Id });
            if (r.Success)
            {
                await RefreshBindingsAsync();
                await RefreshProcessesAsync();
                await RefreshProxyInfoAsync();
            }
            else
            {
                throw new Exception($"Failed to remove: {r.Error}");
            }
        }, $"Removed rule for {processName}", $"Removing binding for {processName}...");
    }

    private async Task ToggleBindingAsync()
    {
        if (SelectedBinding == null) return;
        var processName = SelectedBinding.ProcessName;
        var ns = !SelectedBinding.IsActive;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new ToggleBindingRequest { BindingId = SelectedBinding.Id, IsActive = ns });
            if (r.Success)
            {
                await RefreshBindingsAsync();
            }
            else
            {
                throw new Exception($"Failed to toggle: {r.Error}");
            }
        }, $"{(ns ? "Enabled" : "Disabled")} binding for {processName}");
    }

    private async Task ToggleRoutingMethodAsync(BindingMapping binding)
    {
        if (binding == null) return;
        var targetMethod = binding.RoutingMethod == RoutingMethod.Transparent ? RoutingMethod.ProxyOnly : RoutingMethod.Transparent;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new SetRoutingMethodRequest { BindingId = binding.Id, RoutingMethod = targetMethod });
            if (r.Success)
            {
                await RefreshBindingsAsync();
            }
            else
            {
                throw new Exception($"Failed to set routing method: {r.Error}");
            }
        }, $"Routing method set to {targetMethod} for {binding.ProcessName}", $"Updating routing method...");
    }

    private async Task ApplyMetricAsync()
    {
        if (SelectedAdapter == null) return;
        if (!uint.TryParse(MetricInput, out uint metric) || metric < 1 || metric > 9999)
        {
            ShowToast("Metric must be between 1 and 9999", true);
            return;
        }
        var friendlyName = SelectedAdapter.FriendlyName;
        await ExecutePipeCallAsync(async () =>
        {
            var r = new SetMetricRequest { InterfaceIndex = SelectedAdapter.InterfaceIndex, Metric = metric };
            var r2 = await _pipeClient.SendRequestAsync<GenericResponse>(r);
            if (r2.Success)
            {
                await RefreshAdaptersAsync();
            }
            else
            {
                throw new Exception($"Failed to set metric: {r2.Error}");
            }
        }, $"Set metric to {metric} for {friendlyName}", $"Applying metric for {friendlyName}...");
    }

    private async Task ResetMetricAsync()
    {
        if (SelectedAdapter == null) return;
        var friendlyName = SelectedAdapter.FriendlyName;
        await ExecutePipeCallAsync(async () =>
        {
            var r = await _pipeClient.SendRequestAsync<GenericResponse>(new ResetMetricRequest { InterfaceIndex = SelectedAdapter.InterfaceIndex });
            if (r.Success)
            {
                await RefreshAdaptersAsync();
            }
            else
            {
                throw new Exception($"Failed to reset metric: {r.Error}");
            }
        }, $"Reset metric for {friendlyName}", $"Resetting metric for {friendlyName}...");
    }

    private async Task RefreshBindingsAsync()
    {
        try
        {
            var r = await _pipeClient.SendRequestAsync<GetBindingsResponse>(new GetBindingsRequest());
            Bindings.Clear();
            foreach (var b in r.Bindings) Bindings.Add(b);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async Task RefreshProxyInfoAsync()
    {
        try
        {
            var r = await _pipeClient.SendRequestAsync<GetProxyInfoResponse>(new GetProxyInfoRequest());
            Proxies.Clear();
            foreach (var p in r.Proxies) Proxies.Add(p);
        }
        catch { /* Non-critical */ }
    }

    private async Task RefreshAllAsync()
    {
        await RefreshAdaptersAsync();
        await RefreshProcessesAsync();
        await RefreshBindingsAsync();
        await RefreshProxyInfoAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose() => _pipeClient.Dispose();
}

/// <summary>An ICommand implementation that supports async execution and CanExecute.</summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (_isExecuting) return;
        _isExecuting = true; CommandManager.InvalidateRequerySuggested();
        try { await _execute(); }
        finally { _isExecuting = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

/// <summary>An ICommand implementation that supports async execution with a parameter.</summary>
public class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T, Task> _execute;
    private readonly Func<T, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T, Task> execute, Func<T, bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        if (parameter is T val)
        {
            return _canExecute?.Invoke(val) ?? true;
        }
        return false;
    }
    public async void Execute(object? parameter)
    {
        if (_isExecuting || parameter is not T val) return;
        _isExecuting = true; CommandManager.InvalidateRequerySuggested();
        try { await _execute(val); }
        finally { _isExecuting = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}