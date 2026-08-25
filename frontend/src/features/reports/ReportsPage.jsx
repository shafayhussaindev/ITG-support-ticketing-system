import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Legend, Line, LineChart,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { saveBlob } from '@/services/apiClient';
import { daysAgoIso, reportKeys, reportsService } from '@/services/reportsService';
import { formatMinutes } from '@/services/reportingService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import s from './ReportsPage.module.css';

const REPORTS = [
  { key: 'sla-compliance', label: 'SLA compliance' },
  { key: 'volume-trend', label: 'Volume and backlog' },
  { key: 'staff-performance', label: 'Staff performance' },
  { key: 'satisfaction', label: 'Satisfaction' },

  // Super Admin alone by default: every other report describes the desk, this one
  // describes people, so the tab is hidden rather than shown and refused.
  { key: 'customer-behaviour', label: 'Customer behaviour', permission: 'reports.customer_behaviour' },
];

const PERIODS = [
  { days: 7, label: 'Last 7 days' },
  { days: 30, label: 'Last 30 days' },
  { days: 90, label: 'Last 90 days' },
  { days: 365, label: 'Last 12 months' },
];

const PRIORITY_COLOR = {
  Critical: 'var(--c-priority-critical)',
  High: 'var(--c-priority-high)',
  Medium: 'var(--c-priority-medium)',
  Low: 'var(--c-priority-low)',
};

const SCOPE_LABEL = {
  Own: 'the tickets you raised',
  Assigned: 'tickets assigned to you',
  Team: 'your teams',
  Department: 'your department',
  Organization: 'your organization',
  All: 'every organization',
};

const axis = { fill: 'var(--c-text-3)', fontSize: 11 };

const tooltipStyle = {
  background: 'var(--c-surface)',
  border: '1px solid var(--c-border)',
  borderRadius: 6,
  fontSize: 12,
  color: 'var(--c-text)',
};

/** An em dash beats a zero: they mean different things and only one of them is true. */
function num(value, suffix = '') {
  return value === null || value === undefined ? '—' : `${value}${suffix}`;
}

function shortDate(value) {
  return new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

function ComplianceCell({ percent }) {
  if (percent === null || percent === undefined) {
    return <span className={s.muted}>no data</span>;
  }

  const tone = percent >= 95 ? 'success' : percent >= 85 ? 'warning' : 'danger';
  return <Badge tone={tone}>{percent}%</Badge>;
}

function SlaTable({ title, rows }) {
  if (rows.length === 0) {
    return null;
  }

  return (
    <Card>
      <CardHeader title={title} />
      <CardBody className={s.flush}>
        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">{title}</th>
                <th scope="col">Tracked</th>
                <th scope="col">Met</th>
                <th scope="col">Breached</th>
                <th scope="col">Running</th>
                <th scope="col">Compliance</th>
                <th scope="col">Avg response</th>
                <th scope="col">Avg resolution</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.label}>
                  <th scope="row">{row.label}</th>
                  <td>{row.tracked}</td>
                  <td>{row.resolutionMet}</td>
                  <td>{row.resolutionBreached > 0
                    ? <Badge tone="danger">{row.resolutionBreached}</Badge>
                    : '—'}</td>
                  <td className={s.muted}>{row.unsettled}</td>
                  <td><ComplianceCell percent={row.compliancePercent} /></td>
                  <td>{formatMinutes(row.averageResponseMinutes)}</td>
                  <td>{formatMinutes(row.averageResolutionMinutes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardBody>
    </Card>
  );
}

function SlaComplianceReport({ data }) {
  return (
    <>
      <div className={s.summary}>
        <Card>
          <CardBody className={s.summaryBody}>
            <span className={s.summaryValue}>{num(data.overall.compliancePercent, '%')}</span>
            <span className={s.summaryLabel}>Resolution compliance</span>
            <span className={s.summaryHint}>
              {data.overall.resolutionMet + data.overall.resolutionBreached} of{' '}
              {data.overall.tracked} clocks have settled. Running clocks are excluded —
              they have not failed yet, and counting them either way would misstate the
              period.
            </span>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Compliance by priority" />
          <CardBody>
            {data.byPriority.length === 0 ? (
              <p className={s.muted}>No SLA clocks started in this period.</p>
            ) : (
              <div className={s.chart}>
                <ResponsiveContainer width="100%" height="100%">
                  <BarChart data={data.byPriority} margin={{ top: 6, right: 12, bottom: 4, left: -20 }}>
                    <CartesianGrid stroke="var(--c-border)" vertical={false} />
                    <XAxis dataKey="label" tick={axis} stroke="var(--c-border-strong)" />
                    <YAxis tick={axis} stroke="var(--c-border-strong)" domain={[0, 100]} unit="%" />
                    <Tooltip contentStyle={tooltipStyle} formatter={(v) => `${v}%`} />
                    <Bar dataKey="compliancePercent" name="Compliance" radius={[3, 3, 0, 0]} barSize={34}>
                      {data.byPriority.map((row) => (
                        <Cell key={row.label} fill={PRIORITY_COLOR[row.label] ?? 'var(--c-primary)'} />
                      ))}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
              </div>
            )}
          </CardBody>
        </Card>
      </div>

      <SlaTable title="Priority" rows={data.byPriority} />
      <SlaTable title="Team" rows={data.byTeam} />
      <SlaTable title="Category" rows={data.byCategory} />
    </>
  );
}

function VolumeTrendReport({ data }) {
  return (
    <>
      <Card>
        <CardHeader
          title="Backlog"
          subtitle="Anchored to what was already open when the period began"
        />
        <CardBody>
          <div className={s.tallChart}>
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={data.days} margin={{ top: 6, right: 12, bottom: 4, left: -20 }}>
                <CartesianGrid stroke="var(--c-border)" vertical={false} />
                <XAxis dataKey="date" tick={axis} stroke="var(--c-border-strong)"
                       tickFormatter={shortDate} minTickGap={28} />
                <YAxis tick={axis} stroke="var(--c-border-strong)" allowDecimals={false} />
                <Tooltip contentStyle={tooltipStyle}
                         labelFormatter={(v) => new Date(v).toLocaleDateString()} />
                <Area type="monotone" dataKey="backlog" name="Open at close of day"
                      stroke="var(--c-warning)" fill="var(--c-warning-soft)" strokeWidth={2} />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="Raised against resolved" subtitle="Where the backlog above comes from" />
        <CardBody>
          <div className={s.chart}>
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={data.days} margin={{ top: 6, right: 12, bottom: 4, left: -20 }}>
                <CartesianGrid stroke="var(--c-border)" vertical={false} />
                <XAxis dataKey="date" tick={axis} stroke="var(--c-border-strong)"
                       tickFormatter={shortDate} minTickGap={28} />
                <YAxis tick={axis} stroke="var(--c-border-strong)" allowDecimals={false} />
                <Tooltip contentStyle={tooltipStyle}
                         labelFormatter={(v) => new Date(v).toLocaleDateString()} />
                <Legend wrapperStyle={{ fontSize: 12 }} />
                <Line type="monotone" dataKey="raised" name="Raised"
                      stroke="var(--c-info)" strokeWidth={2} dot={false} />
                <Line type="monotone" dataKey="resolved" name="Resolved"
                      stroke="var(--c-success)" strokeWidth={2} dot={false} />
                <Line type="monotone" dataKey="reopened" name="Reopened"
                      stroke="var(--c-danger)" strokeWidth={2} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </CardBody>
      </Card>

      <div className={s.summary}>
        <BreakdownCard title="By category" rows={data.byCategory} />
        <BreakdownCard title="By type" rows={data.byType} />
        <BreakdownCard title="By source" rows={data.bySource} />
      </div>
    </>
  );
}

function BreakdownCard({ title, rows }) {
  const total = rows.reduce((sum, row) => sum + row.count, 0);

  return (
    <Card>
      <CardHeader title={title} />
      <CardBody>
        {rows.length === 0 ? (
          <p className={s.muted}>Nothing raised in this period.</p>
        ) : (
          <ul className={s.breakdown}>
            {rows.map((row) => (
              <li key={row.label} className={s.breakdownRow}>
                <span className={s.breakdownLabel}>{row.label}</span>
                <span className={s.breakdownBar}>
                  <span
                    className={s.breakdownFill}
                    style={{ width: `${total === 0 ? 0 : (row.count / total) * 100}%` }}
                  />
                </span>
                <span className={s.breakdownCount}>{row.count}</span>
              </li>
            ))}
          </ul>
        )}
      </CardBody>
    </Card>
  );
}

function StaffPerformanceReport({ data }) {
  if (data.staff.length === 0) {
    return (
      <EmptyState
        icon="◔"
        title="No individual figures for you"
        message={
          'Per-person performance needs visibility of a team. Your account can open '
          + 'reports but sees only its own tickets, so there is nothing to break down.'
        }
      />
    );
  }

  return (
    <Card>
      <CardHeader
        title="Staff performance"
        subtitle="Volume shown beside reopens, breaches and satisfaction, because volume alone rewards the wrong thing"
      />
      <CardBody className={s.flush}>
        <div className={s.tableWrap}>
          <table className={s.table}>
            <thead>
              <tr>
                <th scope="col">Staff</th>
                <th scope="col">Team</th>
                <th scope="col">Open</th>
                <th scope="col">Resolved</th>
                <th scope="col">Closed</th>
                <th scope="col">Reopened</th>
                <th scope="col">Breached</th>
                <th scope="col">Avg response</th>
                <th scope="col">Avg resolution</th>
                <th scope="col">Satisfaction</th>
              </tr>
            </thead>
            <tbody>
              {data.staff.map((person) => (
                <tr key={person.staffId}>
                  <th scope="row">{person.staffName}</th>
                  <td className={s.muted}>{person.teamName ?? '—'}</td>
                  <td>{person.openTickets}</td>
                  <td>{person.resolvedInPeriod}</td>
                  <td>{person.closedInPeriod}</td>
                  <td>{person.reopenedAfterResolution > 0
                    ? <Badge tone="warning">{person.reopenedAfterResolution}</Badge>
                    : '—'}</td>
                  <td>{person.slaBreached > 0
                    ? <Badge tone="danger">{person.slaBreached}</Badge>
                    : '—'}</td>
                  <td>{formatMinutes(person.averageFirstResponseMinutes)}</td>
                  <td>{formatMinutes(person.averageResolutionMinutes)}</td>
                  <td>
                    {person.satisfactionResponses === 0 ? (
                      <span className={s.muted}>no responses</span>
                    ) : (
                      <>
                        {person.averageSatisfaction} / 5{' '}
                        <span className={s.muted}>({person.satisfactionResponses})</span>
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardBody>
    </Card>
  );
}

function SatisfactionReport({ data }) {
  return (
    <>
      <div className={s.summary}>
        <Card>
          <CardBody className={s.summaryBody}>
            <span className={s.summaryValue}>
              {data.averageRating === null ? '—' : `${data.averageRating} / 5`}
            </span>
            <span className={s.summaryLabel}>Average rating</span>
            <span className={s.summaryHint}>
              {data.responses} of {data.eligible} finished tickets were rated
              {data.responsePercent === null ? '' : ` — ${data.responsePercent}%`}. The
              response rate belongs beside the average: a high score from few people is
              not the same claim as a fair score from many.
            </span>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Distribution" />
          <CardBody>
            <div className={s.chart}>
              <ResponsiveContainer width="100%" height="100%">
                <BarChart data={data.distribution} margin={{ top: 6, right: 12, bottom: 4, left: -24 }}>
                  <CartesianGrid stroke="var(--c-border)" vertical={false} />
                  <XAxis dataKey="label" tick={axis} stroke="var(--c-border-strong)" unit="★" />
                  <YAxis tick={axis} stroke="var(--c-border-strong)" allowDecimals={false} />
                  <Tooltip contentStyle={tooltipStyle} />
                  <Bar dataKey="count" name="Responses" radius={[3, 3, 0, 0]} barSize={34}>
                    {data.distribution.map((row) => (
                      <Cell
                        key={row.label}
                        fill={Number(row.label) >= 4 ? 'var(--c-success)'
                          : Number(row.label) === 3 ? 'var(--c-warning)' : 'var(--c-danger)'}
                      />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </div>
          </CardBody>
        </Card>
      </div>

      {data.byStaff.length > 0 ? (
        <Card>
          <CardHeader title="By staff member" />
          <CardBody className={s.flush}>
            <div className={s.tableWrap}>
              <table className={s.table}>
                <thead>
                  <tr>
                    <th scope="col">Staff</th>
                    <th scope="col">Responses</th>
                    <th scope="col">Average</th>
                    <th scope="col">Three or below</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byStaff.map((row) => (
                    <tr key={row.staffId}>
                      <th scope="row">{row.staffName}</th>
                      <td>{row.responses}</td>
                      <td>{row.averageRating} / 5</td>
                      <td>{row.detractors > 0 ? <Badge tone="warning">{row.detractors}</Badge> : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </CardBody>
        </Card>
      ) : null}

      {data.recentComments.length > 0 ? (
        <Card>
          <CardHeader title="What people said" subtitle="Most recent first" />
          <CardBody>
            <ul className={s.comments}>
              {data.recentComments.map((row) => (
                <li key={row.ticketId} className={s.comment}>
                  <div className={s.commentHead}>
                    <span className={s.commentTicket}>{row.ticketNumber}</span>
                    <span className={s.commentStars}>{'★'.repeat(row.rating)}
                      <span className={s.muted}>{'★'.repeat(5 - row.rating)}</span>
                    </span>
                  </div>
                  <p className={s.commentSubject}>{row.subject}</p>
                  <p className={s.commentBody}>{row.comment}</p>
                </li>
              ))}
            </ul>
          </CardBody>
        </Card>
      ) : null}
    </>
  );
}

/**
 * Named requesters, ranked by how they use the desk.
 *
 * <p>Every count is shown against the desk's own average, because there is no universal
 * number for "too many tickets" — eleven means nothing until you know the average is
 * three. The averages are what make the rows readable at all.</p>
 *
 * <p>Framed as a prompt for a conversation rather than a verdict, and the page says so.
 * A high figure usually means somebody has been handed a system that keeps failing them,
 * or that nobody explained what the impact scale means.</p>
 */
function CustomerBehaviourReport({ data }) {
  if (data.rows.length === 0) {
    return (
      <Card>
        <CardBody>
          <EmptyState
            icon="◍"
            title="Nobody raised a ticket in this period"
            message="Widen the period, or come back once the desk has been used."
          />
        </CardBody>
      </Card>
    );
  }

  const average = data.averageTicketsPerRequester;

  return (
    <>
      <div className={s.summary}>
        <Card>
          <CardBody className={s.summaryBody}>
            <span className={s.summaryValue}>{average}</span>
            <span className={s.summaryLabel}>Tickets per requester, on average</span>
            <span className={s.summaryHint}>
              {data.requesters} {data.requesters === 1 ? 'person' : 'people'} raised{' '}
              {data.ticketsRaised.toLocaleString()} tickets. Read every row against this
              number: a count on its own says nothing about whether it is unusual.
            </span>
          </CardBody>
        </Card>
      </div>

      <Card>
        <CardHeader
          title="By requester"
          subtitle="Busiest first. A prompt for a conversation, not a verdict."
        />
        <CardBody>
          <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th scope="col">Requester</th>
                  <th scope="col">Raised</th>
                  <th scope="col">Over-claimed</th>
                  <th scope="col">Reopened</th>
                  <th scope="col">Cancelled</th>
                  <th scope="col">High or Critical</th>
                  <th scope="col">Awaiting them</th>
                  <th scope="col">Confirms in</th>
                  <th scope="col">Rates us</th>
                </tr>
              </thead>
              <tbody>
                {data.rows.map((row) => (
                  <tr key={row.requesterId}>
                    <th scope="row">
                      {row.requesterName}
                      <span className={s.subtle}>{row.requesterEmail}</span>
                    </th>
                    <td>
                      {/* Spaced deliberately: the count and the badge ran together as
                          "132× average" when adjacent, which reads as one number. */}
                      <span className={s.countWithBadge}>
                        <strong>{row.ticketsRaised}</strong>
                        {row.ticketsRaised >= average * 2 ? (
                          <Badge tone="warning">
                            {Math.round(row.ticketsRaised / average)}× average
                          </Badge>
                        ) : null}
                      </span>
                    </td>
                    <td className={row.overClaimedSeverity > 0 ? undefined : s.muted}>
                      {row.overClaimedSeverity || '—'}
                    </td>
                    <td className={row.reopened > 0 ? undefined : s.muted}>{row.reopened || '—'}</td>
                    <td className={row.cancelled > 0 ? undefined : s.muted}>{row.cancelled || '—'}</td>
                    <td>{row.highOrCritical}</td>
                    <td className={row.awaitingTheirConfirmation > 0 ? undefined : s.muted}>
                      {row.awaitingTheirConfirmation || '—'}
                    </td>
                    <td className={s.muted}>
                      {row.averageConfirmationHours === null
                        ? '—'
                        : `${row.averageConfirmationHours} h`}
                    </td>
                    <td className={s.muted}>
                      {row.averageSatisfaction === null ? '—' : `${row.averageSatisfaction} / 5`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <p className={s.footnote}>
            <strong>Over-claimed</strong> counts tickets where somebody asked for more
            severity than they may declare and the cap reduced it.{' '}
            <strong>Reopened</strong> counts tickets, not reopenings — one ticket reopened
            four times is one unresolved problem, not four.{' '}
            <strong>Awaiting them</strong> is work finished and waiting on the requester
            to confirm, which is often why a ticket looks stuck.
          </p>
        </CardBody>
      </Card>
    </>
  );
}

export function ReportsPage() {
  const { can } = useAuth();
  const toast = useToast();

  const [report, setReport] = useState('sla-compliance');
  const [days, setDays] = useState(30);
  const [exporting, setExporting] = useState(false);

  const filters = { fromUtc: daysAgoIso(days) };

  const queries = {
    'sla-compliance': {
      key: reportKeys.sla(filters),
      fn: () => reportsService.slaCompliance(filters),
      render: (data) => <SlaComplianceReport data={data} />,
    },
    'volume-trend': {
      key: reportKeys.volume(filters),
      fn: () => reportsService.volumeTrend(filters),
      render: (data) => <VolumeTrendReport data={data} />,
    },
    'staff-performance': {
      key: reportKeys.staff(filters),
      fn: () => reportsService.staffPerformance(filters),
      render: (data) => <StaffPerformanceReport data={data} />,
    },
    satisfaction: {
      key: reportKeys.satisfaction(filters),
      fn: () => reportsService.satisfaction(filters),
      render: (data) => <SatisfactionReport data={data} />,
    },
    'customer-behaviour': {
      key: reportKeys.customerBehaviour(filters),
      fn: () => reportsService.customerBehaviour(filters),
      render: (data) => <CustomerBehaviourReport data={data} />,
    },
  };

  const active = queries[report];

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: active.key,
    queryFn: active.fn,
  });

  async function exportCsv(which) {
    setExporting(true);

    try {
      const { blob, fileName } = await reportsService.export(which, filters);
      saveBlob(blob, fileName);
      toast.success('Export downloaded', fileName);
    } catch (failure) {
      toast.error('Could not export', failure.detail ?? failure.message);
    } finally {
      setExporting(false);
    }
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Reports</h2>
          <p className={s.subtitle}>
            {data?.period
              ? <>Covering <strong>{SCOPE_LABEL[data.period.scope] ?? 'what you can see'}</strong>
                  {' '}— {data.period.ticketsInScope.toLocaleString()} tickets over{' '}
                  {data.period.days} days.</>
              : 'Figures cover the tickets your account is entitled to see.'}
          </p>
        </div>

        <div className={s.headerControls}>
          <label className={s.control}>
            <span className="sr-only">Reporting period</span>
            <select
              className={s.select}
              value={days}
              onChange={(event) => setDays(Number(event.target.value))}
            >
              {PERIODS.map((period) => (
                <option key={period.days} value={period.days}>{period.label}</option>
              ))}
            </select>
          </label>

          {can('reports.export') ? (
            <>
              <Button size="sm" variant="secondary" loading={exporting}
                      onClick={() => exportCsv(report)}>
                Export this report
              </Button>
              <Button size="sm" variant="ghost" loading={exporting}
                      onClick={() => exportCsv('tickets')}>
                Export raw tickets
              </Button>
            </>
          ) : null}
        </div>
      </header>

      <div className={s.tabs} role="tablist" aria-label="Reports">
        {REPORTS.filter((item) => !item.permission || can(item.permission)).map((item) => (
          <button
            key={item.key}
            type="button"
            role="tab"
            aria-selected={report === item.key}
            className={`${s.tab} ${report === item.key ? s.tabActive : ''}`}
            onClick={() => setReport(item.key)}
          >
            {item.label}
          </button>
        ))}
      </div>

      {isPending ? (
        <div className={s.stack}>
          <Skeleton height={120} />
          <Skeleton height={240} />
        </div>
      ) : isError ? (
        <ErrorState error={error} onRetry={refetch} title="Could not load this report" />
      ) : (
        <div className={s.stack}>{active.render(data)}</div>
      )}
    </>
  );
}
