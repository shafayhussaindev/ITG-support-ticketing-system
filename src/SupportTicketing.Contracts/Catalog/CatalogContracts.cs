namespace SupportTicketing.Contracts.Catalog;

public sealed record CategoryResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<SubcategoryResponse> Subcategories { get; init; }
}

public sealed record SubcategoryResponse
{
    public required Guid Id { get; init; }
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }

    /// <summary>Pre-selects impact in the ticket form. The requester can still change it.</summary>
    public string? DefaultImpact { get; init; }
}

public sealed record ApplicationResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public required bool IsBusinessCritical { get; init; }
    public required IReadOnlyList<ApplicationModuleResponse> Modules { get; init; }
}

public sealed record ApplicationModuleResponse
{
    public required Guid Id { get; init; }
    public required Guid ApplicationId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
}

public sealed record AssignableAgentResponse
{
    public required Guid Id { get; init; }
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public string? JobTitle { get; init; }
    public required bool IsAvailable { get; init; }
    public required IReadOnlyList<string> Teams { get; init; }

    /// <summary>Open tickets currently assigned, shown so a lead can spread load by eye.</summary>
    public required int OpenTicketCount { get; init; }
}
