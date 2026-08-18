using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Auth;
using SupportTicketing.Contracts.Common;
using SupportTicketing.Domain.Common;

namespace SupportTicketing.Api.Middleware;

/// <summary>
/// Converts every unhandled exception into an RFC 7807 Problem Details response.
/// </summary>
/// <remarks>
/// Two rules govern what reaches the client. First, known exception types map to a
/// specific status and a stable machine-readable code. Second, anything unrecognised
/// becomes a generic 500 carrying only a correlation id — stack traces, SQL text and
/// EF messages are logged server-side and never serialised, because they disclose
/// schema and library versions to an attacker.
/// </remarks>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await WriteProblemAsync(context, ex, currentUser);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception exception, ICurrentUser currentUser)
    {
        if (context.Response.HasStarted)
        {
            logger.LogError(exception, "Exception thrown after the response had started; cannot write Problem Details.");
            return;
        }

        var correlationId = currentUser.CorrelationId;
        var (status, code, title, detail, extensions) = Map(exception, correlationId);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception,
                "Unhandled exception. Correlation {CorrelationId}, path {Path}",
                correlationId, context.Request.Path);
        }
        else
        {
            logger.LogInformation(
                "Request failed with {Status} ({Code}). Correlation {CorrelationId}, path {Path}",
                status, code, correlationId, context.Request.Path);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = code,
            Instance = context.Request.Path
        };

        problem.Extensions["correlationId"] = correlationId.ToString();
        problem.Extensions["code"] = code;

        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        // The raw exception is exposed only outside production, and even then under a
        // clearly named member so it is never mistaken for part of the contract.
        if (status >= StatusCodes.Status500InternalServerError && !environment.IsProduction())
        {
            problem.Extensions["developerDetail"] = exception.ToString();
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers[HttpContextCurrentUser.CorrelationHeader] = correlationId.ToString();

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }

    private static (int Status, string Code, string Title, string Detail, Dictionary<string, object?> Extensions)
        Map(Exception exception, Guid correlationId) => exception switch
    {
        ValidationException validation => (
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            "One or more fields are invalid.",
            "Correct the highlighted fields and try again.",
            new Dictionary<string, object?>
            {
                ["errors"] = validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())
            }),

        AuthenticationFailedException auth => (
            auth.Code switch
            {
                ErrorCodes.AccountLocked => StatusCodes.Status423Locked,
                ErrorCodes.AccountInactive => StatusCodes.Status403Forbidden,
                ErrorCodes.TwoFactorRequired => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status401Unauthorized
            },
            auth.Code,
            "Authentication failed.",
            auth.Message,
            []),

        ForbiddenException forbidden => (
            StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "Access denied.", forbidden.Message, []),

        // Deliberately 404 rather than 403: telling a caller that a record exists but
        // is not theirs confirms the identifier is real and enables enumeration.
        NotFoundException notFound => (
            StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found.", notFound.Message, []),

        ConflictException conflict => (
            StatusCodes.Status409Conflict, conflict.Code, "Conflict.", conflict.Message, []),

        InvalidStatusTransitionException transition => (
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.InvalidStatusTransition,
            "That status change is not allowed.",
            transition.Message,
            new Dictionary<string, object?> { ["from"] = transition.From, ["to"] = transition.To }),

        BusinessRuleException rule => (
            StatusCodes.Status422UnprocessableEntity, rule.Code, "Operation not allowed.", rule.Message, []),

        DomainException domain => (
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.BusinessRuleViolation, "Operation not allowed.", domain.Message, []),

        DbUpdateConcurrencyException => (
            StatusCodes.Status409Conflict,
            ErrorCodes.ConcurrencyConflict,
            "This record changed while you were editing it.",
            "Someone else updated this record. Reload it and reapply your changes.",
            []),

        // 499 is nginx's "client closed request". ASP.NET Core has no constant for it.
        OperationCanceledException => (
            499, "request_cancelled", "Request cancelled.", "The client closed the connection.", []),

        _ => (
            StatusCodes.Status500InternalServerError,
            ErrorCodes.Internal,
            "Something went wrong.",
            $"An unexpected error occurred. Quote correlation id {correlationId} when reporting this.",
            [])
    };
}
