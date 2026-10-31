using System.Text.Json;
using NetBinder.Shared.Models;

namespace NetBinder.Service.Services;

/// <summary>
/// Manages loading and saving the NetBinder configuration to %APPDATA%\NetBinder\config.json.
/// Configuration includes: binding mappings and interface metric overrides.
/// This ensures persistence across application restarts.
/// </summary>
public class ConfigManager
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NetBinder");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private NetBinderConfig _config = new();

    /// <summary>Current in-memory configuration.</summary>
    public NetBinderConfig Config => _config;

    /// <summary>Event fired when the configuration changes.</summary>
    public event Action<NetBinderConfig>? ConfigChanged;

    public ConfigManager()
    {
        Load();
    }

    /// <summary>
    /// Loads configuration from disk. Creates default config if file doesn't exist.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                _config = JsonSerializer.Deserialize<NetBinderConfig>(json, JsonOptions) ?? new NetBinderConfig();
            }
            else
            {
                _config = new NetBinderConfig();
                Save();
            }
        }
        catch (Exception ex)
        {
            // Preserve a potentially recoverable config before starting with defaults.
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string backupPath = ConfigFilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    File.Copy(ConfigFilePath, backupPath, overwrite: false);
                    Console.WriteLine($"[ConfigManager] Preserved unreadable config at {backupPath}");
                }
            }
            catch (Exception backupException)
            {
                Console.WriteLine($"[ConfigManager] Could not preserve unreadable config: {backupException.Message}");
            }

            _config = new NetBinderConfig();
            Console.WriteLine($"[ConfigManager] Failed to load config: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves current configuration to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            _config.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            string tempPath = ConfigFilePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(tempPath, json);
                // Keep the temporary file in the same directory so replacement stays
                // on the same volume and the live config is never partially written.
                File.Move(tempPath, ConfigFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConfigManager] Failed to save config: {ex.Message}");
        }
    }

    // === Binding Mapping Operations ===

    public BindingMapping AddBinding(string executablePath, string processName, int interfaceIndex, string interfaceName, RoutingMethod routingMethod = RoutingMethod.Transparent)
    {
        // Check if binding already exists for this executable
        var existing = _config.Bindings.FirstOrDefault(b =>
            b.ExecutablePath.Equals(executablePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Update existing binding
            existing.InterfaceIndex = interfaceIndex;
            existing.InterfaceName = interfaceName;
            existing.IsActive = true;
            existing.RoutingMethod = routingMethod;
            Save();
            ConfigChanged?.Invoke(_config);
            return existing;
        }

        var binding = new BindingMapping
        {
            Id = Guid.NewGuid(),
            ExecutablePath = executablePath,
            ProcessName = processName,
            InterfaceIndex = interfaceIndex,
            InterfaceName = interfaceName,
            IsActive = true,
            RoutingMethod = routingMethod,
            CreatedAt = DateTime.UtcNow
        };

        _config.Bindings.Add(binding);
        Save();
        ConfigChanged?.Invoke(_config);
        return binding;
    }

    public bool SetRoutingMethod(Guid bindingId, RoutingMethod routingMethod)
    {
        var binding = _config.Bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding == null) return false;

        binding.RoutingMethod = routingMethod;
        Save();
        ConfigChanged?.Invoke(_config);
        return true;
    }

    public bool RemoveBinding(Guid bindingId)
    {
        var binding = _config.Bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding == null) return false;

        _config.Bindings.Remove(binding);
        Save();
        ConfigChanged?.Invoke(_config);
        return true;
    }

    public bool UpdateBinding(Guid bindingId, int newInterfaceIndex, string newInterfaceName)
    {
        var binding = _config.Bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding == null) return false;

        binding.InterfaceIndex = newInterfaceIndex;
        binding.InterfaceName = newInterfaceName;
        Save();
        ConfigChanged?.Invoke(_config);
        return true;
    }

    public bool ToggleBinding(Guid bindingId, bool isActive)
    {
        var binding = _config.Bindings.FirstOrDefault(b => b.Id == bindingId);
        if (binding == null) return false;

        binding.IsActive = isActive;
        Save();
        ConfigChanged?.Invoke(_config);
        return true;
    }

    // === Metric Override Operations ===

    public InterfaceMetricOverride SetMetricOverride(int interfaceIndex, string interfaceName, uint originalMetric, uint customMetric)
    {
        var existing = _config.MetricOverrides.FirstOrDefault(m => m.InterfaceIndex == interfaceIndex);
        if (existing != null)
        {
            existing.CustomMetric = customMetric;
            Save();
            ConfigChanged?.Invoke(_config);
            return existing;
        }

        var metricOverride = new InterfaceMetricOverride
        {
            InterfaceIndex = interfaceIndex,
            InterfaceName = interfaceName,
            OriginalMetric = originalMetric,
            CustomMetric = customMetric,
            IsApplied = false
        };

        _config.MetricOverrides.Add(metricOverride);
        Save();
        ConfigChanged?.Invoke(_config);
        return metricOverride;
    }

    public bool ResetMetricOverride(int interfaceIndex)
    {
        var existing = _config.MetricOverrides.FirstOrDefault(m => m.InterfaceIndex == interfaceIndex);
        if (existing == null) return false;

        _config.MetricOverrides.Remove(existing);
        Save();
        ConfigChanged?.Invoke(_config);
        return true;
    }
}
