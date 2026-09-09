using FarmoraTray.Apis;
using FarmoraTray.Services;
using Scalar.AspNetCore;

namespace FarmoraTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Farmora Tray could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Farmora Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Run(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\FarmoraTray", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Farmora Tray is already running. Look for the icon in the system tray.",
                "Farmora Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var configStore = new ConfigStore();
        configStore.LoadOrCreate();

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();

        builder.WebHost.UseUrls($"http://127.0.0.1:{configStore.Current.Port}");

        builder.Services.AddSingleton(configStore);
        builder.Services.AddSingleton<PrinterDiscovery>();
        builder.Services.AddSingleton<PdfPrintService>();
        builder.Services.AddSingleton<RawPrintService>();
        builder.Services.AddSingleton<PrintOrchestrator>();
        builder.Services.AddProblemDetails();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("frontend", policy =>
            {
                policy.SetIsOriginAllowed(origin => configStore.IsOriginAllowed(origin))
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        builder.Services.AddHealthChecks();

        var app = builder.Build();

        if (configStore.ApiKeyWasGenerated)
        {
            app.Logger.LogInformation(
                "Farmora Tray API key generated. Use Copy API key on the tray menu. Config file: {ConfigPath}",
                configStore.ConfigPath);
        }
        else
        {
            app.Logger.LogInformation(
                "Farmora Tray listening on http://127.0.0.1:{Port}. Config: {ConfigPath}",
                configStore.Current.Port,
                configStore.ConfigPath);
        }

        app.MapOpenApi();
        app.MapScalarApiReference(options => options.Servers = []);

        app.UseCors("frontend");
        // app.UseMiddleware<ApiKeyMiddleware>();

        app.MapHealthApi();
        app.MapPrintersApi();
        app.MapConfigApi();
        app.MapPrintApi();

        app.MapHealthChecks("/health2");

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not listen on http://127.0.0.1:{configStore.Current.Port}.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Farmora Tray",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        using (var tray = new TrayApplicationContext(app, configStore))
        {
            Application.Run(tray);
        }

        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
