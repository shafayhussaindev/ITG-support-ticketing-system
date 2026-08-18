import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { api } from '@/services/apiClient';
import { ticketKeys, ticketService } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import {
  Badge, Button, Card, CardBody, CardHeader, ConfirmDialog,
  EmptyState, ErrorState, LoadingState,
} from '@/components/ui';
import { PriorityBadge, StatusBadge, TypeBadge, humanizeStatus } from '@/components/ui/TicketBadges';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import { Conversation } from './Conversation';
import { SlaPanel } from './SlaPanel';
import { SatisfactionPanel } from './SatisfactionPanel';
import s from './TicketDetailPage.module.css';

export function TicketDetailPage() {
  const { id } = useParams();
  const { can, user } = useAuth();
  const toast = useToast();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [dialog, setDialog] = useState(null);
  const [resolution, setResolution] = useState({ summary: '', rootCause: '' });
  const [reopenReason, setReopenReason] = useState('');

  const ticketQuery = useQuery({
    queryKey: ticketKeys.detail(id),
    queryFn: () => ticketService.get(id),
  });

  const timelineQuery = useQuery({
    queryKey: ticketKeys.timeline(id),
    queryFn: () => ticketService.timeline(id),
    enabled: Boolean(ticketQuery.data),
  });

  const agentsQuery = useQuery({
    queryKey: ['catalog', 'agents'],
    queryFn: () => api.get('/agents'),
    // Only leads can assign, so only fetch the picker for them.
    enabled: can('ticket.assign'),
    staleTime: 60_000,
  });

  function refreshAll() {
    queryClient.invalidateQueries({ queryKey: ticketKeys.detail(id) });
    queryClient.invalidateQueries({ queryKey: ticketKeys.timeline(id) });
    queryClient.invalidateQueries({ queryKey: ticketKeys.comments(id) });
    queryClient.invalidateQueries({ queryKey: ['tickets', 'list'] });
  }

  /** Wraps a ticket command so every action shares the same success and error handling. */
  function useTicketAction(fn, successMessage) {
    return useMutation({
      mutationFn: fn,
      onSuccess: () => {
        refreshAll();
        toast.success(successMessage);
        setDialog(null);
      },
      onError: (error) => {
        // The server's message is shown verbatim: it explains precisely which rule
        // blocked the action, which is more useful than a generic failure notice.
        toast.error('That did not work', error.detail ?? 'Please try again.');
      },
    });
  }

  const accept = useTicketAction(() => ticketService.accept(id), 'Ticket accepted');
  const assign = useTicketAction((agentId) => ticketService.assign(id, { agentId }), 'Ticket assigned');
  const resolve = useTicketAction(
    () => ticketService.resolve(id, {
      resolutionSummary: resolution.summary,
      rootCause: resolution.rootCause || null,
    }),
    'Resolution sent to the requester',
  );
  const close = useTicketAction(() => ticketService.close(id, {}), 'Ticket closed');
  const reopen = useTicketAction(
    () => ticketService.reopen(id, { reason: reopenReason }),
    'Ticket reopened',
  );

  if (ticketQuery.isPending) {
    return <LoadingState label="Loading ticket" />;
  }

  if (ticketQuery.isError) {
    const notFound = ticketQuery.error?.status === 404;

    return notFound ? (
      <Card>
        <CardBody>
          <EmptyState
            icon="⌕"
            title="Ticket not found"
            message="It may have been archived, or it may belong to someone whose tickets you cannot see."
            actions={<Button size="sm" onClick={() => navigate('/tickets')}>Back to tickets</Button>}
          />
        </CardBody>
      </Card>
    ) : (
      <ErrorState error={ticketQuery.error} onRetry={ticketQuery.refetch} title="Could not load the ticket" />
    );
  }

  const ticket = ticketQuery.data;
  const isRequester = ticket.requesterId === user?.id;
  const isAssignee = ticket.assignedAgentId === user?.id;
  const canResolve = can('ticket.resolve') && !['Resolved', 'Closed', 'Cancelled'].includes(ticket.status);
  const canAccept = can('ticket.accept') && ['New', 'Assigned', 'Reopened'].includes(ticket.status);
  const canConfirm = isRequester && ticket.status === 'Resolved';
  const canReopen = ['Resolved', 'Closed'].includes(ticket.status)
    && (can('ticket.reopen') || (isRequester && can('ticket.confirm_resolution')));

  return (
    <>
      <header className={s.header}>
        <div className={s.headerMain}>
          <button type="button" className={s.back} onClick={() => navigate('/tickets')}>
            ← All tickets
          </button>

          <div className={s.titleRow}>
            <span className={s.number}>{ticket.ticketNumber}</span>
            <StatusBadge status={ticket.status} />
            <PriorityBadge priority={ticket.priority} />
            <TypeBadge type={ticket.type} />
          </div>

          <h2 className={s.subject}>{ticket.subject}</h2>

          <p className={s.byline}>
            Raised by <strong>{ticket.requesterName}</strong>{' '}
            <time dateTime={ticket.createdAtUtc} title={formatDateTime(ticket.createdAtUtc)}>
              {formatRelative(ticket.createdAtUtc)}
            </time>
            {ticket.reopenCount > 0 ? (
              <>
                {' · '}
                <Badge tone="warning">
                  Reopened {ticket.reopenCount} time{ticket.reopenCount === 1 ? '' : 's'}
                </Badge>
              </>
            ) : null}
          </p>
        </div>

        <div className={s.actions}>
          {canAccept ? (
            <Button size="sm" onClick={() => accept.mutate()} loading={accept.isPending}>
              Accept
            </Button>
          ) : null}

          {canResolve ? (
            <Button size="sm" variant="secondary" onClick={() => setDialog('resolve')}>
              Resolve
            </Button>
          ) : null}

          {canConfirm ? (
            <Button size="sm" onClick={() => setDialog('confirm')}>
              Confirm resolution
            </Button>
          ) : null}

          {canReopen ? (
            <Button size="sm" variant="secondary" onClick={() => setDialog('reopen')}>
              {isRequester ? 'This is not fixed' : 'Reopen'}
            </Button>
          ) : null}
        </div>
      </header>

      <div className={s.grid}>
        <div className={s.main}>
          <Card>
            <CardHeader title="Description" />
            <CardBody>
              <p className={s.description}>{ticket.description}</p>
            </CardBody>
          </Card>

          {ticket.resolutionSummary ? (
            <Card className={s.resolutionCard}>
              <CardHeader
                title="Proposed resolution"
                subtitle={ticket.resolvedByName ? `By ${ticket.resolvedByName}` : undefined}
              />
              <CardBody>
                <p className={s.description}>{ticket.resolutionSummary}</p>
                {ticket.rootCause ? (
                  <>
                    <p className={s.subhead}>Root cause</p>
                    <p className={s.description}>{ticket.rootCause}</p>
                  </>
                ) : null}
              </CardBody>
            </Card>
          ) : null}

          <Conversation ticketId={id} ticketStatus={ticket.status} />
        </div>

        <aside className={s.side}>
          <SatisfactionPanel
            ticketId={id}
            ticketStatus={ticket.status}
            isRequester={isRequester}
          />

          <SlaPanel ticketId={id} />

          <Card>
            <CardHeader title="Details" />
            <CardBody>
              <dl className={s.dl}>
                <dt>Status</dt>
                <dd><StatusBadge status={ticket.status} /></dd>

                <dt>Priority</dt>
                <dd>
                  <PriorityBadge priority={ticket.priority} />
                  {ticket.priorityDecisionSource === 'Human' ? (
                    <p className={s.note}>
                      Overridden from <strong>{ticket.suggestedPriority}</strong>.{' '}
                      {ticket.priorityOverrideReason}
                    </p>
                  ) : (
                    <p className={s.note}>
                      Calculated from {ticket.impact} impact and {ticket.urgency} urgency.
                    </p>
                  )}
                </dd>

                <dt>Assigned to</dt>
                <dd>
                  {ticket.assignedAgentName ?? <span className={s.unassigned}>Unassigned</span>}
                  {ticket.assignedTeamName ? (
                    <p className={s.note}>{ticket.assignedTeamName}</p>
                  ) : null}
                </dd>

                <dt>Category</dt>
                <dd>
                  {ticket.categoryName ?? '—'}
                  {ticket.subcategoryName ? <p className={s.note}>{ticket.subcategoryName}</p> : null}
                </dd>

                {ticket.applicationName ? (
                  <>
                    <dt>Application</dt>
                    <dd>
                      {ticket.applicationName}
                      {ticket.applicationModuleName ? (
                        <p className={s.note}>{ticket.applicationModuleName}</p>
                      ) : null}
                    </dd>
                  </>
                ) : null}

                <dt>Department</dt>
                <dd>{ticket.departmentName ?? '—'}</dd>

                <dt>First response</dt>
                <dd>
                  {ticket.firstRespondedAtUtc
                    ? formatRelative(ticket.firstRespondedAtUtc)
                    : <span className={s.pending}>Not yet</span>}
                </dd>

                {ticket.resolvedAtUtc ? (
                  <>
                    <dt>Resolved</dt>
                    <dd title={formatDateTime(ticket.resolvedAtUtc)}>
                      {formatRelative(ticket.resolvedAtUtc)}
                    </dd>
                  </>
                ) : null}
              </dl>
            </CardBody>
          </Card>

          {can('ticket.assign') ? (
            <Card>
              <CardHeader title="Assignment" subtitle="Open ticket counts shown to help spread load" />
              <CardBody>
                <label className="sr-only" htmlFor="assign-agent">Assign to agent</label>
                <select
                  id="assign-agent"
                  className={s.select}
                  value={ticket.assignedAgentId ?? ''}
                  disabled={assign.isPending}
                  onChange={(e) => e.target.value && assign.mutate(e.target.value)}
                >
                  <option value="">Unassigned</option>
                  {(agentsQuery.data ?? []).map((agent) => (
                    <option key={agent.id} value={agent.id}>
                      {agent.fullName} — {agent.openTicketCount} open
                      {agent.isAvailable ? '' : ' (unavailable)'}
                    </option>
                  ))}
                </select>
              </CardBody>
            </Card>
          ) : null}

          <Card>
            <CardHeader title="History" subtitle="Rebuilt from the audit trail" />
            <CardBody>
              {timelineQuery.isPending ? (
                <p className={s.note}>Loading…</p>
              ) : timelineQuery.isError ? (
                <p className={s.note}>Could not load the history.</p>
              ) : (
                <ol className={s.timeline}>
                  {timelineQuery.data.map((entry, index) => (
                    <li key={`${entry.occurredAtUtc}-${index}`} className={s.timelineItem}>
                      <span className={s.timelineDot} aria-hidden="true" />
                      <div>
                        <p className={s.timelineSummary}>{entry.summary}</p>
                        <p className={s.timelineMeta}>
                          {entry.actorName}
                          {entry.decisionSource && entry.decisionSource !== 'Human'
                            ? ` · ${entry.decisionSource}`
                            : ''}
                          {' · '}
                          <time dateTime={entry.occurredAtUtc} title={formatDateTime(entry.occurredAtUtc)}>
                            {formatRelative(entry.occurredAtUtc)}
                          </time>
                        </p>
                        {entry.detail ? <p className={s.timelineDetail}>{entry.detail}</p> : null}
                      </div>
                    </li>
                  ))}
                </ol>
              )}
            </CardBody>
          </Card>
        </aside>
      </div>

      {/* ---- resolve ---- */}
      {dialog === 'resolve' ? (
        <div className={s.backdrop} onMouseDown={(e) => e.target === e.currentTarget && setDialog(null)}>
          <div className={s.modal} role="dialog" aria-modal="true" aria-labelledby="resolve-title">
            <h3 id="resolve-title" className={s.modalTitle}>Resolve this ticket</h3>
            <p className={s.modalNote}>
              The summary is sent to {ticket.requesterName}, who confirms or rejects it. Write it
              for them, not for the next engineer.
            </p>

            <label className={s.label} htmlFor="resolution-summary">
              Resolution summary<span className={s.required}>*</span>
            </label>
            <textarea
              id="resolution-summary"
              className={s.textarea}
              rows={5}
              value={resolution.summary}
              onChange={(e) => setResolution((r) => ({ ...r, summary: e.target.value }))}
              placeholder="What was wrong and what you did about it."
            />

            <label className={s.label} htmlFor="root-cause">Root cause (optional)</label>
            <textarea
              id="root-cause"
              className={s.textarea}
              rows={3}
              value={resolution.rootCause}
              onChange={(e) => setResolution((r) => ({ ...r, rootCause: e.target.value }))}
              placeholder="Why it happened, for future reporting."
            />

            <div className={s.modalActions}>
              <Button size="sm" variant="secondary" onClick={() => setDialog(null)}>Cancel</Button>
              <Button
                size="sm"
                loading={resolve.isPending}
                disabled={resolution.summary.trim().length < 3}
                onClick={() => resolve.mutate()}
              >
                Send resolution
              </Button>
            </div>
          </div>
        </div>
      ) : null}

      {/* ---- reopen ---- */}
      {dialog === 'reopen' ? (
        <div className={s.backdrop} onMouseDown={(e) => e.target === e.currentTarget && setDialog(null)}>
          <div className={s.modal} role="dialog" aria-modal="true" aria-labelledby="reopen-title">
            <h3 id="reopen-title" className={s.modalTitle}>Reopen this ticket</h3>
            <p className={s.modalNote}>
              This reopens the same ticket rather than starting a new one, so the history stays
              in one place.
            </p>

            <label className={s.label} htmlFor="reopen-reason">
              What is still wrong?<span className={s.required}>*</span>
            </label>
            <textarea
              id="reopen-reason"
              className={s.textarea}
              rows={4}
              value={reopenReason}
              onChange={(e) => setReopenReason(e.target.value)}
              placeholder="Describe what is still happening."
            />

            <div className={s.modalActions}>
              <Button size="sm" variant="secondary" onClick={() => setDialog(null)}>Cancel</Button>
              <Button
                size="sm"
                loading={reopen.isPending}
                disabled={reopenReason.trim().length < 3}
                onClick={() => reopen.mutate()}
              >
                Reopen
              </Button>
            </div>
          </div>
        </div>
      ) : null}

      <ConfirmDialog
        open={dialog === 'confirm'}
        title="Confirm this is fixed?"
        message={`Ticket ${ticket.ticketNumber} will be closed. You can still reopen it later if the problem returns.`}
        confirmLabel="Yes, close it"
        loading={close.isPending}
        onConfirm={() => close.mutate()}
        onCancel={() => setDialog(null)}
      />
    </>
  );
}
