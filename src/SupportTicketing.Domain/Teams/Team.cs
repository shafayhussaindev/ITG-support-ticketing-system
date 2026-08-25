using SupportTicketing.Domain.Common;
using SupportTicketing.Domain.Identity;
using SupportTicketing.Domain.Organizations;

namespace SupportTicketing.Domain.Teams;

/// <summary>A support team, for example IT Support, ERP Support, or QA Support.</summary>
public class Team : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }

    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }

    /// <summary>Team lead. Receives level-one escalations for this team's tickets.</summary>
    public Guid? TeamLeadId { get; set; }
    public User? TeamLead { get; set; }

    /// <summary>Fallback team for tickets this team transfers out or cannot accept.</summary>
    public Guid? EscalationTeamId { get; set; }

    /// <summary>Minutes an unaccepted ticket may sit in this team's queue before escalating.</summary>
    public int AcceptanceTimeoutMinutes { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    public ICollection<TeamMember> Members { get; set; } = [];
}

public class TeamMember : AuditableEntity
{
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Role within the team, distinct from the system-wide security role.</summary>
    public TeamRole RoleInTeam { get; set; } = TeamRole.Member;

    /// <summary>
    /// Relative share of automatically routed work. 1.0 is a full share; 0.5 gives
    /// this member half as many round-robin and load-balanced assignments.
    /// </summary>
    public decimal CapacityWeight { get; set; } = 1.0m;

    public bool IsActive { get; set; } = true;
}

public enum TeamRole
{
    Member = 1,
    Lead = 2,
    Specialist = 3,
    Observer = 4
}

/// <summary>A capability a staff member can hold, used by skill-based routing.</summary>
public class Skill : TenantEntity
{
    public required string Name { get; set; }
    public required string Code { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<UserSkill> UserSkills { get; set; } = [];
}

public class UserSkill : AuditableEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid SkillId { get; set; }
    public Skill? Skill { get; set; }

    /// <summary>1 (novice) to 5 (expert). Routing prefers the lowest sufficient level to spread load.</summary>
    public int Proficiency { get; set; } = 3;
}
