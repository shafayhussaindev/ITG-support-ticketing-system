/*
  Navigation definition.

  Each item declares the permission the backend requires. Hiding an item the user
  cannot use is a usability decision — the API enforces the same check again, so a
  user who types the URL directly still gets a 403 or 404.

  `available: false` marks a destination whose backend does not exist yet. It is
  rendered with a "Planned" badge and routes to a page that says so plainly, rather
  than to a mock screen that looks functional and is not.
*/

export const navigation = [
  {
    label: 'Overview',
    items: [
      { to: '/dashboard', icon: '▦', label: 'Dashboard', available: true },
      { to: '/profile', icon: '◍', label: 'My profile', available: true },
    ],
  },
  {
    label: 'Tickets',
    items: [
      { to: '/tickets', icon: '≡', label: 'All tickets', permission: 'ticket.view_own', available: true },
      { to: '/tickets/new', icon: '＋', label: 'Raise a ticket', permission: 'ticket.create', available: true },
      {
        to: '/tickets?mine=true&openOnly=true',
        icon: '◉',
        label: 'My assigned',
        permission: 'ticket.view_assigned',
        available: true,
      },
      {
        to: '/tickets?unassigned=true&openOnly=true',
        icon: '⬒',
        label: 'Unassigned queue',
        permission: 'ticket.view_team',
        available: true,
      },
      { to: '/escalations', icon: '▲', label: 'Escalations', permission: 'escalation.view', available: true },
    ],
  },
  {
    label: 'Insight',
    items: [
      { to: '/reports', icon: '◔', label: 'Reports', permission: 'reports.view', available: false },
      { to: '/knowledge', icon: '❑', label: 'Knowledge base', permission: 'knowledge.view', available: true },
      { to: '/audit', icon: '◧', label: 'Audit log', permission: 'audit.view', available: false },
    ],
  },
  {
    label: 'Administration',
    items: [
      { to: '/admin/users', icon: '◐', label: 'Users', permission: 'users.manage', available: false },
      { to: '/admin/roles', icon: '◑', label: 'Roles & permissions', permission: 'roles.manage', available: false },
      { to: '/admin/teams', icon: '◒', label: 'Teams', permission: 'teams.manage', available: false },
      { to: '/admin/catalog', icon: '◓', label: 'Categories', permission: 'catalog.manage', available: false },
      { to: '/admin/sla', icon: '◴', label: 'SLA policies', permission: 'sla.manage', available: false },
      { to: '/admin/ai', icon: '◈', label: 'AI assistance', permission: 'ai.configure', available: true },
      { to: '/admin/settings', icon: '⚙', label: 'System settings', permission: 'system.configure', available: false },
    ],
  },
];

/** Filters the navigation tree down to what this principal may see. */
export function visibleNavigation(can) {
  return navigation
    .map((group) => ({
      ...group,
      items: group.items.filter((item) => !item.permission || can(item.permission)),
    }))
    .filter((group) => group.items.length > 0);
}
