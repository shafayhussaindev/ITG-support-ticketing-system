import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate } from 'react-router-dom';
import { AppLayout } from '@/components/layout/AppLayout';
import { ProtectedRoute } from './ProtectedRoute';
import { LoginPage } from '@/features/auth/LoginPage';
import { NotImplementedPage } from '@/pages/NotImplementedPage';
import { NotFoundPage } from '@/pages/NotFoundPage';
import { LoadingState } from '@/components/ui';

// The dashboard pulls in the charting library, which is by far the largest
// dependency. Loading it on demand keeps it out of the initial bundle, so the
// sign-in screen — the one page every user waits for — stays small.
const DashboardPage = lazy(() =>
  import('@/features/dashboard/DashboardPage').then((m) => ({ default: m.DashboardPage })),
);

const ProfilePage = lazy(() =>
  import('@/features/profile/ProfilePage').then((m) => ({ default: m.ProfilePage })),
);

const TicketListPage = lazy(() =>
  import('@/features/tickets/TicketListPage').then((m) => ({ default: m.TicketListPage })),
);

const CreateTicketPage = lazy(() =>
  import('@/features/tickets/CreateTicketPage').then((m) => ({ default: m.CreateTicketPage })),
);

const TicketDetailPage = lazy(() =>
  import('@/features/tickets/TicketDetailPage').then((m) => ({ default: m.TicketDetailPage })),
);

function lazyRoute(element) {
  return <Suspense fallback={<LoadingState label="Loading" />}>{element}</Suspense>;
}

/**
 * Routes for modules that are navigable but not yet implemented. Declaring them
 * here rather than writing a stub component each keeps the honesty consistent and
 * makes the remaining work countable.
 */
const planned = [
  {
    path: 'escalations',
    permission: 'escalation.view',
    title: 'Escalations',
    phase: 'Phase 3',
    description:
      'The SLA breach and escalation queue, driven by a background service rather than by a browser tab being left open.',
    endpoints: ['GET /api/v1/escalations', 'POST /api/v1/escalations/{id}/acknowledge'],
  },
  {
    path: 'reports',
    permission: 'reports.view',
    title: 'Reports and analytics',
    phase: 'Phase 4',
    description:
      'KPI cards and charts for volume, SLA compliance, response and resolution times, backlog, agent workload and CSAT, each drilling through to the underlying ticket list.',
    endpoints: ['GET /api/v1/reports/dashboard', 'POST /api/v1/reports/export'],
  },
  {
    path: 'knowledge',
    permission: 'knowledge.view',
    title: 'Knowledge base',
    phase: 'Phase 4',
    description:
      'Articles with draft, review, published and archived states, version history, and suggestions offered while a requester types a ticket.',
    endpoints: ['GET /api/v1/knowledge/articles', 'GET /api/v1/knowledge/search'],
  },
  {
    path: 'audit',
    permission: 'audit.view',
    title: 'Audit log',
    phase: 'Phase 4',
    description:
      'The immutable history. Every field change, status transition, assignment and permission change, filterable and exportable. The table already exists and is being written to today.',
    endpoints: ['GET /api/v1/audit', 'GET /api/v1/audit/tickets/{id}/reconstruct'],
  },
  {
    path: 'admin/users',
    permission: 'users.manage',
    title: 'User management',
    phase: 'Phase 2',
    description: 'Create, deactivate and edit users, and assign their roles and teams.',
    endpoints: ['GET /api/v1/users', 'POST /api/v1/users', 'PUT /api/v1/users/{id}/roles'],
  },
  {
    path: 'admin/roles',
    permission: 'roles.manage',
    title: 'Roles and permissions',
    phase: 'Phase 2',
    description:
      'Edit which permissions each role carries. Roles are database rows, not hardcoded checks, so changes take effect without a deployment.',
    endpoints: ['GET /api/v1/roles', 'PUT /api/v1/roles/{id}/permissions', 'GET /api/v1/permissions'],
  },
  {
    path: 'admin/teams',
    permission: 'teams.manage',
    title: 'Teams and routing',
    phase: 'Phase 2',
    description: 'Team membership, capacity weighting and the routing rules that pick an assignee.',
    endpoints: ['GET /api/v1/teams', 'PUT /api/v1/teams/{id}/members'],
  },
  {
    path: 'admin/catalog',
    permission: 'catalog.manage',
    title: 'Categories and modules',
    phase: 'Phase 2',
    description:
      'Categories, subcategories, applications, modules, and the impact-by-urgency priority matrix. The matrix is already seeded with sixteen cells.',
    endpoints: ['GET /api/v1/categories', 'PUT /api/v1/priority-matrix'],
  },
  {
    path: 'admin/sla',
    permission: 'sla.manage',
    title: 'SLA policies and calendars',
    phase: 'Phase 3',
    description:
      'Response and resolution targets per priority, business hours, weekends, holidays and pause conditions.',
    endpoints: ['GET /api/v1/sla-policies', 'GET /api/v1/business-calendars'],
  },
  {
    path: 'admin/settings',
    permission: 'system.configure',
    title: 'System settings',
    phase: 'Phase 4',
    description: 'Runtime configuration, notification rules and integration settings.',
    endpoints: ['GET /api/v1/system/settings'],
  },
];

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/', element: <Navigate to="/dashboard" replace /> },
  {
    path: '/',
    element: (
      <ProtectedRoute>
        <AppLayout />
      </ProtectedRoute>
    ),
    children: [
      { path: 'dashboard', element: lazyRoute(<DashboardPage />) },
      { path: 'profile', element: lazyRoute(<ProfilePage />) },

      // Ordered so the literal /tickets/new is matched before the /tickets/:id
      // parameter route, which would otherwise treat "new" as an identifier.
      {
        path: 'tickets',
        element: (
          <ProtectedRoute permission="ticket.view_own">{lazyRoute(<TicketListPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'tickets/new',
        element: (
          <ProtectedRoute permission="ticket.create">{lazyRoute(<CreateTicketPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'tickets/:id',
        element: (
          <ProtectedRoute permission="ticket.view_own">{lazyRoute(<TicketDetailPage />)}</ProtectedRoute>
        ),
      },

      ...planned.map(({ path, permission, ...page }) => ({
        path,
        element: (
          <ProtectedRoute permission={permission}>
            <NotImplementedPage {...page} />
          </ProtectedRoute>
        ),
      })),

      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
