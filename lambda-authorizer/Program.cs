using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using FiapEsperancaSolidaria.Lambda.Authorizer.Application.Queries;
using FiapEsperancaSolidaria.Lambda.Authorizer.Infrastructure;
using System.Text.Json;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace FiapEsperancaSolidaria.Lambda.Authorizer;

public class AuthorizerFunction
{
    private static readonly AuthorizeTokenQueryHandler Handler = new(new JwtTokenService(), new AuthorizationRulesService());
    private static readonly IamPolicyBuilder PolicyBuilder = new();

    public static Dictionary<string, object> FunctionHandler(Dictionary<string, object> @event, ILambdaContext context)
    {
        context.Logger.LogLine("FunctionHandler called");
        var methodArn = GetEventString(@event, "methodArn");
        var authorizationToken =
            GetEventString(@event, "authorizationToken");

        if (string.IsNullOrEmpty(authorizationToken))
        {
            authorizationToken = ExtractAuthorizationHeader(@event);
        }

        // methodArn: arn:aws:execute-api:{region}:{account}:{apiId}/{stage}/{httpMethod}/{resourcePath}
        var arnParts = methodArn.Split(':');
        var resourceParts = arnParts.Length > 5 ? arnParts[5].Split('/') : [];
        var apiId = resourceParts.Length > 0 ? resourceParts[0] : "";
        var stage = resourceParts.Length > 1 ? resourceParts[1] : "";
        var httpMethod = resourceParts.Length > 2 ? resourceParts[2] : "";
        var resourcePath = resourceParts.Length > 3 ? string.Join("/", resourceParts[3..]) : "";

        try
        {
            var query = new AuthorizeTokenQuery
            {
                Token = authorizationToken,
                HttpMethod = httpMethod,
                ResourcePath = resourcePath,
                ApiId = apiId,
                Stage = stage
            };

            var result = Handler.Handle(query).GetAwaiter().GetResult();

            if (!result.IsAuthorized)
            {
                context.Logger.LogLine($"Authorization refused: {httpMethod} {resourcePath} (principal={result.PrincipalId}).");
            }

            return PolicyBuilder.BuildPolicy(result.PrincipalId, result.IsAuthorized, apiId, stage, methodArn, result.Context);
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"Authorization error: {ex.Message}");
            return PolicyBuilder.BuildPolicy("error", false, apiId, stage, methodArn);
        }
    }

    private static string ExtractAuthorizationHeader(Dictionary<string, object> evt)
    {
        if (!evt.TryGetValue("headers", out var headersObj))
            return "";

        if (headersObj is JsonElement headersElement &&
            headersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headersElement.EnumerateObject())
            {
                if (prop.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals("authorizationToken", StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.GetString() ?? "";
                }
            }
        }

        return "";
    }

    private static string GetEventString(Dictionary<string, object> evt, string key)
    {
        if (!evt.TryGetValue(key, out var value) || value == null)
            return "";

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
            JsonElement je => je.ToString(),
            _ => value.ToString() ?? ""
        };
    }

}
