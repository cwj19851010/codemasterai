using System.Text.Json;
using CodeMaster.Core.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeMaster.Infrastructure.Middleware;

/// <summary>
/// Converts expected business failures into a stable JSON HTTP response.
/// </summary>
public sealed class BusinessExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<BusinessExceptionMiddleware> _logger;

    public BusinessExceptionMiddleware(RequestDelegate next, ILogger<BusinessExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BusinessException exception)
        {
            if (context.Response.HasStarted)
                throw;

            _logger.LogInformation("Business request rejected with HTTP {StatusCode}: {Message}", exception.Code, exception.Message);
            context.Response.Clear();
            context.Response.StatusCode = exception.Code is >= 400 and <= 599 ? exception.Code : StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                code = context.Response.StatusCode,
                message = exception.Message,
                success = false
            }));
        }
    }
}

public static class BusinessExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseBusinessExceptionHandling(this IApplicationBuilder builder)
        => builder.UseMiddleware<BusinessExceptionMiddleware>();
}
