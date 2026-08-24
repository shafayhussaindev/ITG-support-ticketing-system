import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { slaKeys, slaService } from '@/services/slaService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import { PriorityBadge } from '@/components/ui/TicketBadges';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import s from './EscalationsPage.module.css';

const STATE_TONE = {
  Raised: 'danger',
  Notified: 'warning',
  Acknowledged: 'info',
  Resolved: 'success',
  Cancelled: 'neutral',
};

export function EscalationsPage() {
  const { can } = useAuth();
  const toast = useToast();
  const queryClient = useQueryClient();

  const [openOnly, setOpenOnly] = useState(true);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: slaKeys.escalations(openOnly),
    queryFn: () => slaService.escalations(openOnly),
    refetchInterval: 60_000,
  });

  // The oversight view. An administrator opening this screen is asking whether the desk
  // is keeping up, which a flat list of two hundred rows does not answer.
  const { data: summary } = useQuery({
    queryKey: slaKeys.escalationSummary(),
    queryFn: slaService.escalationSummary,
    refetchInterval: 60_000,
  });

  const mayAcknowledge = can('escalation.acknowledge');

  const acknowledge = useMutation({
    mutationFn: (id) => slaService.acknowledgeEscalation(id),
    onSuccess: () => {
      // Both: the row changes state and the counts above it move with it.
      queryClient.invalidateQueries({ queryKey: ['sla', 'escalations'] });
      toast.success('Escalation acknowledged');
    },
    onError: (err) => toast.error('Could not acknowledge that', err.detail),
  });

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Escalations</h2>
          <p className={s.subtitle}>
            Raised automatically by the background SLA sweep as a ticket consumes its
            resolution budget. Only tickets you can already see are listed.
          </p>
        </div>

        <label className={s.toggle}>
          <input
            type="checkbox"
            checked={openOnly}
            onChange={(event) => setOpenOnly(event.target.checked)}
          />
          Unacknowledged only
        </label>
      </header>

      {summary ? (
        <div className={s.summary}>
          <div className={s.stat}>
            <span className={s.statValue} data-tone={summary.unacknowledged > 0 ? 'danger' : 'calm'}>
              {summary.unacknowledged}
            </span>
            <span className={s.statLabel}>waiting for someone</span>
          </div>

          <div className={s.stat}>
            <span className={s.statValue}>{summary.acknowledged}</span>
            <span className={s.statLabel}>owned, still open</span>
          </div>

          <div className={s.stat}>
            <span className={s.statValue} data-tone={summary.beyondFirstLevel > 0 ? 'warn' : 'calm'}>
              {summary.beyondFirstLevel}
            </span>
            <span className={s.statLabel}>past the first rung</span>
          </div>

          <div className={s.stat}>
            {/* The number that says whether anyone is actually watching. An hours
                figure here is worth more than a count, because a queue of three that
                nobody has touched in two days is worse than a queue of twenty. */}
            <span className={s.statValue} data-tone={
              summary.oldestUnacknowledgedHours === null ? 'calm'
                : summary.oldestUnacknowledgedHours >= 24 ? 'danger'
                  : summary.oldestUnacknowledgedHours >= 4 ? 'warn' : 'calm'
            }>
              {summary.oldestUnacknowledgedHours === null
                ? '—'
                : `${summary.oldestUnacknowledgedHours} h`}
            </span>
            <span className={s.statLabel}>oldest unacknowledged</span>
          </div>

          <div className={s.stat}>
            <span className={s.statValue}>{summary.settledLastWeek}</span>
            <span className={s.statLabel}>settled this week</span>
          </div>
        </div>
      ) : null}

      <Card className={s.card}>
        {isPending ? (
          <div className={s.skeletons}>
            {Array.from({ length: 4 }, (_, i) => <Skeleton key={i} height={40} />)}
          </div>
        ) : isError ? (
          <ErrorState error={error} onRetry={refetch} title="Could not load escalations" />
        ) : data.length === 0 ? (
          <EmptyState
            icon="✓"
            title={openOnly ? 'Nothing is escalated' : 'No escalations recorded'}
            message={
              openOnly
                ? 'Every ticket you can see is inside its SLA budget, or has already been acknowledged.'
                : 'No ticket has crossed an escalation threshold yet.'
            }
            actions={
              openOnly ? (
                <Button size="sm" variant="secondary" onClick={() => setOpenOnly(false)}>
                  Show the full history
                </Button>
              ) : null
            }
          />
        ) : (
          <div className={s.tableWrap}>
            <table className={s.table}>
              <caption className="sr-only">Escalated tickets, {data.length} in total</caption>
              <thead>
                <tr>
                  <th scope="col">Ticket</th>
                  <th scope="col">Level</th>
                  <th scope="col">Trigger</th>
                  <th scope="col">Priority</th>
                  <th scope="col">Notified</th>
                  <th scope="col">State</th>
                  <th scope="col">Raised</th>
                  {mayAcknowledge ? <th scope="col">Action</th> : null}
                </tr>
              </thead>
              <tbody>
                {data.map((escalation) => (
                  <tr key={escalation.id}>
                    <td>
                      <Link className={s.number} to={`/tickets/${escalation.ticketId}`}>
                        {escalation.ticketNumber}
                      </Link>
                      <div className={s.subject}>{escalation.ticketSubject}</div>
                      {escalation.reason ? (
                        <div className={s.reason}>{escalation.reason}</div>
                      ) : null}
                    </td>
                    <td>
                      <span className={s.level}>L{escalation.level}</span>
                      <div className={s.threshold}>at {escalation.thresholdPercent}%</div>
                    </td>
                    <td className={s.muted}>
                      {escalation.trigger.replace(/([a-z])([A-Z])/g, '$1 $2')}
                    </td>
                    <td><PriorityBadge priority={escalation.priority} /></td>
                    <td className={s.muted}>
                      {escalation.recipientName ?? (
                        // Recorded rather than hidden: an escalation that reached
                        // nobody is the most important kind to surface.
                        <span className={s.nobody}>Nobody matched</span>
                      )}
                    </td>
                    <td>
                      <Badge tone={STATE_TONE[escalation.state] ?? 'neutral'}>
                        {escalation.state}
                      </Badge>
                    </td>
                    <td className={s.muted} title={formatDateTime(escalation.raisedAtUtc)}>
                      {formatRelative(escalation.raisedAtUtc)}
                    </td>

                    {mayAcknowledge ? (
                      <td>
                        {escalation.state === 'Raised' || escalation.state === 'Notified' ? (
                          <Button
                            size="sm"
                            variant="secondary"
                            disabled={acknowledge.isPending}
                            onClick={() => acknowledge.mutate(escalation.id)}
                          >
                            Acknowledge
                          </Button>
                        ) : escalation.acknowledgedByName ? (
                          // Who took it on, not just that somebody did. On a shared
                          // queue the name is the whole point.
                          <span className={s.muted} title={formatDateTime(escalation.acknowledgedAtUtc)}>
                            {escalation.acknowledgedByName}
                          </span>
                        ) : (
                          <span className={s.muted}>—</span>
                        )}
                      </td>
                    ) : null}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}
