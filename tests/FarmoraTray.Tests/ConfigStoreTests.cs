using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FarmoraTray.Models;
using FarmoraTray.Services;

namespace FarmoraTray.Tests;

public class ConfigStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void NullByteConfig_StartsWithDefaultsAndPreservesTheDamagedFile()
    {
        using var dir = new TempDirectory();
        var damaged = new byte[128];
        File.WriteAllBytes(ConfigPath(dir), damaged);

        var store = Open(dir);

        AssertStartedFromDefaults(store, "null bytes");
        var quarantine = AssertQuarantined(dir, damaged);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(Path.GetFileName(quarantine), StringComparison.Ordinal));
        AssertSavedDefaults(store);
        Assert.False(File.Exists(BackupPath(dir)));
        AssertNoTempFiles(dir);
        AssertRestartKeepsKey(dir, store.Current.ApiKey);
        Assert.Equal(damaged, File.ReadAllBytes(quarantine));
        Assert.Single(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
    }

    [Fact]
    public void EmptyConfig_StartsWithDefaultsAndPreservesTheDamagedFile()
    {
        using var dir = new TempDirectory();
        var damaged = Array.Empty<byte>();
        File.WriteAllBytes(ConfigPath(dir), damaged);

        var store = Open(dir);

        AssertStartedFromDefaults(store, "empty");
        var quarantine = AssertQuarantined(dir, damaged);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(Path.GetFileName(quarantine), StringComparison.Ordinal));
        AssertSavedDefaults(store);
        Assert.False(File.Exists(BackupPath(dir)));
        AssertNoTempFiles(dir);
        AssertRestartKeepsKey(dir, store.Current.ApiKey);
    }

    [Fact]
    public void TruncatedConfig_StartsWithDefaultsAndPreservesTheDamagedFile()
    {
        using var dir = new TempDirectory();
        var damaged = Encoding.UTF8.GetBytes("{\"apiKey\":\"truncated-key\",\"port\":9123,\"printers\":{");
        File.WriteAllBytes(ConfigPath(dir), damaged);

        var store = Open(dir);

        Assert.True(store.ApiKeyWasGenerated);
        Assert.NotEqual("truncated-key", store.Current.ApiKey);
        Assert.Matches("^[0-9a-f]{64}$", store.Current.ApiKey);
        Assert.Equal(9123, store.Current.Port);
        Assert.NotEmpty(store.StartupWarnings);
        var quarantine = AssertQuarantined(dir, damaged);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(Path.GetFileName(quarantine), StringComparison.Ordinal));
        AssertSavedDefaults(store);
        AssertNoTempFiles(dir);
        AssertRestartKeepsKey(dir, store.Current.ApiKey);
    }

    [Fact]
    public void CorruptConfig_WithValidBackup_RestoresBackup()
    {
        using var dir = new TempDirectory();
        var damaged = new byte[64];
        var backup = SettingsJson(
            "bak-key",
            "https://app.farmora.example",
            dotMatrix: "EPSON LX-310",
            thermal: "POS-80");
        var backupBytes = Encoding.UTF8.GetBytes(backup);
        File.WriteAllBytes(ConfigPath(dir), damaged);
        File.WriteAllBytes(BackupPath(dir), backupBytes);

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Equal("bak-key", store.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", store.Current.AllowedOrigin);
        Assert.Equal(9123, store.Current.Port);
        Assert.Equal("EPSON LX-310", store.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", store.Current.Printers.Thermal);
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Contains(store.StartupWarnings, warning => warning.Contains("config.json.bak", StringComparison.Ordinal));

        var saved = ReadDisk(store.ConfigPath);
        Assert.Equal("bak-key", saved.ApiKey);
        Assert.Equal("https://app.farmora.example", saved.AllowedOrigin);
        Assert.Equal("EPSON LX-310", saved.Printers.DotMatrix);
        Assert.Equal("POS-80", saved.Printers.Thermal);
        var quarantine = AssertQuarantined(dir, damaged);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(Path.GetFileName(quarantine), StringComparison.Ordinal));
        AssertNoTempFiles(dir);

        AssertRestartKeepsKey(dir, "bak-key");
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Equal(damaged, File.ReadAllBytes(quarantine));
    }

    [Fact]
    public void NullJson_WithValidBackup_RestoresBackup()
    {
        using var dir = new TempDirectory();
        var damaged = Encoding.UTF8.GetBytes("null");
        var backupBytes = Encoding.UTF8.GetBytes(SettingsJson("from-backup", dotMatrix: "MATRIX"));
        File.WriteAllBytes(ConfigPath(dir), damaged);
        File.WriteAllBytes(BackupPath(dir), backupBytes);

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Equal("from-backup", store.Current.ApiKey);
        Assert.Equal("MATRIX", store.Current.Printers.DotMatrix);
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Equal("from-backup", ReadDisk(store.ConfigPath).ApiKey);
        AssertQuarantined(dir, damaged);
        Assert.NotEmpty(store.StartupWarnings);
        AssertRestartKeepsKey(dir, "from-backup");
    }

    [Fact]
    public void MissingConfig_WithValidBackup_RestoresBackup()
    {
        using var dir = new TempDirectory();
        var backupBytes = Encoding.UTF8.GetBytes(SettingsJson(
            "from-backup",
            "https://app.farmora.example",
            dotMatrix: "EPSON LX-310",
            thermal: "POS-80"));
        File.WriteAllBytes(BackupPath(dir), backupBytes);

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Equal("from-backup", store.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", store.Current.AllowedOrigin);
        Assert.Equal("EPSON LX-310", store.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", store.Current.Printers.Thermal);
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Equal("from-backup", ReadDisk(store.ConfigPath).ApiKey);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains("config.json.bak", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
        AssertNoTempFiles(dir);
        AssertRestartKeepsKey(dir, "from-backup");
    }

    [Fact]
    public void CorruptConfig_WithCorruptBackup_StartsWithDefaults()
    {
        using var dir = new TempDirectory();
        var damaged = new byte[32];
        var damagedBackup = Encoding.UTF8.GetBytes("{\"apiKey\":");
        File.WriteAllBytes(ConfigPath(dir), damaged);
        File.WriteAllBytes(BackupPath(dir), damagedBackup);

        var store = Open(dir);

        AssertStartedFromDefaults(store, "default");
        Assert.Equal(damagedBackup, File.ReadAllBytes(BackupPath(dir)));
        var quarantine = AssertQuarantined(dir, damaged);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(Path.GetFileName(quarantine), StringComparison.Ordinal));
        Assert.Equal(store.Current.ApiKey, ReadDisk(store.ConfigPath).ApiKey);
        AssertNoTempFiles(dir);
        AssertRestartKeepsKey(dir, store.Current.ApiKey);
        Assert.Equal(damagedBackup, File.ReadAllBytes(BackupPath(dir)));
        Assert.Equal(damaged, File.ReadAllBytes(quarantine));
    }

    [Fact]
    public void AtomicWrite_RoundTripsSettings()
    {
        using var dir = new TempDirectory();
        var store = Open(dir);
        Assert.True(store.ApiKeyWasGenerated);
        Assert.Equal(9123, store.Current.Port);

        var updated = store.Update(new UpdateConfigRequest
        {
            AllowedOrigin = "https://app.farmora.example",
            Printers = new PrinterMappingsUpdate
            {
                DotMatrix = "EPSON LX-310",
                Thermal = "POS-80"
            }
        });

        Assert.Equal("https://app.farmora.example", updated.AllowedOrigin);
        Assert.Equal("EPSON LX-310", updated.Printers.DotMatrix);
        Assert.Equal("POS-80", updated.Printers.Thermal);
        Assert.Matches("^[0-9a-f]{64}$", updated.ApiKey);

        var onDisk = ReadDisk(store.ConfigPath);
        Assert.Equal(updated.ApiKey, onDisk.ApiKey);
        Assert.Equal("https://app.farmora.example", onDisk.AllowedOrigin);
        Assert.Equal(9123, onDisk.Port);
        Assert.Equal("EPSON LX-310", onDisk.Printers.DotMatrix);
        Assert.Equal("POS-80", onDisk.Printers.Thermal);
        AssertNoTempFiles(dir);

        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var backupBytes = File.ReadAllBytes(BackupPath(dir));
        var again = Open(dir);
        Assert.Equal(updated.ApiKey, again.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", again.Current.AllowedOrigin);
        Assert.Equal("EPSON LX-310", again.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", again.Current.Printers.Thermal);
        Assert.Equal(9123, again.Current.Port);
        Assert.False(again.ApiKeyWasGenerated);
        Assert.Empty(again.StartupWarnings);
        Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
    }

    [Fact]
    public void ReplacingExistingConfig_KeepsThePreviousFileAsBackup()
    {
        using var dir = new TempDirectory();
        var store = Open(dir);
        var before = File.ReadAllBytes(store.ConfigPath);
        Assert.False(File.Exists(BackupPath(dir)));

        store.Update(new UpdateConfigRequest
        {
            AllowedOrigin = "https://app.farmora.example",
            Printers = new PrinterMappingsUpdate
            {
                DotMatrix = "EPSON LX-310",
                Thermal = "POS-80"
            }
        });

        Assert.Equal(before, File.ReadAllBytes(BackupPath(dir)));
        Assert.NotEqual(before, File.ReadAllBytes(store.ConfigPath));
        var saved = ReadDisk(store.ConfigPath);
        Assert.Equal(store.Current.ApiKey, saved.ApiKey);
        Assert.Equal("https://app.farmora.example", saved.AllowedOrigin);
        Assert.Equal(9123, saved.Port);
        Assert.Equal("EPSON LX-310", saved.Printers.DotMatrix);
        Assert.Equal("POS-80", saved.Printers.Thermal);
        var previous = ReadDisk(BackupPath(dir));
        Assert.Equal(store.Current.ApiKey, previous.ApiKey);
        Assert.Null(previous.AllowedOrigin);
        Assert.Null(previous.Printers.DotMatrix);
        Assert.Null(previous.Printers.Thermal);
        AssertNoTempFiles(dir);
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
    }

    [Fact]
    public void ExistingHealthyConfig_IsNotRewrittenOnStartup()
    {
        using var dir = new TempDirectory();
        var json = SettingsJson(
            "stable-key",
            "https://app.farmora.example",
            dotMatrix: "EPSON LX-310",
            thermal: "POS-80");
        File.WriteAllText(ConfigPath(dir), json);
        var before = File.ReadAllBytes(ConfigPath(dir));

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Empty(store.StartupWarnings);
        Assert.Equal("stable-key", store.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", store.Current.AllowedOrigin);
        Assert.Equal(9123, store.Current.Port);
        Assert.Equal("EPSON LX-310", store.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", store.Current.Printers.Thermal);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath(dir)));
        Assert.False(File.Exists(BackupPath(dir)));
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
        AssertNoTempFiles(dir);
    }

    [Fact]
    public void ExistingHealthyConfig_WithWindowsLineEndings_IsNotRewritten()
    {
        using var dir = new TempDirectory();
        var json = SettingsJson(
            "stable-key",
            "https://app.farmora.example",
            dotMatrix: "EPSON LX-310",
            thermal: "POS-80");
        json = json.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        var before = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(ConfigPath(dir), before);

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Empty(store.StartupWarnings);
        Assert.Equal("stable-key", store.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", store.Current.AllowedOrigin);
        Assert.Equal("EPSON LX-310", store.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", store.Current.Printers.Thermal);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath(dir)));
        Assert.False(File.Exists(BackupPath(dir)));
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
    }

    [Fact]
    public void ValidConfig_IsLeftAloneWhenABackupExists()
    {
        using var dir = new TempDirectory();
        var store = Open(dir);
        var key = store.Current.ApiKey;
        var mainBytes = File.ReadAllBytes(store.ConfigPath);
        var backupBytes = Encoding.UTF8.GetBytes(SettingsJson("other-key", dotMatrix: "OTHER"));
        File.WriteAllBytes(BackupPath(dir), backupBytes);

        var again = Open(dir);

        Assert.Equal(key, again.Current.ApiKey);
        Assert.NotEqual("other-key", again.Current.ApiKey);
        Assert.Empty(again.StartupWarnings);
        Assert.Equal(mainBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(backupBytes, File.ReadAllBytes(BackupPath(dir)));
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
        AssertNoTempFiles(dir);
    }

    [Fact]
    public void LegacyPrinterKeys_AreMigratedAndThePreviousFileIsKeptAsBackup()
    {
        using var dir = new TempDirectory();
        var legacy = """
            {
              "apiKey": "legacy-key",
              "allowedOrigin": "https://app.farmora.example",
              "port": 9123,
              "printers": {
                "saleOrderDotMatrix": "EPSON LX-310",
                "saleOrderThermal": "POS-80"
              }
            }
            """;
        var legacyBytes = Encoding.UTF8.GetBytes(legacy);
        File.WriteAllBytes(ConfigPath(dir), legacyBytes);

        var store = Open(dir);

        Assert.False(store.ApiKeyWasGenerated);
        Assert.Equal("legacy-key", store.Current.ApiKey);
        Assert.Equal("https://app.farmora.example", store.Current.AllowedOrigin);
        Assert.Equal("EPSON LX-310", store.Current.Printers.DotMatrix);
        Assert.Equal("POS-80", store.Current.Printers.Thermal);
        Assert.Equal(legacyBytes, File.ReadAllBytes(BackupPath(dir)));
        var rewritten = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"dotMatrix\"", rewritten);
        Assert.Contains("EPSON LX-310", rewritten);
        Assert.Contains("\"thermal\"", rewritten);
        Assert.Contains("POS-80", rewritten);
        Assert.DoesNotContain("saleOrderDotMatrix", rewritten);
        Assert.DoesNotContain("saleOrderThermal", rewritten);
        Assert.Empty(Directory.GetFiles(dir.Path, "config.corrupt-*.json"));
        AssertNoTempFiles(dir);
    }

    private static ConfigStore Open(TempDirectory dir)
    {
        var store = new ConfigStore(dir.Path);
        store.LoadOrCreate();
        return store;
    }

    private static void AssertStartedFromDefaults(ConfigStore store, string warningText)
    {
        Assert.True(store.ApiKeyWasGenerated);
        Assert.Matches("^[0-9a-f]{64}$", store.Current.ApiKey);
        Assert.Equal(9123, store.Current.Port);
        Assert.Null(store.Current.AllowedOrigin);
        Assert.Null(store.Current.Printers.DotMatrix);
        Assert.Null(store.Current.Printers.Thermal);
        Assert.Contains(store.StartupWarnings, warning => warning.Contains(warningText, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertSavedDefaults(ConfigStore store)
    {
        var saved = ReadDisk(store.ConfigPath);
        Assert.Equal(store.Current.ApiKey, saved.ApiKey);
        Assert.Equal(9123, saved.Port);
        Assert.DoesNotContain((byte)0, File.ReadAllBytes(store.ConfigPath));
    }

    private static void AssertRestartKeepsKey(TempDirectory dir, string apiKey)
    {
        var again = Open(dir);
        Assert.Equal(apiKey, again.Current.ApiKey);
        Assert.False(again.ApiKeyWasGenerated);
        Assert.Empty(again.StartupWarnings);
        AssertNoTempFiles(dir);
    }

    private static string AssertQuarantined(TempDirectory dir, byte[] expectedBytes)
    {
        var matches = Directory.GetFiles(dir.Path, "config.corrupt-*.json");
        var match = Assert.Single(matches);
        var name = Path.GetFileName(match);
        var parsed = Regex.Match(name, @"^config\.corrupt-(\d{14})(?:-\d+)?\.json$");
        Assert.True(parsed.Success, name);
        var stamp = DateTime.ParseExact(
            parsed.Groups[1].Value,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var skew = DateTime.UtcNow - stamp;
        Assert.InRange(skew.TotalSeconds, -5, 120);
        Assert.Equal(expectedBytes, File.ReadAllBytes(match));
        return match;
    }

    private static TrayConfig ReadDisk(string path)
    {
        var loaded = JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(path), JsonOptions);
        Assert.NotNull(loaded);
        return loaded;
    }

    private static string SettingsJson(
        string apiKey,
        string? allowedOrigin = null,
        int port = 9123,
        string? dotMatrix = null,
        string? thermal = null)
    {
        return JsonSerializer.Serialize(new TrayConfig
        {
            ApiKey = apiKey,
            AllowedOrigin = allowedOrigin,
            Port = port,
            Printers = new PrinterMappings
            {
                DotMatrix = dotMatrix,
                Thermal = thermal
            }
        }, JsonOptions);
    }

    private static string ConfigPath(TempDirectory dir) => Path.Combine(dir.Path, "config.json");

    private static string BackupPath(TempDirectory dir) => Path.Combine(dir.Path, "config.json.bak");

    private static void AssertNoTempFiles(TempDirectory dir)
    {
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "farmora-tray-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
