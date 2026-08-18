import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { slaKeys, slaService } from '@/services/slaService';
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
  const [openOnly, setOpenOnly] = useState(true);

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: slaKeys.escalations(openOnly),
    queryFn: () => slaService.escalations(openOnly),
    refetchInterval: 60_000,
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
