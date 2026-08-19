using SupportTicketing.Domain.Enums;
using SupportTicketing.Domain.Identity;

namespace SupportTicketing.Infrastructure.Persistence.Seeding;

/// <summary>
/// The permission catalogue and the seven system roles, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the demo seeder and the production bootstrapper. Two copies of "what a
/// Support Agent may do" would drift the first time somebody adjusted one of them,
/// and the deployment that got the stale copy would be the production one.
/// </para>
/// <para>
/// These are starting points, not a contract. An administrator can edit any role's
/// permissions afterwards, and the bootstrapper does not force them back.
/// </para>
/// </remarks>
public static class SystemRoleDefinitions
{
    /// <summary>Name, default data scope and rank for each seeded role, lowest authority first.</summary>
    public static IReadOnlyList<(string Name, DataScope Scope, int Rank)> Roles { get; } =
    [
        (RoleNames.Requester, DataScope.Own, 10),
        (RoleNames.SupportAgent, DataScope.Team, 20),
        (RoleNames.TechnicalSpecialist, DataScope.Team, 30),
        (RoleNames.TeamLead, DataScope.Team, 40),
        (RoleNames.Manager, DataScope.Organization, 50),
        (RoleNames.Administrator, DataScope.Organization, 60),
        (RoleNames.SuperAdmin, DataScope.All, 70),
    ];

    /// <summary>
    /// The permission keys each role starts with.
    /// </summary>
    /// <remarks>
    /// Cumulative where that reflects reality — an agent can do everything a requester
    /// can, because agents raise tickets too — and deliberately not cumulative for the
    /// Administrator, who configures the system rather than working its queues. That is
    /// why an Administrator holds <c>users.manage</c> but only <c>ticket.view_own</c>.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PermissionsByRole { get; } =
        BuildPermissionSets();

    private static Dictionary<string, IReadOnlyList<string>> BuildPermissionSets()
    {
        string[] requester =
        [
            Permissions.Tickets.Create, Permissions.Tickets.ViewOwn, Permissions.Tickets.PublicReply,
            Permissions.Tickets.ConfirmResolution, Permissions.Tickets.Reopen, Permissions.Tickets.Cancel,
            Permissions.Attachments.Upload, Permissions.Attachments.Download,
            Permissions.Knowledge.View, Permissions.Sla.View,
        ];

        string[] agent =
        [
            .. requester,
            Permissions.Tickets.ViewAssigned, Permissions.Tickets.ViewTeam, Permissions.Tickets.Edit,
            Permissions.Tickets.Accept, Permissions.Tickets.ChangeStatus, Permissions.Tickets.Resolve,
            Permissions.Tickets.InternalNote, Permissions.Tickets.LogWork, Permissions.Tickets.LinkRecords,
            Permissions.Tickets.RecordRootCause, Permissions.Escalations.View,
            Permissions.Knowledge.Create, Permissions.Ai.Use,
        ];

        string[] specialist = [.. agent, Permissions.Tickets.Transfer, Permissions.Knowledge.Edit];

        string[] lead =
        [
            .. specialist,
            Permissions.Tickets.Assign, Permissions.Tickets.Reassign, Permissions.Tickets.ChangePriority,
            Permissions.Tickets.Close, Permissions.Escalations.Manage, Permissions.Escalations.Acknowledge,
            Permissions.Reports.ViewTeam, Permissions.Reports.View, Permissions.Knowledge.Publish,
            Permissions.Attachments.Delete,
        ];

        string[] manager =
        [
            .. lead,
            Permissions.Tickets.ViewDepartment, Permissions.Tickets.ViewOrganization,
            Permissions.Reports.ViewOrganization, Permissions.Reports.Export,
            Permissions.Sla.Manage, Permissions.Sla.Override, Permissions.Knowledge.Archive,
        ];

        string[] administrator =
        [
            Permissions.Tickets.ViewOwn, Permissions.Knowledge.View, Permissions.Sla.View,
            Permissions.Sla.Manage, Permissions.Escalations.View, Permissions.Reports.View,
            Permissions.Reports.Export,
            Permissions.Administration.ManageUsers, Permissions.Administration.ManageRoles,
            Permissions.Administration.ManageTeams, Permissions.Administration.ManageCatalog,
            Permissions.Administration.ManageRouting, Permissions.Administration.ManageNotifications,
            Permissions.Administration.ManageCalendars, Permissions.Administration.ConfigureSystem,
            Permissions.Administration.ViewAudit, Permissions.Ai.Configure,
        ];

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [RoleNames.Requester] = requester,
            [RoleNames.SupportAgent] = agent,
            [RoleNames.TechnicalSpecialist] = specialist,
            [RoleNames.TeamLead] = lead,
            [RoleNames.Manager] = manager,
            [RoleNames.Administrator] = administrator,
            [RoleNames.SuperAdmin] = Permissions.All,
        };
    }

    /// <summary>
    /// Turns <c>ticket.view_own</c> into "View own", for the permission picker.
    /// </summary>
    public static string Humanise(string key)
    {
        var action = key.Split('.').Last().Replace('_', ' ');
        return char.ToUpperInvariant(action[0]) + action[1..];
    }
}
