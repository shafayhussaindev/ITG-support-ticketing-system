namespace SupportTicketing.Api.Security;

/// <summary>
/// Refuses to start a production instance that is misconfigured in a way that
/// would be dangerous rather than merely broken.
/// </summary>
/// <remarks>
/// <para>
/// Each of these has the same shape: the application runs perfectly well, serves
/// traffic, and is quietly insecure. A weak signing key still signs tokens. Demo
/// seeding still seeds. A wildcard origin still answers. Nothing fails, so nobody
/// looks — which is exactly why the check has to be a refusal to boot rather than a
/// warning in a log nobody reads.
/// </para>
/// <para>
/// Development is exempt throughout. The point is to stop a bad production
/// deployment, not to make local work tedious.
/// </para>
/// </remarks>
public static class StartupGuard
{
    private const int MinimumSigningKeyLength = 32;

    /// <summary>
    /// Keys that appear in documentation and examples. Any of them reaching production
    /// means somebody copied the sample and never replaced it — at which point anyone
    /// who has read the repository can mint a valid token for any user.
    /// </summary>
    private static readonly string[] KnownPlaceholders =
    [
        "change-me",
        "changeme",
        "your-signing-key",
        "development-only",
        "supersecret",
        "secret",
    ];

    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var problems = new List<string>();

        var signingKey = configuration["Jwt:SigningKey"];

        if (string.IsNullOrWhiteSpace(signingKey))
        {
            problems.Add(
                "Jwt:SigningKey is not set. Supply it through an environment variable or a "
                + "secret store — never appsettings.json.");
        }
        else
        {
            if (signingKey.Length < MinimumSigningKeyLength)
            {
                problems.Add(
                    $"Jwt:SigningKey is {signingKey.Length} characters; at least "
                    + $"{MinimumSigningKeyLength} are required. Generate one with "
                    + "'openssl rand -base64 48'.");
            }

            if (KnownPlaceholders.Any(placeholder =>
                    signingKey.Contains(placeholder, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(
                    "Jwt:SigningKey looks like a placeholder from the documentation. Anyone who "
                    + "has read the repository could forge a token for any user.");
            }
        }

        if (configuration.GetValue("Seed:EnableDemoAccounts", false))
        {
            problems.Add(
                "Seed:EnableDemoAccounts is true outside Development. The demo seeder is "
                + "additionally gated on the environment name, so it would not have run — but "
                + "the setting being present in a production configuration is a mistake worth "
                + "correcting before it meets an environment that does not have that gate.");
        }

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        if (origins.Length == 0)
        {
            problems.Add(
                "Cors:AllowedOrigins is empty, so the browser application cannot call the API. "
                + "List the exact origin the frontend is served from.");
        }

        if (origins.Any(origin => origin.Trim() == "*"))
        {
            problems.Add(
                "Cors:AllowedOrigins contains '*'. Credentials are sent with these requests, so "
                + "a wildcard is neither permitted by the browser nor safe.");
        }

        foreach (var origin in origins.Where(o => o.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            // Not fatal — an internal deployment may genuinely terminate TLS elsewhere —
            // but loopback aside, it is nearly always an oversight.
            if (!origin.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && !origin.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                problems.Add(
                    $"Cors:AllowedOrigins contains the plaintext origin '{origin}'. Tokens would "
                    + "travel over an unencrypted connection.");
            }
        }

        var connectionString = configuration.GetConnectionString("SupportTicketingDb");

        if (!string.IsNullOrWhiteSpace(connectionString)
            && connectionString.Contains("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Server=.", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                "The connection string sets TrustServerCertificate=True against a non-local "
                + "server, which disables validation of the database's certificate and permits "
                + "an interception between the application and its data.");
        }

        if (problems.Count == 0)
        {
            return;
        }

        var message = string.Join(
            Environment.NewLine,
            new[] { $"Refusing to start in '{environment.EnvironmentName}'. Configuration problems:" }
                .Concat(problems.Select((p, i) => $"  {i + 1}. {p}"))
                .Append(string.Empty)
                .Append("See docs/DEPLOYMENT.md for the full configuration reference."));

        throw new InvalidOperationException(message);
    }
}
