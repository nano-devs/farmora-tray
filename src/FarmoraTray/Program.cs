using FarmoraTray.Apis;
using FarmoraTray.Services;
using Scalar.AspNetCore;

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
    app.Logger.LogWarning(
        "Farmora Tray API key generated. Copy this key into the Farmora frontend printer settings for this PC:{NewLine}{ApiKey}{NewLine}Config file: {ConfigPath}",
        Environment.NewLine,
        configStore.Current.ApiKey,
        Environment.NewLine,
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

app.Run();
