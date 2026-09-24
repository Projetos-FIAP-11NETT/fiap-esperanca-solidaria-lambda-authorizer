using FiapEsperancaSolidaria.Lambda.Authorizer.Domain;
using FiapEsperancaSolidaria.Lambda.Authorizer.Infrastructure;

namespace FiapEsperancaSolidaria.Lambda.Authorizer.Application.Queries;

public sealed class AuthorizeTokenQueryHandler
(
    IJwtTokenService jwtService,
    IAuthorizationRulesService rulesService
)
{
    public Task<AuthorizationResult> Handle(AuthorizeTokenQuery request)
    {
        var routeKey = $"{request.HttpMethod} {request.ResourcePath}";

        try
        {
            var token = ExtractToken(request.Token);

            // Sem token não é erro: pode ser rota AllowAnonymous (deslogado).
            // A checagem de papel/rota é sempre feita pela AuthorizationRulesService.
            Dictionary<string, object>? claims = string.IsNullOrEmpty(token)
                ? null
                : jwtService.DecodeToken(token);

            var userId = claims != null && claims.TryGetValue("sub", out object? value) ? value.ToString() : null;
            var roles = claims != null ? ExtractRoles(claims) : [];

            var isAuthorized = rulesService.IsAuthorized(routeKey, roles);

            return Task.FromResult(new AuthorizationResult
            {
                PrincipalId = userId ?? "anonymous",
                IsAuthorized = isAuthorized,
                Context = new Dictionary<string, object>
                {
                    { "userId", userId ?? "" },
                    { "roles", string.Join(",", roles) }
                },
                Roles = roles
            });
        }
        catch
        {
            return Task.FromResult(new AuthorizationResult
            {
                PrincipalId = "anonymous",
                IsAuthorized = false
            });
        }
    }

    private static string ExtractToken(string authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return string.Empty;

        const string bearer = "Bearer ";
        if (authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return authorizationHeader[bearer.Length..];

        return authorizationHeader;
    }

    private static List<string> ExtractRoles(Dictionary<string, object> claims)
    {
        if (claims.TryGetValue("roles", out var rolesObj))
            return NormalizeRolesList(rolesObj);

        if (claims.TryGetValue("role", out var roleObj))
            return NormalizeRolesList(roleObj);

        return [];
    }

    private static List<string> NormalizeRolesList(object? rolesObj)
    {
        if (rolesObj == null)
            return [];

        if (rolesObj is System.Collections.IEnumerable enumerable && rolesObj is not string)
        {
            return [.. enumerable.Cast<object>().Select(r => r?.ToString() ?? "").Where(r => !string.IsNullOrEmpty(r))];
        }

        var rolesStr = rolesObj.ToString() ?? "";
        return string.IsNullOrEmpty(rolesStr) ? [] : [rolesStr];
    }
}
