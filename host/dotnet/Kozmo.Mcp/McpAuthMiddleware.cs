namespace Kozmo.Mcp;

/// <summary>
/// Auth seam for /mcp requests.
///
/// Dev-no-auth: set Mcp:DevNoAuth=true in appsettings or ASPNETCORE_ENVIRONMENT=Development.
/// Keyed auth: set Mcp:ApiKey — requests must include "Authorization: Bearer {key}".
/// If neither is configured (no ApiKey set), the middleware passes through (permissive default
/// suitable for localhost-only deployments).
/// </summary>
public sealed class McpAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration  _config;
    private readonly IHostEnvironment _env;

    public McpAuthMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env)
    {
        _next   = next;
        _config = config;
        _env    = env;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/mcp"))
        {
            await _next(ctx);
            return;
        }

        // Dev-no-auth: explicitly opted in, or running in Development environment
        if (_config.GetValue<bool>("Mcp:DevNoAuth") || _env.IsDevelopment())
        {
            await _next(ctx);
            return;
        }

        // Production: require Bearer token if ApiKey is configured
        var apiKey = _config["Mcp:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            // No key configured — permissive (operator must configure key before production)
            await _next(ctx);
            return;
        }

        var header = ctx.Request.Headers["Authorization"].FirstOrDefault();
        if (header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            && header[7..] == apiKey)
        {
            await _next(ctx);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("Unauthorized — provide a valid Bearer token.");
    }
}
