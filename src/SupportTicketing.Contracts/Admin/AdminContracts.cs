namespace SupportTicketing.Contracts.Admin;

// ---------------------------------------------------------------------- users

public sealed record UserListQueryParameters
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string? Search { get; init; }
    public Guid? RoleId { get; init; }
    public Guid? TeamId { get; init; }
    public Guid? DepartmentId { get; init; }
    public bool? ActiveOnly { get; init; }
}

public sealed record UserListItemResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string FullName { get; init; }
    public string? JobTitle { get; init; }
    public string? DepartmentName { get; init; }
    public string? OfficeName { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required IReadOnlyList<string> Teams { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>Set while the account is locked out after repeated failed sign-ins.</summary>
    public DateTime? LockoutEndUtc { get; init; }

    public DateTime? LastLoginAtUtc { get; init; }
    public required int OpenTickets { get; init; }
}

public sealed record UserDetailResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? JobTitle { get; init; }
    public string? PhoneNumber { get; init; }
    public required string TimeZoneId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? OfficeId { get; init; }
    public required bool IsActive { get; init; }
    public required bool MustChangePassword { get; init; }
    public required bool IsAvailableForAssignment { get; init; }
    public required int MaxConcurrentTickets { get; init; }
    public DateTime? LockoutEndUtc { get; init; }
    public DateTime? LastLoginAtUtc { get; init; }
    public required IReadOnlyList<Guid> RoleIds { get; init; }
    public required IReadOnlyList<TeamMembershipResponse> Teams { get; init; }

    /// <summary>The union of their roles, after any per-user override. Read-only here.</summary>
    public required IReadOnlyList<string> EffectivePermissions { get; init; }

    public required int ActiveSessions { get; init; }
}

public sealed record TeamMembershipResponse
{
    public required Guid TeamId { get; init; }
    public required string TeamName { get; init; }
    public required string RoleInTeam { get; init; }
    public required decimal CapacityWeight { get; init; }
}

public sealed record CreateUserRequest
{
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? JobTitle { get; init; }
    public string? PhoneNumber { get; init; }
    public string? TimeZoneId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? OfficeId { get; init; }
    public IReadOnlyList<Guid>? RoleIds { get; init; }
}

public sealed record UpdateUserRequest
{
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public string? JobTitle { get; init; }
    public string? PhoneNumber { get; init; }
    public string? TimeZoneId { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? OfficeId { get; init; }
    public bool IsAvailableForAssignment { get; init; } = true;
    public int MaxConcurrentTickets { get; init; }
}

public sealed record SetUserRolesRequest
{
    public required IReadOnlyList<Guid> RoleIds { get; init; }
    public string? Reason { get; init; }
}

public sealed record SetUserActiveRequest
{
    public required bool IsActive { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// The one-time password produced when an administrator resets an account.
/// </summary>
/// <remarks>
/// Returned once and never stored in readable form. The administrator passes it to
/// the user out of band, and the account is flagged to require a change at next
/// sign-in, so the administrator's knowledge of it is short-lived by construction.
/// </remarks>
public sealed record TemporaryPasswordResponse
{
    public required string TemporaryPassword { get; init; }
    public required string Notice { get; init; }
}

// ---------------------------------------------------------------------- roles

public sealed record RoleResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string DefaultScope { get; init; }
    public required int Rank { get; init; }

    /// <summary>System roles may have their permissions edited but cannot be renamed or removed.</summary>
    public required bool IsSystemRole { get; init; }

    public required int UserCount { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
}

public sealed record PermissionResponse
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
}

public sealed record UpdateRoleRequest
{
    public string? Description { get; init; }
    public required string DefaultScope { get; init; }
    public required int Rank { get; init; }
}

public sealed record SetRolePermissionsRequest
{
    public required IReadOnlyList<string> PermissionKeys { get; init; }
    public string? Reason { get; init; }
}

public sealed record CreateRoleRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string DefaultScope { get; init; }
    public int Rank { get; init; }
    public IReadOnlyList<string>? PermissionKeys { get; init; }
}

/// <summary>How much open work one member of staff is holding, right now.</summary>
public sealed record StaffWorkloadRow
{
    public required Guid UserId { get; init; }
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public string? JobTitle { get; init; }
    public required IReadOnlyList<string> Teams { get; init; }

    /// <summary>False when this person has taken themselves out of the rotation.</summary>
    public required bool IsAvailableForAssignment { get; init; }

    /// <summary>Zero means no ceiling has been set.</summary>
    public required int MaxConcurrentTickets { get; init; }

    public required int OpenTickets { get; init; }
    public required int InProgress { get; init; }

    /// <summary>Open, but waiting on the requester or a third party rather than on them.</summary>
    public required int Waiting { get; init; }

    public required int Critical { get; init; }
    public required int High { get; init; }
    public required int SlaBreached { get; init; }

    /// <summary>Age of their longest-standing open ticket, in days.</summary>
    public double? OldestOpenDays { get; init; }

    public required bool IsOverCapacity { get; init; }
}

// ---------------------------------------------------------------------- teams

public sealed record TeamResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public Guid? TeamLeadId { get; init; }
    public string? TeamLeadName { get; init; }
    public Guid? EscalationTeamId { get; init; }
    public string? EscalationTeamName { get; init; }
    public required int AcceptanceTimeoutMinutes { get; init; }
    public required bool IsActive { get; init; }
    public required IReadOnlyList<TeamMemberResponse> Members { get; init; }
    public required int OpenTickets { get; init; }
}

public sealed record TeamMemberResponse
{
    public required Guid UserId { get; init; }
    public required string FullName { get; init; }
    public required string Email { get; init; }
    public required string RoleInTeam { get; init; }
    public required decimal CapacityWeight { get; init; }
    public required bool IsActive { get; init; }
    public required int OpenTickets { get; init; }
}

public sealed record SaveTeamRequest
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? TeamLeadId { get; init; }
    public Guid? EscalationTeamId { get; init; }
    public int AcceptanceTimeoutMinutes { get; init; } = 30;
    public bool IsActive { get; init; } = true;
}

public sealed record SaveTeamMemberRequest
{
    public required Guid UserId { get; init; }
    public string? RoleInTeam { get; init; }

    /// <summary>Relative share of routed work. 0.5 means half a full workload.</summary>
    public decimal CapacityWeight { get; init; } = 1.0m;
}

// -------------------------------------------------------------------- catalog

public sealed record AdminCategoryResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public string? DefaultTeamName { get; init; }
    public Guid? SlaPolicyId { get; init; }
    public string? SlaPolicyName { get; init; }
    public required int DisplayOrder { get; init; }
    public required bool IsActive { get; init; }
    public required bool IsInternalOnly { get; init; }
    public required int TicketCount { get; init; }
    public required IReadOnlyList<AdminSubcategoryResponse> Subcategories { get; init; }
}

public sealed record AdminSubcategoryResponse
{
    public required Guid Id { get; init; }
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public string? DefaultImpact { get; init; }
    public required int DisplayOrder { get; init; }
    public required bool IsActive { get; init; }
}

public sealed record SaveCategoryRequest
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public Guid? SlaPolicyId { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsInternalOnly { get; init; }
}

public sealed record SaveSubcategoryRequest
{
    public required Guid CategoryId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? DefaultTeamId { get; init; }
    public string? DefaultImpact { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record AdminApplicationResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Vendor { get; init; }
    public string? Version { get; init; }
    public Guid? OwningTeamId { get; init; }
    public string? OwningTeamName { get; init; }
    public required bool IsBusinessCritical { get; init; }
    public required bool IsActive { get; init; }
    public required IReadOnlyList<AdminModuleResponse> Modules { get; init; }
}

public sealed record AdminModuleResponse
{
    public required Guid Id { get; init; }
    public required Guid ApplicationId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public required int DisplayOrder { get; init; }
    public required bool IsActive { get; init; }
}

public sealed record SaveApplicationRequest
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public string? Vendor { get; init; }
    public string? Version { get; init; }
    public Guid? OwningTeamId { get; init; }
    public bool IsBusinessCritical { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record SaveModuleRequest
{
    public required Guid ApplicationId { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public Guid? OwningTeamId { get; init; }
    public int DisplayOrder { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>One cell of the impact-by-urgency grid.</summary>
public sealed record PriorityMatrixCell
{
    public required string Impact { get; init; }
    public required string Urgency { get; init; }
    public required string Priority { get; init; }
}

public sealed record SavePriorityMatrixRequest
{
    public required IReadOnlyList<PriorityMatrixCell> Cells { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// One cell of a policy's grid, with where its value came from.
/// </summary>
/// <remarks>
/// The source is the point of this record. A grid that shows sixteen priorities and
/// nothing else cannot tell an administrator which of them this policy actually decided
/// and which it is merely inheriting — and that is the only question worth asking when
/// looking at an override.
/// </remarks>
public sealed record PolicyPriorityMatrixCell
{
    public required string Impact { get; init; }
    public required string Urgency { get; init; }
    public required string Priority { get; init; }

    /// <summary>One of <c>Policy</c>, <c>Organization</c> or <c>BuiltIn</c>.</summary>
    public required string Source { get; init; }
}

public sealed record PolicyPriorityMatrixResponse
{
    public required Guid PolicyId { get; init; }
    public required string PolicyName { get; init; }

    /// <summary>False when the policy defers entirely to the organization's matrix.</summary>
    public required bool HasOverrides { get; init; }

    public required int OverriddenCells { get; init; }
    public required IReadOnlyList<PolicyPriorityMatrixCell> Cells { get; init; }
}

// ------------------------------------------------------------------------ SLA

public sealed record SlaPolicyResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Guid? BusinessCalendarId { get; init; }
    public string? BusinessCalendarName { get; init; }
    public Guid? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public string? TicketType { get; init; }
    public required bool IsDefault { get; init; }
    public required bool IsActive { get; init; }
    public required bool PauseWhenWaitingOnOthers { get; init; }
    public required IReadOnlyList<SlaTargetResponse> Targets { get; init; }

    /// <summary>Clocks currently running against this policy, so an edit's blast radius is visible.</summary>
    public required int ActiveClocks { get; init; }
}

public sealed record SlaTargetResponse
{
    public required string Priority { get; init; }
    public required int ResponseMinutes { get; init; }
    public required int ResolutionMinutes { get; init; }
    public required int WarningThresholdPercent { get; init; }
}

public sealed record SaveSlaPolicyRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Guid? BusinessCalendarId { get; init; }
    public Guid? CategoryId { get; init; }
    public string? TicketType { get; init; }
    public bool IsDefault { get; init; }
    public bool IsActive { get; init; } = true;
    public bool PauseWhenWaitingOnOthers { get; init; } = true;
    public required IReadOnlyList<SlaTargetResponse> Targets { get; init; }
}

public sealed record BusinessCalendarResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Code { get; init; }
    public required string TimeZoneId { get; init; }
    public required bool IsDefault { get; init; }
    public required bool IsActive { get; init; }
    public required IReadOnlyList<BusinessHourResponse> Hours { get; init; }
    public required IReadOnlyList<HolidayResponse> Holidays { get; init; }
    public required int PoliciesUsing { get; init; }
}

public sealed record BusinessHourResponse
{
    public required string DayOfWeek { get; init; }
    public required int StartMinute { get; init; }
    public required int EndMinute { get; init; }
}

public sealed record HolidayResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DateTime DateUtc { get; init; }
    public required bool IsRecurring { get; init; }
}

public sealed record SaveBusinessCalendarRequest
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Description { get; init; }
    public required string TimeZoneId { get; init; }
    public bool IsDefault { get; init; }
    public bool IsActive { get; init; } = true;
    public required IReadOnlyList<BusinessHourResponse> Hours { get; init; }
}

public sealed record SaveHolidayRequest
{
    public required string Name { get; init; }
    public required DateTime DateUtc { get; init; }
    public bool IsRecurring { get; init; }
}

// -------------------------------------------------------------- system settings

public sealed record SystemSettingResponse
{
    public required Guid Id { get; init; }
    public required string Key { get; init; }

    /// <summary>Masked when the setting is marked sensitive. The real value never leaves the server.</summary>
    public required string Value { get; init; }

    public required string ValueType { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public required bool IsSensitive { get; init; }

    /// <summary>Editable only by Super Admin; the API enforces this, not the UI.</summary>
    public required bool IsSystemManaged { get; init; }

    /// <summary>False for a global default that this organization has not overridden.</summary>
    public required bool IsOrganizationOverride { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }
}

public sealed record SaveSystemSettingRequest
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? ValueType { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public bool IsSensitive { get; init; }
}

// ------------------------------------------------------------------ reference

/// <summary>
/// The lists every admin form needs to populate a dropdown, fetched once.
/// </summary>
/// <remarks>
/// One request rather than six. These lists are small, change rarely and are needed
/// together on every form, so splitting them would cost round trips and buy nothing.
/// </remarks>
public sealed record AdminReferenceData
{
    public required IReadOnlyList<LookupItem> Departments { get; init; }
    public required IReadOnlyList<LookupItem> Offices { get; init; }
    public required IReadOnlyList<LookupItem> Teams { get; init; }
    public required IReadOnlyList<LookupItem> Roles { get; init; }
    public required IReadOnlyList<LookupItem> Categories { get; init; }
    public required IReadOnlyList<LookupItem> SlaPolicies { get; init; }
    public required IReadOnlyList<LookupItem> BusinessCalendars { get; init; }
    public required IReadOnlyList<LookupItem> Users { get; init; }
}

public sealed record LookupItem(Guid Id, string Name, bool IsActive = true);
