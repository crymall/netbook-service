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
        var services = context.HttpContext.RequestServices;
        var configuration = services.GetRequiredService<IConfiguration>();
        var validApiKey = configuration["MiddenApiKey"];

        // Fail closed: outside Development, a missing key is a deployment
        // error, never an open door. (Program.cs also refuses to start
        // without it; this is defense in depth.)
        if (string.IsNullOrEmpty(validApiKey))
        {
            var environment = services.GetRequiredService<IHostEnvironment>();
            if (!environment.IsDevelopment())
            {
                context.Result = new ObjectResult(new { error = "Service misconfigured" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError,
                };
                return;
            }
            validApiKey = "dev_api_key";
        }

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
