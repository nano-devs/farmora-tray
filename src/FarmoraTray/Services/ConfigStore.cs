using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly List<string> _startupWarnings = new();
    private TrayConfig _current = new();
    private bool _apiKeyWasGenerated;

    public ConfigStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FarmoraTray"))
    {
    }

    public ConfigStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _backupPath = Path.Combine(_directory, "config.json.bak");
    }

    public string ConfigPath => _configPath;

    public IReadOnlyList<string> StartupWarnings
    {
        get
        {
            lock (_gate)
            {
                return _startupWarnings.ToArray();
            }
        }
    }

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
            _startupWarnings.Clear();

            var main = ReadConfig(_configPath);
            if (main.IsValid)
            {
                _current = main.Config!;
                _apiKeyWasGenerated = false;
                // Keep a healthy file as it is, including its line endings and whitespace.
                // Rewrite when legacy keys or normalization changed the saved values.
                var rewritten = JsonSerializer.Serialize(_current, JsonOptions);
                if (!JsonValuesEqual(main.RawJson!, rewritten))
                {
                    PersistUnlocked();
                }

                return;
            }

            var backup = ReadConfig(_backupPath);
            string? quarantined = null;
            if (main.Exists)
            {
                quarantined = QuarantineDamagedMain();
            }

            if (backup.IsValid)
            {
                _current = backup.Config!;
                _apiKeyWasGenerated = false;
                if (main.Exists)
                {
                    Warn(
                        $"Could not read saved settings at '{_configPath}' ({main.Reason}). " +
                        $"Restored the last good copy from '{_backupPath}'. " +
                        $"The damaged file was moved to '{quarantined}'.");
                }
                else
                {
                    Warn(
                        $"Saved settings file '{_configPath}' is missing. " +
                        $"Restored the last good copy from '{_backupPath}'.");
                }

                PersistUnlocked();
                return;
            }

            if (main.Exists || backup.Exists)
            {
                Warn(DescribeUnusable(main, backup, quarantined));
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

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
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
        var tempPath = Path.Combine(_directory, $"config.json.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.None))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(true);
            }

            if (File.Exists(_configPath))
            {
                File.Replace(tempPath, _configPath, _backupPath);
            }
            else
            {
                File.Move(tempPath, _configPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private ConfigSnapshot ReadConfig(string path)
    {
        if (!File.Exists(path))
        {
            return new ConfigSnapshot(false, false, null, null, "the file is missing");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigSnapshot(true, false, null, null, ex.Message);
        }

        if (json.Length == 0)
        {
            return new ConfigSnapshot(true, false, null, null, "the file is empty");
        }

        if (IsOnlyNullBytes(json))
        {
            return new ConfigSnapshot(true, false, null, null, "the file contains only null bytes");
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<TrayConfig>(json, JsonOptions);
            if (loaded is null)
            {
                return new ConfigSnapshot(true, false, null, null, "the file deserialized to null");
            }

            MigrateLegacyPrinters(loaded, json);
            Normalize(loaded);
            return new ConfigSnapshot(true, true, loaded, json, "");
        }
        catch (JsonException ex)
        {
            return new ConfigSnapshot(true, false, null, null, ex.Message);
        }
    }

    private string QuarantineDamagedMain()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(_directory, $"config.corrupt-{stamp}.json");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(_directory, $"config.corrupt-{stamp}-{suffix}.json");
            suffix++;
        }

        File.Move(_configPath, candidate);
        return candidate;
    }

    private string DescribeUnusable(ConfigSnapshot main, ConfigSnapshot backup, string? quarantined)
    {
        if (main.Exists && backup.Exists)
        {
            var moved = quarantined is null
                ? ""
                : $" Moved the damaged file to '{quarantined}'.";
            return
                $"Could not read saved settings at '{_configPath}' ({main.Reason}). " +
                $"The backup '{_backupPath}' could not be used ({backup.Reason})." +
                moved +
                " Started with default settings.";
        }

        if (main.Exists)
        {
            var moved = quarantined is null
                ? ""
                : $" Moved the damaged file to '{quarantined}'.";
            return
                $"Could not read saved settings at '{_configPath}' ({main.Reason}). " +
                "No backup was found." +
                moved +
                " Started with default settings.";
        }

        return
            $"Saved settings file '{_configPath}' is missing. " +
            $"The backup '{_backupPath}' could not be used ({backup.Reason}). " +
            "Started with default settings.";
    }

    private void Warn(string message)
    {
        _startupWarnings.Add(message);
    }

    private static bool JsonValuesEqual(string existing, string rewritten)
    {
        try
        {
            var left = JsonNode.Parse(existing);
            var right = JsonNode.Parse(rewritten);
            return JsonNode.DeepEquals(left, right);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsOnlyNullBytes(string json)
    {
        foreach (var ch in json)
        {
            if (ch != '\0')
            {
                return false;
            }
        }

        return json.Length > 0;
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

    private readonly record struct ConfigSnapshot(
        bool Exists,
        bool IsValid,
        TrayConfig? Config,
        string? RawJson,
        string Reason);
}
