import { lazy, Suspense } from 'react';
import { createBrowserRouter } from 'react-router-dom';
import { AppLayout } from '@/components/layout/AppLayout';
import { ProtectedRoute } from './ProtectedRoute';
import { LoginPage } from '@/features/auth/LoginPage';
import { RootRoute } from './RootRoute';
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

const EscalationsPage = lazy(() =>
  import('@/features/escalations/EscalationsPage').then((m) => ({ default: m.EscalationsPage })),
);

const KnowledgePage = lazy(() =>
  import('@/features/knowledge/KnowledgePage').then((m) => ({ default: m.KnowledgePage })),
);

const ReportsPage = lazy(() =>
  import('@/features/reports/ReportsPage').then((m) => ({ default: m.ReportsPage })),
);

const AuditLogPage = lazy(() =>
  import('@/features/audit/AuditLogPage').then((m) => ({ default: m.AuditLogPage })),
);

const UsersPage = lazy(() =>
  import('@/features/admin/UsersPage').then((m) => ({ default: m.UsersPage })),
);

const RolesPage = lazy(() =>
  import('@/features/admin/RolesPage').then((m) => ({ default: m.RolesPage })),
);

const TeamsPage = lazy(() =>
  import('@/features/admin/TeamsPage').then((m) => ({ default: m.TeamsPage })),
);

const CatalogPage = lazy(() =>
  import('@/features/admin/CatalogPage').then((m) => ({ default: m.CatalogPage })),
);

const SlaPoliciesPage = lazy(() =>
  import('@/features/admin/SlaPoliciesPage').then((m) => ({ default: m.SlaPoliciesPage })),
);

const SystemSettingsPage = lazy(() =>
  import('@/features/admin/SystemSettingsPage').then((m) => ({ default: m.SystemSettingsPage })),
);

const AiSettingsPage = lazy(() =>
  import('@/features/admin/AiSettingsPage').then((m) => ({ default: m.AiSettingsPage })),
);

const ArticlePage = lazy(() =>
  import('@/features/knowledge/ArticlePage').then((m) => ({ default: m.ArticlePage })),
);

const WorkloadPage = lazy(() =>
  import('@/features/admin/WorkloadPage').then((m) => ({ default: m.WorkloadPage })),
);

const ArticleEditorPage = lazy(() =>
  import('@/features/knowledge/ArticleEditorPage').then((m) => ({ default: m.ArticleEditorPage })),
);

function lazyRoute(element) {
  return <Suspense fallback={<LoadingState label="Loading" />}>{element}</Suspense>;
}

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/', element: <RootRoute /> },
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
        path: 'escalations',
        element: (
          <ProtectedRoute permission="escalation.view">{lazyRoute(<EscalationsPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'knowledge',
        element: (
          <ProtectedRoute permission="knowledge.view">{lazyRoute(<KnowledgePage />)}</ProtectedRoute>
        ),
      },
      {
        // Declared before 'knowledge/:id', which would otherwise match this and try to
        // load an article whose identifier is the word "new".
        path: 'knowledge/new',
        element: (
          <ProtectedRoute permission="knowledge.create">{lazyRoute(<ArticleEditorPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'knowledge/:id',
        element: (
          <ProtectedRoute permission="knowledge.view">{lazyRoute(<ArticlePage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'knowledge/:id/edit',
        element: (
          <ProtectedRoute permission="knowledge.edit">{lazyRoute(<ArticleEditorPage />)}</ProtectedRoute>
        ),
      },
      {
        // Two permissions reach this and the rows differ by which, so the guard is the
        // looser of the pair and the handler narrows what comes back.
        path: 'admin/workload',
        element: (
          <ProtectedRoute permission="reports.view_team">{lazyRoute(<WorkloadPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'reports',
        element: (
          <ProtectedRoute permission="reports.view">{lazyRoute(<ReportsPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'audit',
        element: (
          <ProtectedRoute permission="audit.view">{lazyRoute(<AuditLogPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/users',
        element: (
          <ProtectedRoute permission="users.manage">{lazyRoute(<UsersPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/roles',
        element: (
          <ProtectedRoute permission="roles.manage">{lazyRoute(<RolesPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/teams',
        element: (
          <ProtectedRoute permission="teams.manage">{lazyRoute(<TeamsPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/catalog',
        element: (
          <ProtectedRoute permission="catalog.manage">{lazyRoute(<CatalogPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/sla',
        element: (
          <ProtectedRoute permission="sla.manage">{lazyRoute(<SlaPoliciesPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'admin/settings',
        element: (
          <ProtectedRoute permission="system.configure">
            {lazyRoute(<SystemSettingsPage />)}
          </ProtectedRoute>
        ),
      },
      {
        path: 'admin/ai',
        element: (
          <ProtectedRoute permission="ai.configure">{lazyRoute(<AiSettingsPage />)}</ProtectedRoute>
        ),
      },
      {
        path: 'tickets/:id',
        element: (
          <ProtectedRoute permission="ticket.view_own">{lazyRoute(<TicketDetailPage />)}</ProtectedRoute>
        ),
      },

      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
