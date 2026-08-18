import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Bar, BarChart, CartesianGrid, Cell, Legend, Line, LineChart,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { formatMinutes, reportingKeys, reportingService } from '@/services/reportingService';
import { useAuth } from '@/contexts/AuthContext';
import { Badge, Card, CardBody, CardHeader, ErrorState, Skeleton } from '@/components/ui';
import s from './DashboardPage.module.css';

const PRIORITY_COLOR = {
  Critical: 'var(--c-priority-critical)',
  High: 'var(--c-priority-high)',
  Medium: 'var(--c-priority-medium)',
  Low: 'var(--c-priority-low)',
};

const SCOPE_LABEL = {
  Own: 'your own tickets',
  Assigned: 'tickets assigned to you',
  Team: 'your teams',
  Department: 'your department',
  Organization: 'your organization',
  All: 'every organization',
};

const chartAxis = { fill: 'var(--c-text-3)', fontSize: 11 };

const tooltipStyle = {
  background: 'var(--c-surface)',
  border: '1px solid var(--c-border)',
  borderRadius: 6,
  fontSize: 12,
  color: 'var(--c-text)',
};

function Kpi({ label, value, hint, tone, onClick }) {
  const clickable = typeof onClick === 'function';

  return (
    <Card className={s.kpiCard}>
      <CardBody className={s.kpiBody}>
        {clickable ? (
          <button type="button" className={s.kpiButton} onClick={onClick}>
            <span className={`${s.kpiValue} ${tone ? s[tone] : ''}`}>{value}</span>
            <span className={s.kpiLabel}>{label}</span>
          </button>
        ) : (
          <>
            <span className={`${s.kpiValue} ${tone ? s[tone] : ''}`}>{value}</span>
            <span className={s.kpiLabel}>{label}</span>
          </>
        )}
        {hint ? <span className={s.kpiHint}>{hint}</span> : null}
      </CardBody>
    </Card>
  );
}

/** Horizontal bars, which read better than a pie for comparing category magnitudes. */
function SegmentChart({ data, onSelect, colorFor }) {
  if (data.length === 0) {
    return <p className={s.empty}>Nothing open in this view.</p>;
  }

  return (
    <div className={s.chart}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 16, bottom: 4, left: 8 }}>
          <CartesianGrid horizontal={false} stroke="var(--c-border)" />
          <XAxis type="number" allowDecimals={false} tick={chartAxis} stroke="var(--c-border-strong)" />
          <YAxis type="category" dataKey="label" width={104} tick={chartAxis} stroke="var(--c-border-strong)" />
          <Tooltip cursor={{ fill: 'var(--c-surface-3)' }} contentStyle={tooltipStyle} />
          <Bar dataKey="count" radius={[0, 3, 3, 0]} barSize={16} cursor="pointer">
            {data.map((entry) => (
              <Cell
                key={entry.label}
                fill={colorFor(entry.label) ?? 'var(--c-primary)'}
                onClick={() => onSelect(entry.drillDownQuery)}
              />
            ))}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}

export function DashboardPage() {
  const { user } = useAuth();
  const navigate = useNavigate();
  const [days, setDays] = useState(30);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: reportingKeys.dashboard(days),
    queryFn: () => reportingService.dashboard(days),
    refetchInterval: 120_000,
  });

  if (isPending) {
    return (
      <div className={s.kpiGrid}>
        {Array.from({ length: 8 }, (_, i) => (
          <Card key={i}><CardBody><Skeleton height={46} /></CardBody></Card>
        ))}
      </div>
    );
  }

  if (isError) {
    return <ErrorState error={error} onRetry={refetch} title="Could not load the dashboard" />;
  }

  const { kpis } = data;
  const peakLoad = Math.max(1, ...data.agentWorkload.map((a) => a.weightedScore));

  /** Sends a chart click to the ticket list using the same filter the segment counted. */
  function drill(query) {
    if (query) {
      navigate(`/tickets?${query}`);
    }
  }

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Good day, {user?.fullName?.split(' ')[0]}</h2>
          <p className={s.subtitle}>
            Figures cover <strong>{SCOPE_LABEL[data.scope] ?? 'the tickets you can see'}</strong>,
            over the last {days} days.
          </p>
        </div>

        <label className={s.rangeLabel}>
          <span className="sr-only">Reporting period</span>
          <select
            className={s.range}
            value={days}
            onChange={(event) => setDays(Number(event.target.value))}
          >
            <option value={7}>Last 7 days</option>
            <option value={30}>Last 30 days</option>
            <option value={90}>Last 90 days</option>
          </select>
        </label>
      </header>

      <div className={s.kpiGrid}>
        <Kpi label="Open" value={kpis.totalOpen} onClick={() => drill('openOnly=true')} />
        <Kpi label="New today" value={kpis.newToday} />
        <Kpi label="Critical" value={kpis.criticalOpen}
             tone={kpis.criticalOpen > 0 ? 'danger' : undefined}
             onClick={() => drill('priority=Critical&openOnly=true')} />
        <Kpi label="Unassigned" value={kpis.unassigned}
             tone={kpis.unassigned > 0 ? 'warning' : undefined}
             onClick={() => drill('unassigned=true&openOnly=true')} />
        <Kpi label="SLA breached" value={kpis.breached}
             tone={kpis.breached > 0 ? 'danger' : 'success'} />
        <Kpi label="SLA compliance"
             value={kpis.slaCompliancePercent === null ? '—' : `${kpis.slaCompliancePercent}%`}
             hint={kpis.slaCompliancePercent === null ? 'No clock has settled yet' : undefined} />
        <Kpi label="Avg first response" value={formatMinutes(kpis.averageFirstResponseMinutes)} />
        <Kpi label="Avg resolution" value={formatMinutes(kpis.averageResolutionMinutes)} />
        <Kpi label="Resolved today" value={kpis.resolvedToday} tone="success" />
        <Kpi label="Reopened" value={kpis.reopenedCount}
             tone={kpis.reopenedCount > 0 ? 'warning' : undefined} />
        <Kpi label="Satisfaction"
             value={kpis.averageSatisfaction === null ? '—' : `${kpis.averageSatisfaction} / 5`}
             hint={kpis.satisfactionResponses === 0
               ? 'Nobody has rated yet'
               : `${kpis.satisfactionResponses} response${kpis.satisfactionResponses === 1 ? '' : 's'}`} />
        <Kpi label="Approaching breach" value={kpis.approachingBreach}
             tone={kpis.approachingBreach > 0 ? 'warning' : undefined} />
      </div>

      <div className={s.chartGrid}>
        <Card className={s.wide}>
          <CardHeader title="Volume" subtitle="Raised against resolved, by day" />
          <CardBody>
            <div className={s.chart}>
              <ResponsiveContainer width="100%" height="100%">
                <LineChart data={data.volumeByDay} margin={{ top: 6, right: 12, bottom: 4, left: -18 }}>
                  <CartesianGrid stroke="var(--c-border)" vertical={false} />
                  <XAxis
                    dataKey="date"
                    tick={chartAxis}
                    stroke="var(--c-border-strong)"
                    tickFormatter={(value) =>
                      new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}
                    minTickGap={28}
                  />
                  <YAxis tick={chartAxis} stroke="var(--c-border-strong)" allowDecimals={false} />
                  <Tooltip
                    contentStyle={tooltipStyle}
                    labelFormatter={(value) => new Date(value).toLocaleDateString()}
                  />
                  <Legend wrapperStyle={{ fontSize: 12 }} />
                  <Line type="monotone" dataKey="raised" name="Raised"
                        stroke="var(--c-info)" strokeWidth={2} dot={false} />
                  <Line type="monotone" dataKey="resolved" name="Resolved"
                        stroke="var(--c-success)" strokeWidth={2} dot={false} />
                </LineChart>
              </ResponsiveContainer>
            </div>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Open by priority" subtitle="Click a bar to see those tickets" />
          <CardBody>
            <SegmentChart data={data.byPriority} onSelect={drill} colorFor={(l) => PRIORITY_COLOR[l]} />
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Open by status" subtitle="Click a bar to see those tickets" />
          <CardBody>
            <SegmentChart data={data.byStatus} onSelect={drill} colorFor={() => 'var(--c-primary)'} />
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Open by category" />
          <CardBody>
            <SegmentChart data={data.byCategory} onSelect={drill} colorFor={() => 'var(--c-info)'} />
          </CardBody>
        </Card>

        {data.agentWorkload.length > 0 ? (
          <Card className={s.wide}>
            <CardHeader
              title="Agent workload"
              subtitle="Weighted by priority, because ten questions are not ten outages"
            />
            <CardBody>
              <div className={s.tableWrap}>
                <table className={s.table}>
                  <thead>
                    <tr>
                      <th scope="col">Agent</th>
                      <th scope="col">Open</th>
                      <th scope="col">Critical</th>
                      <th scope="col">Breached</th>
                      <th scope="col">Weighted load</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.agentWorkload.map((agent) => (
                      <tr key={agent.agentId}>
                        <td>{agent.agentName}</td>
                        <td>{agent.openTickets}</td>
                        <td>{agent.criticalTickets > 0
                          ? <Badge tone="danger">{agent.criticalTickets}</Badge> : '—'}</td>
                        <td>{agent.breachedTickets > 0
                          ? <Badge tone="danger">{agent.breachedTickets}</Badge> : '—'}</td>
                        <td>
                          <div className={s.loadRow}>
                            <div className={s.loadBar}>
                              <span
                                className={s.loadFill}
                                style={{ width: `${(agent.weightedScore / peakLoad) * 100}%` }}
                              />
                            </div>
                            <span className={s.loadValue}>{agent.weightedScore}</span>
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </CardBody>
          </Card>
        ) : null}
      </div>
    </>
  );
}
