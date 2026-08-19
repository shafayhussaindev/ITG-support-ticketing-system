using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using SupportTicketing.Api.Security;
using SupportTicketing.Application.Abstractions;
using SupportTicketing.Application.Features.Admin;
using SupportTicketing.Contracts.Admin;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Api.Controllers;

// ---------------------------------------------------------------------- users

[ApiController]
[Route("api/v1/admin/users")]
[Produces("application/json")]
[HasPermission(Permissions.Administration.ManageUsers)]
public sealed class AdminUsersController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Lists accounts with their roles, teams and current load.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "List users", Description =
        "Open-ticket counts are included so the person deciding who to add to a team "
        + "can see who is already carrying the work.")]
    [ProducesResponseType<PagedResult<UserListItemResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserListItemResponse>>> List(
        [FromQuery] UserListQueryParameters parameters, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListUsersQuery(parameters), cancellationToken));

    /// <summary>Returns one account, including its effective permissions.</summary>
    [HttpGet("{id:guid}")]
    [SwaggerOperation(Summary = "Get a user", Description =
        "Effective permissions are the union of the user's roles with any per-user "
        + "override applied — a deny beats every role grant, which is the case that is "
        + "hardest to see from the role list alone.")]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDetailResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetUserQuery(id), cancellationToken));

    /// <summary>Creates an account and returns its one-time password.</summary>
    [HttpPost]
    [SwaggerOperation(Summary = "Create a user", Description =
        "The password is generated, not chosen: an administrator-picked password tends "
        + "to be the same string for every new starter, and leaves the administrator "
        + "holding a credential they had no need to know. It is returned once and the "
        + "account must change it at first sign-in.")]
    [ProducesResponseType<TemporaryPasswordResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TemporaryPasswordResponse>> Create(
        [FromBody] CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(new CreateUserCommand(request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    /// <summary>Edits profile and assignment settings.</summary>
    [HttpPut("{id:guid}")]
    [SwaggerOperation(Summary = "Update a user")]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDetailResponse>> Update(
        Guid id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new UpdateUserCommand(id, request), cancellationToken));

    /// <summary>Replaces the user's roles.</summary>
    [HttpPut("{id:guid}/roles")]
    [HasPermission(Permissions.Administration.ManageRoles)]
    [SwaggerOperation(Summary = "Set a user's roles", Description =
        "Set semantics, not add-and-remove: the caller is describing the end state. "
        + "The change does not reach an existing session until its access token "
        + "expires, so this is not a way to contain a hostile account — deactivating "
        + "it, which revokes their sessions, is.")]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDetailResponse>> SetRoles(
        Guid id, [FromBody] SetUserRolesRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SetUserRolesCommand(id, request), cancellationToken));

    /// <summary>Deactivates or restores an account.</summary>
    [HttpPost("{id:guid}/active")]
    [SwaggerOperation(Summary = "Deactivate or restore a user", Description =
        "Deactivation revokes every refresh token the user holds. Accounts are never "
        + "deleted: their name is attached to tickets, comments and audit rows that "
        + "must stay attributable.")]
    [ProducesResponseType<UserDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDetailResponse>> SetActive(
        Guid id, [FromBody] SetUserActiveRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SetUserActiveCommand(id, request), cancellationToken));

    /// <summary>Issues a new one-time password and signs the account out everywhere.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [SwaggerOperation(Summary = "Reset a password", Description =
        "Every session for the account is revoked, because a reset that left them "
        + "running would not actually remove access from whoever prompted it.")]
    [ProducesResponseType<TemporaryPasswordResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TemporaryPasswordResponse>> ResetPassword(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new ResetUserPasswordCommand(id), cancellationToken));

    /// <summary>Permanently removes an account that owns no work.</summary>
    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Administration.ManageOrganizations)]
    [SwaggerOperation(Summary = "Delete a user", Description =
        "Super Admin only. An account that owns nothing is removed outright. One that "
        + "raised or was assigned tickets, wrote comments or authored articles is "
        + "anonymised instead: the name becomes \"Deleted user\", the email an "
        + "unroutable placeholder and the password a random value, while the row stays "
        + "so those tickets remain attributable. Either way the account disappears from "
        + "every list of people and can never sign in again. Audit rows are untouched — "
        + "they hold the name and email as a snapshot rather than a foreign key.")]
    [ProducesResponseType<DeleteUserResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<DeleteUserResult>> Delete(
        Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new DeleteUserCommand(id), cancellationToken));

    /// <summary>Signs the account out of every device.</summary>
    [HttpPost("{id:guid}/revoke-sessions")]
    [SwaggerOperation(Summary = "Revoke sessions")]
    [ProducesResponseType<int>(StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> RevokeSessions(Guid id, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RevokeUserSessionsCommand(id), cancellationToken));
}

// ---------------------------------------------------------------------- roles

[ApiController]
[Route("api/v1/admin/roles")]
[Produces("application/json")]
[HasPermission(Permissions.Administration.ManageRoles)]
public sealed class AdminRolesController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Lists roles with their permissions and how many people hold each.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "List roles")]
    [ProducesResponseType<IReadOnlyList<RoleResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> List(CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListRolesQuery(), cancellationToken));

    /// <summary>The permission catalogue, grouped by area.</summary>
    [HttpGet("/api/v1/admin/permissions")]
    [SwaggerOperation(Summary = "List permissions", Description =
        "Read from the table rather than from the constant list, so a key that was "
        + "never seeded — and therefore cannot be granted — is not offered as an "
        + "option that silently does nothing.")]
    [ProducesResponseType<IReadOnlyList<PermissionResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermissionResponse>>> ListPermissions(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListPermissionsQuery(), cancellationToken));

    /// <summary>Creates a role.</summary>
    [HttpPost]
    [SwaggerOperation(Summary = "Create a role")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleResponse>> Create(
        [FromBody] CreateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await dispatcher.SendAsync(new CreateRoleCommand(request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, role);
    }

    /// <summary>Edits a role's description, data scope and rank.</summary>
    [HttpPut("{id:guid}")]
    [SwaggerOperation(Summary = "Update a role", Description =
        "The data scope decides which rows the role's permissions may touch — a "
        + "separate question from which verbs it may perform.")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RoleResponse>> Update(
        Guid id, [FromBody] UpdateRoleRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new UpdateRoleCommand(id, request), cancellationToken));

    /// <summary>Replaces the permissions a role carries.</summary>
    [HttpPut("{id:guid}/permissions")]
    [SwaggerOperation(Summary = "Set role permissions", Description =
        "Allowed on system roles: the seeded roles are a starting point, not a "
        + "contract. Refused when it would leave nobody able to manage roles.")]
    [ProducesResponseType<RoleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleResponse>> SetPermissions(
        Guid id, [FromBody] SetRolePermissionsRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SetRolePermissionsCommand(id, request), cancellationToken));

    /// <summary>Deletes a role that nobody holds.</summary>
    [HttpDelete("{id:guid}")]
    [SwaggerOperation(Summary = "Delete a role", Description =
        "Refused for system roles and for any role with members — deleting one out "
        + "from under its holders would strip their permissions silently.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteRoleCommand(id), cancellationToken);
        return NoContent();
    }
}

// ---------------------------------------------------------------------- teams

[ApiController]
[Route("api/v1/admin/teams")]
[Produces("application/json")]
[HasPermission(Permissions.Administration.ManageTeams)]
public sealed class AdminTeamsController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Lists teams with their members and open workload.</summary>
    [HttpGet]
    [SwaggerOperation(Summary = "List teams")]
    [ProducesResponseType<IReadOnlyList<TeamResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TeamResponse>>> List(CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListTeamsQuery(), cancellationToken));

    /// <summary>Creates a team.</summary>
    [HttpPost]
    [SwaggerOperation(Summary = "Create a team")]
    [ProducesResponseType<TeamResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamResponse>> Create(
        [FromBody] SaveTeamRequest request, CancellationToken cancellationToken)
    {
        var team = await dispatcher.SendAsync(new SaveTeamCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, team);
    }

    /// <summary>Edits a team.</summary>
    [HttpPut("{id:guid}")]
    [SwaggerOperation(Summary = "Update a team", Description =
        "A team cannot escalate to itself; the configuration is refused rather than "
        + "producing a loop the escalation engine has to break at runtime.")]
    [ProducesResponseType<TeamResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TeamResponse>> Update(
        Guid id, [FromBody] SaveTeamRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveTeamCommand(id, request), cancellationToken));

    /// <summary>Adds a member, or updates their role and capacity.</summary>
    [HttpPut("{id:guid}/members")]
    [SwaggerOperation(Summary = "Add or update a team member", Description =
        "Capacity weight is the member's relative share of routed work. Zero keeps "
        + "them on the team but out of the rotation.")]
    [ProducesResponseType<TeamResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TeamResponse>> SaveMember(
        Guid id, [FromBody] SaveTeamMemberRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveTeamMemberCommand(id, request), cancellationToken));

    /// <summary>Takes someone off a team.</summary>
    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [SwaggerOperation(Summary = "Remove a team member", Description =
        "Refused while they still own open tickets for the team, which would otherwise "
        + "disappear from that team's queue.")]
    [ProducesResponseType<TeamResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamResponse>> RemoveMember(
        Guid id, Guid userId, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RemoveTeamMemberCommand(id, userId), cancellationToken));
}

// -------------------------------------------------------------------- catalog

[ApiController]
[Route("api/v1/admin/catalog")]
[Produces("application/json")]
[HasPermission(Permissions.Administration.ManageCatalog)]
public sealed class AdminCatalogController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Categories and their subcategories, with ticket counts.</summary>
    [HttpGet("categories")]
    [SwaggerOperation(Summary = "List categories")]
    [ProducesResponseType<IReadOnlyList<AdminCategoryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminCategoryResponse>>> Categories(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListAdminCategoriesQuery(), cancellationToken));

    /// <summary>Creates a category.</summary>
    [HttpPost("categories")]
    [SwaggerOperation(Summary = "Create a category")]
    [ProducesResponseType<AdminCategoryResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminCategoryResponse>> CreateCategory(
        [FromBody] SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await dispatcher.SendAsync(new SaveCategoryCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, category);
    }

    /// <summary>Edits a category.</summary>
    [HttpPut("categories/{id:guid}")]
    [SwaggerOperation(Summary = "Update a category")]
    [ProducesResponseType<AdminCategoryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminCategoryResponse>> UpdateCategory(
        Guid id, [FromBody] SaveCategoryRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveCategoryCommand(id, request), cancellationToken));

    /// <summary>Archives a category that no ticket uses.</summary>
    [HttpDelete("categories/{id:guid}")]
    [SwaggerOperation(Summary = "Delete a category", Description =
        "Refused once tickets are filed under it. Deactivate instead: it leaves the "
        + "raise-a-ticket form while existing tickets keep saying what they always said.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteCategoryCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>Creates a subcategory.</summary>
    [HttpPost("subcategories")]
    [SwaggerOperation(Summary = "Create a subcategory")]
    [ProducesResponseType<AdminCategoryResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminCategoryResponse>> CreateSubcategory(
        [FromBody] SaveSubcategoryRequest request, CancellationToken cancellationToken)
    {
        var parent = await dispatcher.SendAsync(new SaveSubcategoryCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, parent);
    }

    /// <summary>Edits a subcategory.</summary>
    [HttpPut("subcategories/{id:guid}")]
    [SwaggerOperation(Summary = "Update a subcategory")]
    [ProducesResponseType<AdminCategoryResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminCategoryResponse>> UpdateSubcategory(
        Guid id, [FromBody] SaveSubcategoryRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveSubcategoryCommand(id, request), cancellationToken));

    /// <summary>Applications and their modules.</summary>
    [HttpGet("applications")]
    [SwaggerOperation(Summary = "List applications")]
    [ProducesResponseType<IReadOnlyList<AdminApplicationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminApplicationResponse>>> Applications(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListAdminApplicationsQuery(), cancellationToken));

    /// <summary>Creates an application.</summary>
    [HttpPost("applications")]
    [SwaggerOperation(Summary = "Create an application")]
    [ProducesResponseType<AdminApplicationResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminApplicationResponse>> CreateApplication(
        [FromBody] SaveApplicationRequest request, CancellationToken cancellationToken)
    {
        var app = await dispatcher.SendAsync(new SaveApplicationCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, app);
    }

    /// <summary>Edits an application.</summary>
    [HttpPut("applications/{id:guid}")]
    [SwaggerOperation(Summary = "Update an application")]
    [ProducesResponseType<AdminApplicationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminApplicationResponse>> UpdateApplication(
        Guid id, [FromBody] SaveApplicationRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveApplicationCommand(id, request), cancellationToken));

    /// <summary>Creates a module.</summary>
    [HttpPost("modules")]
    [SwaggerOperation(Summary = "Create a module")]
    [ProducesResponseType<AdminApplicationResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AdminApplicationResponse>> CreateModule(
        [FromBody] SaveModuleRequest request, CancellationToken cancellationToken)
    {
        var app = await dispatcher.SendAsync(new SaveModuleCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, app);
    }

    /// <summary>Edits a module.</summary>
    [HttpPut("modules/{id:guid}")]
    [SwaggerOperation(Summary = "Update a module")]
    [ProducesResponseType<AdminApplicationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AdminApplicationResponse>> UpdateModule(
        Guid id, [FromBody] SaveModuleRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveModuleCommand(id, request), cancellationToken));

    /// <summary>The impact-by-urgency grid.</summary>
    [HttpGet("priority-matrix")]
    [SwaggerOperation(Summary = "Get the priority matrix", Description =
        "All sixteen combinations are returned, filled from the built-in default where "
        + "the organization has no row, so the grid never has holes in it.")]
    [ProducesResponseType<IReadOnlyList<PriorityMatrixCell>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PriorityMatrixCell>>> PriorityMatrix(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetPriorityMatrixQuery(), cancellationToken));

    /// <summary>Rewrites the impact-by-urgency grid.</summary>
    [HttpPut("priority-matrix")]
    [SwaggerOperation(Summary = "Save the priority matrix", Description =
        "Applies to tickets raised from now on. Existing tickets keep the priority they "
        + "were given, because their SLA clocks were started against it.")]
    [ProducesResponseType<IReadOnlyList<PriorityMatrixCell>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<PriorityMatrixCell>>> SavePriorityMatrix(
        [FromBody] SavePriorityMatrixRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SavePriorityMatrixCommand(request), cancellationToken));
}

// ------------------------------------------------------------------------ SLA

[ApiController]
[Route("api/v1/admin/sla")]
[Produces("application/json")]
public sealed class AdminSlaController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Policies with their per-priority targets.</summary>
    [HttpGet("policies")]
    [HasPermission(Permissions.Sla.Manage)]
    [SwaggerOperation(Summary = "List SLA policies", Description =
        "Each policy reports how many clocks are currently running against it, so the "
        + "blast radius of an edit is visible before it is made.")]
    [ProducesResponseType<IReadOnlyList<SlaPolicyResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyResponse>>> Policies(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListSlaPoliciesQuery(), cancellationToken));

    /// <summary>Creates a policy.</summary>
    [HttpPost("policies")]
    [HasPermission(Permissions.Sla.Manage)]
    [SwaggerOperation(Summary = "Create an SLA policy")]
    [ProducesResponseType<SlaPolicyResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SlaPolicyResponse>> CreatePolicy(
        [FromBody] SaveSlaPolicyRequest request, CancellationToken cancellationToken)
    {
        var policy = await dispatcher.SendAsync(new SaveSlaPolicyCommand(null, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, policy);
    }

    /// <summary>Edits a policy and its targets.</summary>
    [HttpPut("policies/{id:guid}")]
    [HasPermission(Permissions.Sla.Manage)]
    [SwaggerOperation(Summary = "Update an SLA policy", Description =
        "New targets apply to clocks started from now on. Running clocks keep the "
        + "deadline they were given — a deadline that moves after the fact makes "
        + "\"did we meet it?\" unanswerable.")]
    [ProducesResponseType<SlaPolicyResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SlaPolicyResponse>> UpdatePolicy(
        Guid id, [FromBody] SaveSlaPolicyRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveSlaPolicyCommand(id, request), cancellationToken));

    /// <summary>Working calendars with their hours and holidays.</summary>
    [HttpGet("calendars")]
    [HasPermission(Permissions.Administration.ManageCalendars)]
    [SwaggerOperation(Summary = "List business calendars", Description =
        "A calendar with no working windows means continuous cover, not no cover.")]
    [ProducesResponseType<IReadOnlyList<BusinessCalendarResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BusinessCalendarResponse>>> Calendars(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListBusinessCalendarsQuery(), cancellationToken));

    /// <summary>Creates a calendar.</summary>
    [HttpPost("calendars")]
    [HasPermission(Permissions.Administration.ManageCalendars)]
    [SwaggerOperation(Summary = "Create a business calendar")]
    [ProducesResponseType<BusinessCalendarResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<BusinessCalendarResponse>> CreateCalendar(
        [FromBody] SaveBusinessCalendarRequest request, CancellationToken cancellationToken)
    {
        var calendar = await dispatcher.SendAsync(
            new SaveBusinessCalendarCommand(null, request), cancellationToken);

        return StatusCode(StatusCodes.Status201Created, calendar);
    }

    /// <summary>Edits a calendar and replaces its weekly hours.</summary>
    [HttpPut("calendars/{id:guid}")]
    [HasPermission(Permissions.Administration.ManageCalendars)]
    [SwaggerOperation(Summary = "Update a business calendar", Description =
        "The time zone is validated against the server's database here rather than "
        + "failing later inside the SLA sweep, where nobody would see it.")]
    [ProducesResponseType<BusinessCalendarResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessCalendarResponse>> UpdateCalendar(
        Guid id, [FromBody] SaveBusinessCalendarRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveBusinessCalendarCommand(id, request), cancellationToken));

    /// <summary>Adds a non-working day.</summary>
    [HttpPost("calendars/{id:guid}/holidays")]
    [HasPermission(Permissions.Administration.ManageCalendars)]
    [SwaggerOperation(Summary = "Add a holiday")]
    [ProducesResponseType<BusinessCalendarResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BusinessCalendarResponse>> AddHoliday(
        Guid id, [FromBody] SaveHolidayRequest request, CancellationToken cancellationToken)
    {
        var calendar = await dispatcher.SendAsync(new AddHolidayCommand(id, request), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, calendar);
    }

    /// <summary>Removes a holiday.</summary>
    [HttpDelete("calendars/{id:guid}/holidays/{holidayId:guid}")]
    [HasPermission(Permissions.Administration.ManageCalendars)]
    [SwaggerOperation(Summary = "Remove a holiday")]
    [ProducesResponseType<BusinessCalendarResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessCalendarResponse>> RemoveHoliday(
        Guid id, Guid holidayId, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new RemoveHolidayCommand(id, holidayId), cancellationToken));
}

// -------------------------------------------------------------------- system

[ApiController]
[Route("api/v1/admin")]
[Produces("application/json")]
public sealed class AdminSystemController(IDispatcher dispatcher) : ControllerBase
{
    /// <summary>Runtime configuration for this organization.</summary>
    [HttpGet("settings")]
    [HasPermission(Permissions.Administration.ConfigureSystem)]
    [SwaggerOperation(Summary = "List system settings", Description =
        "Sensitive values are masked. This endpoint confirms that a credential is set; "
        + "it does not hand it back to a browser.")]
    [ProducesResponseType<IReadOnlyList<SystemSettingResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SystemSettingResponse>>> Settings(
        CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new ListSystemSettingsQuery(), cancellationToken));

    /// <summary>Creates or updates an organization-level setting.</summary>
    [HttpPut("settings")]
    [HasPermission(Permissions.Administration.ConfigureSystem)]
    [SwaggerOperation(Summary = "Save a system setting", Description =
        "Always writes a row owned by the caller's organization; global defaults are "
        + "never edited from here, because that would silently change every tenant. "
        + "Sending back a masked value leaves the stored secret untouched.")]
    [ProducesResponseType<SystemSettingResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SystemSettingResponse>> SaveSetting(
        [FromBody] SaveSystemSettingRequest request, CancellationToken cancellationToken) =>
        Ok(await dispatcher.SendAsync(new SaveSystemSettingCommand(request), cancellationToken));

    /// <summary>Removes this organization's override, restoring the global default.</summary>
    [HttpDelete("settings/{id:guid}")]
    [HasPermission(Permissions.Administration.ConfigureSystem)]
    [SwaggerOperation(Summary = "Delete a system setting override")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSetting(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteSystemSettingCommand(id), cancellationToken);
        return NoContent();
    }

    /// <summary>The lookup lists every administration form needs.</summary>
    [HttpGet("reference")]
    [SwaggerOperation(Summary = "Administration reference data", Description =
        "Departments, offices, teams, roles, categories, SLA policies, calendars and "
        + "users in one request. These lists are small, change rarely and are needed "
        + "together on every form.")]
    [ProducesResponseType<AdminReferenceData>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminReferenceData>> Reference(CancellationToken cancellationToken) =>
        Ok(await dispatcher.QueryAsync(new GetAdminReferenceDataQuery(), cancellationToken));
}
