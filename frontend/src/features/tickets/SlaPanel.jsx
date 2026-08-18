import { useQuery } from '@tanstack/react-query';
import { Badge, Card, CardBody, CardHeader, Skeleton } from '@/components/ui';
import { formatSlaRemaining, slaKeys, slaService, slaTone } from '@/services/slaService';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import s from './SlaPanel.module.css';

const STATE_LABELS = {
  NotStarted: 'Not started',
  Running: 'Running',
  Paused: 'Paused',
  Met: 'Met',
  Breached: 'Breached',
  Cancelled: 'Cancelled',
};

export function SlaPanel({ ticketId }) {
  const { data: sla, isPending, isError } = useQuery({
    queryKey: slaKeys.ticket(ticketId),
    queryFn: () => slaService.forTicket(ticketId),
    // The countdown is time-sensitive, so refresh it while the tab is open rather
    // than letting a stale figure sit on screen for minutes.
    refetchInterval: 60_000,
  });

  if (isPending) {
    return (
      <Card>
        <CardHeader title="Service level" />
        <CardBody>
          <Skeleton height={14} />
          <div style={{ marginTop: 10 }}><Skeleton height={8} /></div>
        </CardBody>
      </Card>
    );
  }

  if (isError) {
    return null;
  }

  if (!sla) {
    // A ticket outside every policy has no promise attached. Saying so is more
    // honest than drawing an empty progress bar that implies one exists.
    return (
      <Card>
        <CardHeader title="Service level" />
        <CardBody>
          <p className={s.none}>
            No SLA policy matched this ticket, so no response or resolution target applies.
          </p>
        </CardBody>
      </Card>
    );
  }

  const tone = slaTone(sla);
  const percent = Math.min(sla.resolutionConsumedPercent, 100);
  const overdue = sla.resolutionConsumedPercent > 100;

  return (
    <Card>
      <CardHeader
        title="Service level"
        subtitle={sla.policyName ?? undefined}
        actions={
          sla.isPaused ? (
            <Badge tone="info" dot>Paused</Badge>
          ) : (
            <Badge tone={tone} dot>{STATE_LABELS[sla.resolutionState] ?? sla.resolutionState}</Badge>
          )
        }
      />

      <CardBody>
        <div className={s.headline}>
          <span className={`${s.remaining} ${s[tone]}`}>
            {sla.resolutionState === 'Met'
              ? 'Resolved within target'
              : sla.resolutionState === 'Cancelled'
                ? 'Clock cancelled'
                : formatSlaRemaining(sla.minutesToResolutionDue)}
          </span>
          <span className={s.percent}>{Math.round(sla.resolutionConsumedPercent)}% used</span>
        </div>

        {/* Progress conveys urgency by width and colour, but the figures above carry
            the same information as text for anyone who cannot distinguish the hues. */}
        <div
          className={s.track}
          role="progressbar"
          aria-valuenow={Math.round(sla.resolutionConsumedPercent)}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-label="Resolution budget consumed"
        >
          <span className={`${s.fill} ${s[`fill_${tone}`]}`} style={{ width: `${percent}%` }} />
          <span
            className={s.threshold}
            style={{ left: `${Math.min(sla.warningThresholdPercent, 100)}%` }}
            title={`Warning at ${sla.warningThresholdPercent}%`}
          />
        </div>

        {sla.isPaused ? (
          <p className={s.pausedNote}>
            Paused since {formatRelative(sla.pausedAtUtc)} while waiting on someone outside
            support. The deadline moves out by the same amount when work resumes.
          </p>
        ) : null}

        <dl className={s.dl}>
          <dt>First response</dt>
          <dd>
            {sla.firstRespondedAtUtc ? (
              <>
                <Badge tone={sla.responseState === 'Met' ? 'success' : 'danger'}>
                  {STATE_LABELS[sla.responseState] ?? sla.responseState}
                </Badge>
                <span className={s.sub}>{formatRelative(sla.firstRespondedAtUtc)}</span>
              </>
            ) : (
              <>
                <span title={formatDateTime(sla.responseDueAtUtc)}>
                  due {formatRelative(sla.responseDueAtUtc)}
                </span>
                <span className={s.sub}>{sla.responseMinutes} business minutes</span>
              </>
            )}
          </dd>

          <dt>Resolution</dt>
          <dd>
            <span title={formatDateTime(sla.resolutionDueAtUtc)}>
              due {formatRelative(sla.resolutionDueAtUtc)}
            </span>
            <span className={s.sub}>{sla.resolutionMinutes} business minutes</span>
          </dd>

          {sla.totalPausedMinutes > 0 ? (
            <>
              <dt>Paused for</dt>
              <dd>{sla.totalPausedMinutes} minutes total</dd>
            </>
          ) : null}

          {sla.highestEscalationLevel > 0 ? (
            <>
              <dt>Escalated</dt>
              <dd><Badge tone="danger">Level {sla.highestEscalationLevel}</Badge></dd>
            </>
          ) : null}
        </dl>

        {overdue && sla.resolutionState === 'Breached' ? (
          <p className={s.breach}>
            This ticket passed its resolution target. It stays on the escalation ladder
            until it is resolved.
          </p>
        ) : null}

        {sla.events.length > 0 ? (
          <details className={s.events}>
            <summary>SLA history ({sla.events.length})</summary>
            <ol className={s.eventList}>
              {sla.events.map((event, index) => (
                <li key={`${event.occurredAtUtc}-${index}`}>
                  <span className={s.eventType}>{event.eventType}</span>
                  <span className={s.eventTime} title={formatDateTime(event.occurredAtUtc)}>
                    {formatRelative(event.occurredAtUtc)}
                  </span>
                  {event.detail ? <span className={s.eventDetail}>{event.detail}</span> : null}
                </li>
              ))}
            </ol>
          </details>
        ) : null}
      </CardBody>
    </Card>
  );
}
