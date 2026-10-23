using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Moodle.Benchmarks;

public class TestAuthStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var claims = new[] { new Claim(ClaimTypes.Name, "Test user") };
                var identity1 = new ClaimsIdentity(claims, "Bearer");
                var identity2 = new ClaimsIdentity(claims, "OpenIddict.Validation.AspNetCore");
                var identity3 = new ClaimsIdentity(claims, "Test");
                context.User = new ClaimsPrincipal(new[] { identity1, identity2, identity3 });
                await nextMiddleware();
            });
            next(app);
        };
    }
}
