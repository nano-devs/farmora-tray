namespace FarmoraTray.Services;

public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Farmora-Tray-Key";

    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ConfigStore configStore)
    {
        var path = context.Request.Path;

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !configStore.IsOriginAllowed(origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Origin not allowed",
                detail = "Set allowedOrigin via PUT /config to match the Farmora frontend origin."
            });
            return;
        }

        if (IsHealthPath(path))
        {
            await _next(context);
            return;
        }

        var provided = context.Request.Headers[HeaderName].ToString();
        if (!configStore.IsApiKeyValid(provided))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                title = "Unauthorized",
                detail = $"Missing or invalid {HeaderName} header."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsHealthPath(PathString path) =>
        path.Equals("/health", StringComparison.OrdinalIgnoreCase);
}
