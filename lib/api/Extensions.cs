using System.ComponentModel.DataAnnotations;

namespace PodereBot.Lib.Api;

internal static class Middleware
{
    public static async Task RestrictToLocalNetwork(HttpContext context, Func<Task> next)
    {
        if (!context.Request.IsLocal())
        {
            context.Response.StatusCode = 403;
            return;
        }

        await next.Invoke();
    }
}

internal static class RouteBuilders
{
    public static (List<ValidationResult> Results, bool IsValid) DataAnnotationsValidate(this object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);

        var isValid = Validator.TryValidateObject(model, context, results, true);

        return (results, isValid);
    }

    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder, bool firstErrorOnly = true)
    {
        builder.AddEndpointFilter(
            async (invocationContext, next) =>
            {
                var argument = invocationContext.Arguments.OfType<T>().FirstOrDefault();
                if (argument == null)
                    return await next(invocationContext);

                var response = argument.DataAnnotationsValidate();
                if (!response.IsValid)
                {
                    string? errorMessage = firstErrorOnly
                        ? response.Results?.FirstOrDefault()?.ErrorMessage
                        : string.Join("|", response.Results.Select(x => x.ErrorMessage));

                    return Results.Problem(errorMessage, statusCode: 400);
                }

                return await next(invocationContext);
            }
        );

        return builder;
    }
}
