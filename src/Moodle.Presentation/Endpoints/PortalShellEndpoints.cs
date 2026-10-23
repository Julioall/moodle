using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Moodle.Presentation.Endpoints;

/// <summary>
/// Compatibilidade de navegação entre as rotas legadas e o portal SPA canônico.
/// </summary>
internal static class PortalShellEndpoints
{
    public static void MapSinglePageApplicationShell(WebApplication app, bool appV2Enabled)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.Equals("", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!appV2Enabled)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.Redirect("/");
                return;
            }

            await next();
        });

        app.MapGet("/app.html", () => appV2Enabled ? Results.Redirect("/") : Results.NotFound());
        app.MapGet("/auth.html", (string? tab, string? error) =>
        {
            if (!appV2Enabled)
            {
                return Results.NotFound();
            }

            var query = new List<string>();
            if (string.Equals(tab, "register", StringComparison.OrdinalIgnoreCase))
            {
                query.Add("tab=register");
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                query.Add($"error={Uri.EscapeDataString(error)}");
            }

            return Results.Redirect($"/{(query.Count > 0 ? $"?{string.Join("&", query)}" : string.Empty)}");
        });

        if (appV2Enabled)
        {
            app.MapFallbackToFile("/{*path:nonfile}", "index.html");
        }
    }
}
