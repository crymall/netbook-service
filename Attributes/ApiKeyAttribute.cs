using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace netbook_service.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAttribute : Attribute, IAsyncActionFilter
{
    private const string ApiKeyHeaderName = "x-api-key";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next
    )
    {
        var configuration =
            context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var validApiKey = configuration["MiddenApiKey"] ?? "dev_api_key";

        if (
            !context.HttpContext.Request.Headers.TryGetValue(
                ApiKeyHeaderName,
                out var extractedApiKey
            ) || !validApiKey.Equals(extractedApiKey)
        )
        {
            context.Result = new UnauthorizedObjectResult(
                new { error = "Access Denied: Invalid API Key" }
            );
            return;
        }

        await next();
    }
}
