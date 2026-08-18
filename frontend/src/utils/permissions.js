/**
 * Permission keys used by the UI. Mirrors the backend catalogue in
 * SupportTicketing.Domain.Identity.Permissions.
 *
 * Referencing a constant rather than a raw string means a typo is a build-time
 * problem here instead of a control that silently never appears.
 */
export const PERMISSIONS = {
  TICKET_CREATE: 'ticket.create',
  TICKET_VIEW_OWN: 'ticket.view_own',
  TICKET_VIEW_ASSIGNED: 'ticket.view_assigned',
  TICKET_VIEW_TEAM: 'ticket.view_team',
  TICKET_VIEW_DEPARTMENT: 'ticket.view_department',
  TICKET_VIEW_ORGANIZATION: 'ticket.view_organization',
  TICKET_VIEW_ALL: 'ticket.view_all',
  TICKET_ASSIGN: 'ticket.assign',
  TICKET_ACCEPT: 'ticket.accept',
  TICKET_RESOLVE: 'ticket.resolve',
  TICKET_CLOSE: 'ticket.close',
  TICKET_INTERNAL_NOTE: 'ticket.internal_note',
  ESCALATION_VIEW: 'escalation.view',
  ESCALATION_MANAGE: 'escalation.manage',
  SLA_VIEW: 'sla.view',
  SLA_MANAGE: 'sla.manage',
  KNOWLEDGE_VIEW: 'knowledge.view',
  REPORTS_VIEW: 'reports.view',
  REPORTS_EXPORT: 'reports.export',
  AUDIT_VIEW: 'audit.view',
  USERS_MANAGE: 'users.manage',
  ROLES_MANAGE: 'roles.manage',
  TEAMS_MANAGE: 'teams.manage',
  CATALOG_MANAGE: 'catalog.manage',
  SYSTEM_CONFIGURE: 'system.configure',
  AI_USE: 'ai.use',
  AI_CONFIGURE: 'ai.configure',
};

/** Matches the DataScope enum on the server. */
export const SCOPE_LABELS = {
  1: 'Own tickets only',
  2: 'Tickets assigned to you',
  3: 'Your teams',
  4: 'Your department',
  5: 'Your organization',
  6: 'All organizations',
};
