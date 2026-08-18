import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { authService } from '@/services/authService';
import { useAuth } from '@/contexts/AuthContext';
import { Badge, Card, CardBody, CardHeader, ErrorState, Skeleton } from '@/components/ui';
import s from './DashboardPage.module.css';

const CATEGORY_COLORS = [
  'var(--c-primary)',
  'var(--c-info)',
  'var(--c-success)',
  'var(--c-warning)',
  'var(--c-priority-critical)',
  'var(--c-priority-low)',
  'var(--c-primary-hover)',
];

/*
  This dashboard shows only data the API actually returns today: identity, roles,
  effective permissions and team membership. It deliberately does not display
  ticket counts, SLA compliance or CSAT — those endpoints do not exist yet, and
  showing invented numbers would make an unfinished system look finished.
*/
export function DashboardPage() {
  const { user: cachedUser } = useAuth();

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: authService.me,
    // The cached copy from sign-in renders instantly while the fresh copy loads.
    placeholderData: cachedUser,
    staleTime: 60_000,
  });

  const user = data ?? cachedUser;

  const permissionsByCategory = useMemo(() => {
    const groups = new Map();

    for (const permission of user?.permissions ?? []) {
      const [category] = permission.split('.');
      if (!groups.has(category)) {
        groups.set(category, []);
      }
      groups.get(category).push(permission);
    }

    return [...groups.entries()]
      .map(([category, permissions]) => ({ category, permissions, count: permissions.length }))
      .sort((a, b) => b.count - a.count);
  }, [user?.permissions]);

  if (isLoading && !user) {
    return (
      <div className={s.grid}>
        {[0, 1, 2].map((i) => (
          <Card key={i}>
            <CardBody>
              <Skeleton height={18} width="45%" />
              <div style={{ marginTop: 14, display: 'grid', gap: 9 }}>
                <Skeleton height={12} />
                <Skeleton height={12} width="80%" />
                <Skeleton height={12} width="60%" />
              </div>
            </CardBody>
          </Card>
        ))}
      </div>
    );
  }

  if (isError && !user) {
    return <ErrorState error={error} onRetry={refetch} title="Could not load your dashboard" />;
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.greeting}>Good day, {user?.fullName?.split(' ')[0]}</h2>
          <p className={s.subline}>
            {user?.jobTitle ? `${user.jobTitle} · ` : ''}
            {user?.organizationName}
            {user?.departmentName ? ` · ${user.departmentName}` : ''}
          </p>
        </div>

        <div className={s.roleRow}>
          {user?.roles?.map((role) => (
            <Badge key={role} tone="primary" dot>
              {role}
            </Badge>
          ))}
        </div>
      </header>

      <div className={s.grid}>
        <Card>
          <CardHeader title="Your account" subtitle="Straight from the API, not cached locally" />
          <CardBody>
            <dl className={s.dl}>
              <dt className={s.dt}>Name</dt>
              <dd className={s.dd}>{user?.fullName}</dd>

              <dt className={s.dt}>Email</dt>
              <dd className={s.dd}>{user?.email}</dd>

              <dt className={s.dt}>Organization</dt>
              <dd className={s.dd}>{user?.organizationName}</dd>

              <dt className={s.dt}>Department</dt>
              <dd className={s.dd}>{user?.departmentName ?? '—'}</dd>

              <dt className={s.dt}>Office</dt>
              <dd className={s.dd}>{user?.officeName ?? '—'}</dd>

              <dt className={s.dt}>Time zone</dt>
              <dd className={s.dd}>{user?.timeZoneId}</dd>

              <dt className={s.dt}>Two-factor</dt>
              <dd className={s.dd}>
                {user?.twoFactorEnabled ? (
                  <Badge tone="success">Enabled</Badge>
                ) : (
                  <Badge tone="neutral">Not enabled</Badge>
                )}
              </dd>
            </dl>
          </CardBody>
        </Card>

        <Card>
          <CardHeader
            title="Teams"
            subtitle={
              user?.teams?.length
                ? `${user.teams.length} membership${user.teams.length === 1 ? '' : 's'}`
                : 'No team memberships'
            }
          />
          <CardBody>
            {user?.teams?.length ? (
              user.teams.map((team) => (
                <div key={team.teamId} className={s.teamRow}>
                  <span>{team.teamName}</span>
                  <Badge tone={team.roleInTeam === 'Lead' ? 'warning' : 'neutral'}>
                    {team.roleInTeam}
                  </Badge>
                </div>
              ))
            ) : (
              <p style={{ fontSize: 'var(--fs-sm)', color: 'var(--c-text-2)' }}>
                You are not a member of any support team. Requesters do not need one.
              </p>
            )}
          </CardBody>
        </Card>

        <Card>
          <CardHeader
            title="Access at a glance"
            subtitle={`${user?.permissions?.length ?? 0} effective permissions`}
          />
          <CardBody>
            <div className={s.chartWrap}>
              <ResponsiveContainer width="100%" height="100%">
                <BarChart
                  data={permissionsByCategory}
                  layout="vertical"
                  margin={{ top: 4, right: 16, bottom: 4, left: 8 }}
                >
                  <CartesianGrid horizontal={false} stroke="var(--c-border)" />
                  <XAxis
                    type="number"
                    allowDecimals={false}
                    tick={{ fill: 'var(--c-text-3)', fontSize: 11 }}
                    stroke="var(--c-border-strong)"
                  />
                  <YAxis
                    type="category"
                    dataKey="category"
                    width={92}
                    tick={{ fill: 'var(--c-text-2)', fontSize: 11 }}
                    stroke="var(--c-border-strong)"
                  />
                  <Tooltip
                    cursor={{ fill: 'var(--c-surface-3)' }}
                    contentStyle={{
                      background: 'var(--c-surface)',
                      border: '1px solid var(--c-border)',
                      borderRadius: 6,
                      fontSize: 12,
                      color: 'var(--c-text)',
                    }}
                    formatter={(value) => [`${value} permissions`, '']}
                  />
                  <Bar dataKey="count" radius={[0, 3, 3, 0]} barSize={14}>
                    {permissionsByCategory.map((entry, index) => (
                      <Cell key={entry.category} fill={CATEGORY_COLORS[index % CATEGORY_COLORS.length]} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </div>
          </CardBody>
        </Card>

        <Card className={s.wide}>
          <CardHeader
            title="What you are permitted to do"
            subtitle="The backend re-checks every one of these on every request"
          />
          <CardBody>
            {permissionsByCategory.map((group) => (
              <div key={group.category} className={s.permGroup}>
                <div className={s.permGroupHead}>
                  <span className={s.permGroupName}>{group.category.replace('_', ' ')}</span>
                  <Badge tone="neutral">{group.count}</Badge>
                </div>
                <div className={s.permList}>
                  {group.permissions.map((permission) => (
                    <span key={permission} className={s.perm}>
                      {permission}
                    </span>
                  ))}
                </div>
              </div>
            ))}
          </CardBody>
        </Card>

        <Card className={s.wide}>
          <CardHeader
            title="Not built yet"
            subtitle="Listed so nothing here is mistaken for a defect"
          />
          <CardBody>
            <div className={s.roadmap}>
              {ROADMAP.map((phase) => (
                <div key={phase.name} className={s.phase}>
                  <div className={s.phaseHead}>
                    <span className={s.phaseName}>{phase.name}</span>
                    <Badge tone={phase.tone}>{phase.status}</Badge>
                  </div>
                  <ul className={s.phaseItems}>
                    {phase.items.map((item) => (
                      <li key={item}>{item}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </CardBody>
        </Card>
      </div>
    </>
  );
}

const ROADMAP = [
  {
    name: 'Phase 1',
    status: 'Done',
    tone: 'success',
    items: ['Authentication', 'Organizations and users', 'Roles and permissions', 'Teams and master data'],
  },
  {
    name: 'Phase 2',
    status: 'Next',
    tone: 'warning',
    items: ['Ticket creation', 'Assignment and lifecycle', 'Comments and attachments', 'Audit trail'],
  },
  {
    name: 'Phase 3',
    status: 'Planned',
    tone: 'neutral',
    items: ['SLA engine', 'Business calendars', 'Escalations', 'Notifications and SignalR'],
  },
  {
    name: 'Phase 4',
    status: 'Planned',
    tone: 'neutral',
    items: ['Dashboards', 'Reporting', 'Knowledge base', 'Satisfaction ratings'],
  },
  {
    name: 'Phase 5',
    status: 'Planned',
    tone: 'neutral',
    items: ['ERP record links', 'Email intake', 'AI assistance'],
  },
];
