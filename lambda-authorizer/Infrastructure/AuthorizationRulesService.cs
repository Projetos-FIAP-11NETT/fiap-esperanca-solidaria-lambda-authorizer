using FiapEsperancaSolidaria.Lambda.Authorizer.Domain;

namespace FiapEsperancaSolidaria.Lambda.Authorizer.Infrastructure;

public interface IAuthorizationRulesService
{
    bool IsAuthorized(string routeKey, List<string> userRoles);
}

public class AuthorizationRulesService : IAuthorizationRulesService
{
    // Static cached rules - initialized once per Lambda container lifetime
    private static readonly Lazy<List<AuthorizationRule>> CachedRules =
        new(() => InitializeRulesStatic(), LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAuthorized(string routeKey, List<string> userRoles)
    {
        var parts = routeKey.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var method = parts.Length > 0 ? parts[0].ToUpperInvariant() : "";
        var path = parts.Length > 1 ? parts[1] : "/";

        var rule = CachedRules.Value.FirstOrDefault(r =>
            (r.Method == "ANY" || r.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) &&
            PathMatches(r.Path, path));

        if (rule == null)
        {
            // Default: deny if no rule matches
            return false;
        }

        if (rule.AllowAnonymous)
            return true;

        if (userRoles.Count == 0)
            return false;

        return rule.AllowedRoles.Any(allowedRole =>
            userRoles.Any(userRole => userRole.Equals(allowedRole, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool PathMatches(string rulePath, string requestPath)
    {
        if (rulePath == "*")
            return true;

        if (rulePath.EndsWith('*'))
        {
            var prefix = rulePath.TrimEnd('*');
            return requestPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(rulePath, requestPath, StringComparison.OrdinalIgnoreCase);
    }

    // Únicos papéis emitidos pelo Firebase (claim "role"): "Doador" (usuário logado) e
    // "GestorONG" (admin). Ausência de token = deslogado, coberto por AllowAnonymous.
    //
    // Espelha exatamente os recursos hoje criados em
    // fiap-esperanca-solidaria-infra/terraform/{k8s,docker-compose}/main.tf.
    // GET /api/v1/campanhas(/{id}), GET /health e os dois POST de users já são
    // authorization=NONE no API Gateway (nunca chegam a invocar esta Lambda) — as
    // regras abaixo para eles existem só como defesa em profundidade.
    //
    // GET api/v1/campanhas/gestao e POST api/v1/campanhas/* (images e {id}/cancel) antecipam
    // rotas CUSTOM ainda não criadas no main.tf (CampaignController.List/UploadImage/Cancel).
    // A primeira regra que casa vence: "gestao" precisa ficar antes do GET anônimo campanhas/*.
    private static List<AuthorizationRule> InitializeRulesStatic()
    {
        return
        [
            new() { Method = "GET", Path = "api/v1/campanhas/gestao", AllowedRoles = ["GestorONG"] },

            new() { Method = "GET", Path = "api/v1/campanhas", AllowAnonymous = true },
            new() { Method = "GET", Path = "api/v1/campanhas/*", AllowAnonymous = true },
            new() { Method = "GET", Path = "health", AllowAnonymous = true },

            new() { Method = "POST", Path = "api/v1/campanhas", AllowedRoles = ["GestorONG"] },
            new() { Method = "POST", Path = "api/v1/campanhas/*", AllowedRoles = ["GestorONG"] },
            new() { Method = "PUT", Path = "api/v1/campanhas/*", AllowedRoles = ["GestorONG"] },

            new() { Method = "POST", Path = "users/api/v1/User/Doador", AllowAnonymous = true },
            new() { Method = "POST", Path = "users/api/v1/User/images", AllowAnonymous = true },
            new() { Method = "POST", Path = "users/api/v1/User/Login", AllowAnonymous = true },
            new() { Method = "GET", Path = "users/api/v1/User/Session/*", AllowedRoles = ["Doador", "GestorONG"] },
            new() { Method = "DELETE", Path = "users/api/v1/User/Session/*", AllowedRoles = ["Doador", "GestorONG"] },
            new() { Method = "PUT", Path = "users/api/v1/User/MakeGestorONG", AllowedRoles = ["GestorONG"] },

            new() { Method = "POST", Path = "api/v1/doacoes", AllowedRoles = ["Doador"] },
            new() { Method = "GET", Path = "api/v1/doacoes/me", AllowedRoles = ["Doador"] },
            new() { Method = "GET", Path = "api/v1/doacoes/*", AllowedRoles = ["Doador", "GestorONG"] },
        ];
    }
}
