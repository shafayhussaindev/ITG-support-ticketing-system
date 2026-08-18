using System.Reflection;
using NetArchTest.Rules;
using SupportTicketing.Api.Controllers;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Domain.Common;
using SupportTicketing.Infrastructure.Persistence;

namespace SupportTicketing.ArchitectureTests;

/// <summary>
/// Executable versions of the architecture rules. A diagram in a document drifts;
/// a failing build does not.
/// </summary>
public class LayeringTests
{
    private static readonly Assembly Domain = typeof(Entity).Assembly;
    private static readonly Assembly Application = typeof(IAppDbContext).Assembly;
    private static readonly Assembly Infrastructure = typeof(AppDbContext).Assembly;
    private static readonly Assembly Api = typeof(AuthController).Assembly;

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        var result = Types.InAssembly(Domain)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Microsoft.Extensions",
                "SupportTicketing.Application",
                "SupportTicketing.Infrastructure",
                "SupportTicketing.Api",
                "SupportTicketing.Contracts")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_the_Api()
    {
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny("SupportTicketing.Infrastructure", "SupportTicketing.Api")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_does_not_reference_a_concrete_database_provider()
    {
        // Referencing the SQL Server provider from the Application layer would make
        // handlers untestable without a real database and would leak a deployment
        // choice into business logic.
        var result = Types.InAssembly(Application)
            .Should()
            .NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore.SqlServer", "Microsoft.Data.SqlClient")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_Api()
    {
        var result = Types.InAssembly(Infrastructure)
            .Should()
            .NotHaveDependencyOn("SupportTicketing.Api")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Controllers_never_expose_domain_entities()
    {
        // Returning an entity leaks the persistence model, invites over-posting, and
        // serialises navigation properties the caller is not authorized to see.
        var offenders = Types.InAssembly(Api)
            .That().AreClasses().And().Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase))
            .GetTypes()
            .SelectMany(controller => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(method => new { Method = method, Entity = FindEntityInReturnType(method.ReturnType) })
            .Where(x => x.Entity is not null)
            .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name} returns {x.Entity!.Name}")
            .ToList();

        offenders.ShouldBeEmpty(
            "controllers must return DTOs from SupportTicketing.Contracts, never domain entities:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Every_controller_action_is_protected_or_explicitly_anonymous()
    {
        // The host sets a fallback policy requiring authentication, so this asserts the
        // weaker but still useful property: nothing is accidentally left with a bare
        // [AllowAnonymous] on a class that also lacks any route protection reasoning.
        var anonymousActions = Types.InAssembly(Api)
            .That().AreClasses().And().Inherit(typeof(Microsoft.AspNetCore.Mvc.ControllerBase))
            .GetTypes()
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is not null)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        // Only the three unauthenticated entry points may be anonymous. Anything new
        // appearing here is a deliberate decision that should fail the build until reviewed.
        string[] permitted = ["AuthController.Login", "AuthController.Refresh"];

        anonymousActions.Except(permitted).ShouldBeEmpty(
            "a new anonymous endpoint was added. Confirm it must be public, then add it to the allowlist. Found: "
            + string.Join(", ", anonymousActions.Except(permitted)));
    }

    [Fact]
    public void Only_authentication_code_may_bypass_the_tenant_filter()
    {
        // IgnoreTenantFilter disables multi-tenant isolation. It is legitimate during
        // sign-in and refresh, where the caller's organization is not yet known, and
        // nowhere else without review.
        var callers = FindCallersOf(Application, nameof(IAppDbContext.IgnoreTenantFilter))
            .Select(OwningType)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        string[] permitted =
        [
            "LoginCommandHandler",
            "RefreshTokenCommandHandler"
        ];

        callers.Except(permitted).ShouldBeEmpty(
            "a new caller bypasses tenant isolation. Review it, then add it to the allowlist. Found: "
            + string.Join(", ", callers.Except(permitted)));
    }

    /// <summary>
    /// Resolves a type to the name a human wrote. An <c>async</c> method compiles into
    /// a nested state-machine class such as <c>&lt;HandleAsync&gt;d__7</c>, so scanning
    /// IL finds that rather than the handler; this walks back to the declaring type.
    /// </summary>
    private static string OwningType(Type type)
    {
        var current = type;

        while (current.Name.StartsWith('<') && current.DeclaringType is not null)
        {
            current = current.DeclaringType;
        }

        return current.Name;
    }

    /// <summary>Walks a return type, unwrapping Task and ActionResult, looking for a domain entity.</summary>
    private static Type? FindEntityInReturnType(Type type)
    {
        while (type.IsGenericType)
        {
            type = type.GetGenericArguments()[0];
        }

        return typeof(Entity).IsAssignableFrom(type) ? type : null;
    }

    /// <summary>
    /// Finds types whose method bodies reference a member by name. Uses the IL token
    /// stream rather than source text so a rename cannot silently defeat the rule.
    /// </summary>
    private static IEnumerable<Type> FindCallersOf(Assembly assembly, string methodName)
    {
        foreach (var type in assembly.GetTypes())
        {
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                MethodBody? body;
                try
                {
                    body = method.GetMethodBody();
                }
                catch (Exception)
                {
                    continue;
                }

                if (body is null)
                {
                    continue;
                }

                var il = body.GetILAsByteArray();
                if (il is null || il.Length == 0)
                {
                    continue;
                }

                if (ReferencesMethod(method.Module, il, methodName))
                {
                    yield return type;
                    break;
                }
            }
        }
    }

    private static bool ReferencesMethod(Module module, byte[] il, string methodName)
    {
        // 0x28 = call, 0x6F = callvirt. Both are followed by a 4-byte metadata token.
        for (var i = 0; i < il.Length - 4; i++)
        {
            if (il[i] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, i + 1);

            try
            {
                var resolved = module.ResolveMethod(token);
                if (resolved is not null && resolved.Name == methodName)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Tokens that fail to resolve are not calls we care about.
            }
        }

        return false;
    }

    private static string Describe(TestResult result) =>
        result.FailingTypeNames is null
            ? "no failing types reported"
            : "violating types: " + string.Join(", ", result.FailingTypeNames);
}
