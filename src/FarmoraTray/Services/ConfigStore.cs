using System.Security.Cryptography;
using System.Text.Json;
using FarmoraTray.Models;

namespace FarmoraTray.Services;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _configPath;
    private TrayConfig _current = new();
    private bool _apiKeyWasGenerated;

    public ConfigStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FarmoraTray");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
    }

    public string ConfigPath => _configPath;

    public TrayConfig Current
    {
        get
        {
            lock (_gate)
            {
                return Clone(_current);
            }
        }
    }

    public bool ApiKeyWasGenerated => _apiKeyWasGenerated;

    public void LoadOrCreate()
    {
        lock (_gate)
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<TrayConfig>(json, JsonOptions) ?? new TrayConfig();
                MigrateLegacyPrinters(loaded, json);
                Normalize(loaded);
                _current = loaded;
                _apiKeyWasGenerated = false;
                PersistUnlocked(); // rewrite in simplified shape if legacy keys were present
                return;
            }

            _current = new TrayConfig
            {
                ApiKey = GenerateApiKey(),
                Port = 9123,
                Printers = new PrinterMappings()
            };
            _apiKeyWasGenerated = true;
            PersistUnlocked();
        }
    }

    public TrayConfig Update(UpdateConfigRequest request)
    {
        lock (_gate)
        {
            if (request.AllowedOrigin is not null)
            {
                _current.AllowedOrigin = string.IsNullOrWhiteSpace(request.AllowedOrigin)
                    ? null
                    : request.AllowedOrigin.Trim();
            }

            if (request.Printers is not null)
            {
                ApplyPrinterUpdate(_current.Printers, request.Printers);
            }

            PersistUnlocked();
            return Clone(_current);
        }
    }

    public string? GetPrinter(PrintMode mode)
    {
        var config = Current;
        return mode switch
        {
            PrintMode.DotMatrix => config.Printers.DotMatrix,
            PrintMode.Thermal => config.Printers.Thermal,
            _ => null
        };
    }

    public bool IsApiKeyValid(string? provided)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expected = Current.ApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var providedBytes = System.Text.Encoding.UTF8.GetBytes(provided);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        var allowed = Current.AllowedOrigin;
        // Bootstrap: until allowedOrigin is configured, accept any browser origin so the FE can call PUT /config.
        if (string.IsNullOrEmpty(allowed))
        {
            return true;
        }

        return string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistUnlocked()
    {
        var json = JsonSerializer.Serialize(_current, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static void Normalize(TrayConfig config)
    {
        if (config.Port <= 0 || config.Port > 65535)
        {
            config.Port = 9123;
        }

        config.Printers ??= new PrinterMappings();
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            config.ApiKey = GenerateApiKey();
        }
    }

    /// <summary>
    /// Maps older per-document printer keys into dotMatrix / thermal.
    /// </summary>
    private static void MigrateLegacyPrinters(TrayConfig config, string json)
    {
        config.Printers ??= new PrinterMappings();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("printers", out var printers)
            || printers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.Printers.DotMatrix))
        {
            config.Printers.DotMatrix =
                ReadString(printers, "dotMatrix")
                ?? ReadString(printers, "saleOrderDotMatrix")
                ?? ReadString(printers, "salesReturn")
                ?? ReadString(printers, "purchaseOrder")
                ?? ReadString(printers, "purchaseReturn");
        }

        if (string.IsNullOrWhiteSpace(config.Printers.Thermal))
        {
            config.Printers.Thermal =
                ReadString(printers, "thermal")
                ?? ReadString(printers, "saleOrderThermal");
        }
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var s = value.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static void ApplyPrinterUpdate(PrinterMappings target, PrinterMappingsUpdate update)
    {
        if (update.DotMatrix is not null)
        {
            target.DotMatrix = EmptyToNull(update.DotMatrix);
        }

        if (update.Thermal is not null)
        {
            target.Thermal = EmptyToNull(update.Thermal);
        }
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GenerateApiKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static TrayConfig Clone(TrayConfig source) => new()
    {
        ApiKey = source.ApiKey,
        AllowedOrigin = source.AllowedOrigin,
        Port = source.Port,
        Printers = new PrinterMappings
        {
            DotMatrix = source.Printers.DotMatrix,
            Thermal = source.Printers.Thermal
        }
    };
}
