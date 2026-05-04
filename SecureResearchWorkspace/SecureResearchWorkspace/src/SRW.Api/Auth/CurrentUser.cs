using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SRW.Api.Auth;

/// <summary>
/// Auth boundary. Today: development stub that reads X-User-Id header.
/// Tomorrow: drop in JwtBearer / OIDC pointed at Keycloak — change Program.cs only,
/// not the rest of the codebase.
/// </summary>
public interface ICurrentUser
{
    string UserId { get; }
    string DisplayName { get; }
    bool IsAuthenticated { get; }
}

public class CurrentUser : ICurrentUser
{
    public string UserId { get; set; } = "anonymous";
    public string DisplayName { get; set; } = "Anonymous";
    public bool IsAuthenticated => UserId != "anonymous";
}

public static class AuthExtensions
{
    public static IServiceCollection AddCurrentUserAccessor(this IServiceCollection services)
    {
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }

    /// <summary>
    /// Dev-mode middleware: trust X-User-Id and X-User-Name headers from a trusted gateway.
    /// In production replace with `app.UseAuthentication(); app.UseAuthorization();`
    /// after configuring `AddAuthentication().AddJwtBearer(...)` against Keycloak's JWKS.
    /// </summary>
    public static IApplicationBuilder UseCurrentUser(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var current = ctx.RequestServices.GetRequiredService<ICurrentUser>() as CurrentUser;
            if (current is not null)
            {
                if (ctx.Request.Headers.TryGetValue("X-User-Id", out var uid))
                    current.UserId = uid.ToString();
                if (ctx.Request.Headers.TryGetValue("X-User-Name", out var name))
                    current.DisplayName = name.ToString();
            }
            await next();
        });
    }
}
