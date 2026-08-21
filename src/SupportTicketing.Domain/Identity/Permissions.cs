namespace SupportTicketing.Domain.Identity;

/// <summary>
/// The complete catalogue of permission keys.
/// </summary>
/// <remarks>
/// These constants exist so code has compile-time safety when referring to a
/// permission. They are <em>not</em> the authorization mechanism: what a role can do
/// is stored in RolePermissions and is editable at runtime. Nothing in the codebase
/// branches on a role name — every check asks whether the principal holds a
/// permission key.
/// </remarks>
public static class Permissions
{
    public static class Tickets
    {
        public const string Create = "ticket.create";
        public const string ViewOwn = "ticket.view_own";
        public const string ViewAssigned = "ticket.view_assigned";
        public const string ViewTeam = "ticket.view_team";
        public const string ViewDepartment = "ticket.view_department";
        public const string ViewOrganization = "ticket.view_organization";
        public const string ViewAll = "ticket.view_all";
        public const string Edit = "ticket.edit";
        public const string Assign = "ticket.assign";
        public const string Reassign = "ticket.reassign";
        public const string Accept = "ticket.accept";
        public const string Transfer = "ticket.transfer";
        public const string ChangePriority = "ticket.change_priority";

        /// <summary>
        /// Whether the holder's own claim of impact and urgency is taken at face value.
        /// </summary>
        /// <remarks>
        /// Without it, a ticket raised by this person is capped at the organization's
        /// configured maximum and what they asked for is recorded alongside it. Everyone
        /// marks their own request urgent; the cap is what stops that inflating every
        /// ticket to Critical and leaving the genuinely critical ones indistinguishable.
        /// </remarks>
        public const string ClaimAnySeverity = "ticket.claim_any_severity";
        public const string ChangeStatus = "ticket.change_status";
        public const string Resolve = "ticket.resolve";
        public const string Close = "ticket.close";
        public const string Reopen = "ticket.reopen";
        public const string Cancel = "ticket.cancel";
        public const string Delete = "ticket.delete";
        public const string InternalNote = "ticket.internal_note";
        public const string PublicReply = "ticket.public_reply";
        public const string LogWork = "ticket.log_work";
        public const string ConfirmResolution = "ticket.confirm_resolution";
        public const string LinkRecords = "ticket.link_records";
        public const string RecordRootCause = "ticket.record_root_cause";
    }

    public static class Attachments
    {
        public const string Upload = "attachment.upload";
        public const string Download = "attachment.download";
        public const string Delete = "attachment.delete";
    }

    public static class Sla
    {
        public const string View = "sla.view";
        public const string Manage = "sla.manage";
        public const string Override = "sla.override";
    }

    public static class Escalations
    {
        public const string View = "escalation.view";
        public const string Manage = "escalation.manage";
        public const string Acknowledge = "escalation.acknowledge";
    }

    public static class Knowledge
    {
        public const string View = "knowledge.view";
        public const string Create = "knowledge.create";
        public const string Edit = "knowledge.edit";
        public const string Publish = "knowledge.publish";
        public const string Archive = "knowledge.archive";
    }

    public static class Reports
    {
        public const string View = "reports.view";
        public const string ViewTeam = "reports.view_team";
        public const string ViewOrganization = "reports.view_organization";
        public const string Export = "reports.export";

        /// <summary>
        /// How individual requesters use the desk, named and side by side.
        /// </summary>
        /// <remarks>
        /// Granted to no role by default, which leaves it with Super Admin alone because
        /// that role holds every permission. Deliberately separate from reports.view:
        /// the other reports describe the desk's performance, and this one describes
        /// named people, which is a different thing to hand out.
        /// </remarks>
        public const string ViewCustomerBehaviour = "reports.customer_behaviour";
    }

    public static class Administration
    {
        public const string ViewAudit = "audit.view";
        public const string ManageUsers = "users.manage";
        public const string ManageRoles = "roles.manage";
        public const string ManageTeams = "teams.manage";
        public const string ManageCatalog = "catalog.manage";
        public const string ManageOrganizations = "organizations.manage";
        public const string ManageRouting = "routing.manage";
        public const string ManageNotifications = "notifications.manage";
        public const string ManageCalendars = "calendars.manage";
        public const string ConfigureSystem = "system.configure";
    }

    public static class Ai
    {
        public const string Use = "ai.use";
        public const string Configure = "ai.configure";
    }

    /// <summary>Every permission key, used by the seeder and the permissions endpoint.</summary>
    public static IReadOnlyList<string> All { get; } = BuildAll();

    private static string[] BuildAll() =>
    [
        Tickets.Create, Tickets.ViewOwn, Tickets.ViewAssigned, Tickets.ViewTeam,
        Tickets.ViewDepartment, Tickets.ViewOrganization, Tickets.ViewAll, Tickets.Edit,
        Tickets.Assign, Tickets.Reassign, Tickets.Accept, Tickets.Transfer,
        Tickets.ChangePriority, Tickets.ClaimAnySeverity, Tickets.ChangeStatus,
        Tickets.Resolve, Tickets.Close,
        Tickets.Reopen, Tickets.Cancel, Tickets.Delete, Tickets.InternalNote,
        Tickets.PublicReply, Tickets.LogWork, Tickets.ConfirmResolution,
        Tickets.LinkRecords, Tickets.RecordRootCause,
        Attachments.Upload, Attachments.Download, Attachments.Delete,
        Sla.View, Sla.Manage, Sla.Override,
        Escalations.View, Escalations.Manage, Escalations.Acknowledge,
        Knowledge.View, Knowledge.Create, Knowledge.Edit, Knowledge.Publish, Knowledge.Archive,
        Reports.View, Reports.ViewTeam, Reports.ViewOrganization, Reports.Export,
        Reports.ViewCustomerBehaviour,
        Administration.ViewAudit, Administration.ManageUsers, Administration.ManageRoles,
        Administration.ManageTeams, Administration.ManageCatalog,
        Administration.ManageOrganizations, Administration.ManageRouting,
        Administration.ManageNotifications, Administration.ManageCalendars,
        Administration.ConfigureSystem,
        Ai.Use, Ai.Configure
    ];
}

/// <summary>
/// Well-known role names used by the development seeder only. Authorization never
/// branches on these — they are labels for permission bundles that an administrator
/// can freely edit at runtime.
/// </summary>
public static class RoleNames
{
    public const string Requester = "Requester";
    public const string SupportAgent = "Support Agent";
    public const string TeamLead = "Team Lead";
    public const string TechnicalSpecialist = "Technical Specialist";
    public const string Manager = "Manager";
    public const string Administrator = "Administrator";
    public const string SuperAdmin = "Super Admin";
}

/// <summary>
/// How far a principal can see. Resolved server-side from the user's roles and
/// explicit data-access grants, then compiled into a query predicate. Never sent
/// by, or trusted from, the client.
/// </summary>
public enum DataScope
{
    /// <summary>Only tickets the user raised.</summary>
    Own = 1,

    /// <summary>Tickets assigned to the user, plus their own.</summary>
    Assigned = 2,

    /// <summary>Every ticket belonging to a team the user is a member of.</summary>
    Team = 3,

    /// <summary>Every ticket in the user's department, including child departments.</summary>
    Department = 4,

    /// <summary>Every ticket in the user's organization.</summary>
    Organization = 5,

    /// <summary>Every ticket in every organization. Reserved for Super Admin and audited.</summary>
    All = 6
}
