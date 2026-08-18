import { describe, expect, it } from 'vitest';
import { visibleNavigation } from './navigation';

function permissionChecker(...granted) {
  const set = new Set(granted);
  return (permission) => set.has(permission);
}

describe('visibleNavigation', () => {
  it('always shows items that need no permission', () => {
    const groups = visibleNavigation(permissionChecker());
    const labels = groups.flatMap((g) => g.items.map((i) => i.label));

    expect(labels).toContain('Dashboard');
    expect(labels).toContain('My profile');
  });

  it('hides administration from a requester', () => {
    // A requester holds ticket.create and ticket.view_own but nothing administrative.
    const groups = visibleNavigation(permissionChecker('ticket.create', 'ticket.view_own'));
    const groupLabels = groups.map((g) => g.label);

    expect(groupLabels).not.toContain('Administration');
    expect(groupLabels).toContain('Tickets');
  });

  it('drops a group entirely once every item in it is filtered out', () => {
    const groups = visibleNavigation(permissionChecker());

    expect(groups.map((g) => g.label)).toEqual(['Overview']);
  });

  it('shows administration to a user holding those permissions', () => {
    const groups = visibleNavigation(
      permissionChecker('users.manage', 'roles.manage', 'system.configure'),
    );

    const admin = groups.find((g) => g.label === 'Administration');
    expect(admin).toBeDefined();
    expect(admin.items.map((i) => i.label)).toEqual([
      'Users',
      'Roles & permissions',
      'System settings',
    ]);
  });

  it('marks modules whose backend does not exist yet', () => {
    // Guards against a route being switched on in the navigation before its API is
    // built, which would send testers to a page that cannot work. Escalations is the
    // canonical example: the queue is designed but the SLA engine that fills it is not.
    const groups = visibleNavigation(permissionChecker('escalation.view', 'reports.view'));

    const escalations = groups
      .flatMap((g) => g.items)
      .find((i) => i.to === '/escalations');

    expect(escalations.available).toBe(false);
  });

  it('shows the ticket routes as built', () => {
    const groups = visibleNavigation(permissionChecker('ticket.view_own', 'ticket.create'));
    const tickets = groups.find((g) => g.label === 'Tickets');

    expect(tickets.items.map((i) => i.label)).toEqual(['All tickets', 'Raise a ticket']);
    expect(tickets.items.every((item) => item.available)).toBe(true);
  });
});
