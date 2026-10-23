using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;

namespace Moodle.Benchmarks;

public class AllowAllAuthorizationHandler : IAuthorizationHandler
{
    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        foreach (var requirement in context.PendingRequirements)
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
