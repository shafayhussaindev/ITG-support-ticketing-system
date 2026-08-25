import { useQuery } from '@tanstack/react-query';
import { adminKeys, adminService } from '@/services/adminService';
import { useAuth } from '@/contexts/AuthContext';
import { Badge, Card, CardBody, CardHeader, EmptyState, ErrorState, LoadingState } from '@/components/ui';
import s from './admin.module.css';

/**
 * Who is holding how much work, right now.
 *
 * <p>Not the staff performance report, which answers how last month went. This answers
 * "who can take the next ticket", which is the question actually asked before
 * reassigning anything, and it is about this moment rather than a period.</p>
 *
 * <p>Everybody who can hold work is listed, including people holding none. Showing only
 * the busy would be precisely backwards: an empty queue is the most useful row on the
 * screen when looking for somewhere to put a ticket.</p>
 */
export function WorkloadPage() {
  const { can } = useAuth();

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: adminKeys.workload(),
    queryFn: adminService.workload,

    // Load moves as tickets are assigned and closed. A minute-old figure is fine for a
    // reassignment decision; a ten-minute-old one sends work to somebody already buried.
    refetchInterval: 60_000,
  });

  if (isPending) return <LoadingState label="Loading workload" />;
  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load the workload" />;

  const seesEveryone = can('users.manage');

  if (data.length === 0) {
    return (
      <Card>
        <CardBody>
          <EmptyState
            icon="◍"
            title={seesEveryone ? 'Nobody can be assigned work yet' : 'You do not lead a team yet'}
            message={seesEveryone
              ? 'Give somebody a role that can hold tickets and they will appear here.'
              : 'This shows the people on teams you lead. An administrator can make you a team lead.'}
          />
        </CardBody>
      </Card>
    );
  }

  const totalOpen = data.reduce((sum, row) => sum + row.openTickets, 0);
  const idle = data.filter((row) => row.openTickets === 0 && row.isAvailableForAssignment);

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Staff workload</h2>
          <p className={s.subtitle}>
            {totalOpen} open {totalOpen === 1 ? 'ticket' : 'tickets'} across {data.length}{' '}
            {data.length === 1 ? 'person' : 'people'}
            {idle.length > 0
              ? <> — <strong>{idle.length}</strong> {idle.length === 1 ? 'is' : 'are'} free</>
              : null}
            . {seesEveryone ? 'Everyone who can hold work.' : 'The teams you lead.'}
          </p>
        </div>
      </header>

      <Card>
        <CardHeader
          title="Busiest first"
          subtitle="Counted from tickets rather than a stored tally, so it cannot drift"
        />
        <CardBody>
          <div className={s.tableWrap}>
            <table className={s.table}>
              <thead>
                <tr>
                  <th scope="col">Person</th>
                  <th scope="col">Teams</th>
                  <th scope="col">Open</th>
                  <th scope="col">In progress</th>
                  <th scope="col">Waiting</th>
                  <th scope="col">Critical</th>
                  <th scope="col">High</th>
                  <th scope="col">Breached</th>
                  <th scope="col">Oldest</th>
                </tr>
              </thead>
              <tbody>
                {data.map((row) => (
                  <tr key={row.userId}>
                    <th scope="row">
                      {row.fullName}
                      {!row.isAvailableForAssignment
                        ? <Badge tone="neutral">not taking work</Badge>
                        : null}
                      {row.isOverCapacity
                        ? <Badge tone="danger">over capacity</Badge>
                        : null}
                      {row.jobTitle ? <span className={s.permissionKey}>{row.jobTitle}</span> : null}
                    </th>

                    <td className={row.teams.length ? undefined : s.muted}>
                      {row.teams.length ? row.teams.join(', ') : 'no team'}
                    </td>

                    <td>
                      <strong>{row.openTickets}</strong>
                      {row.maxConcurrentTickets > 0
                        ? <span className={s.muted}> / {row.maxConcurrentTickets}</span>
                        : null}
                    </td>

                    <td className={row.inProgress ? undefined : s.muted}>{row.inProgress || '—'}</td>

                    {/* Waiting is not idleness: the ticket is open but the delay is
                        somebody else's, so it should not read as unfinished work. */}
                    <td className={row.waiting ? undefined : s.muted}>{row.waiting || '—'}</td>

                    <td>{row.critical ? <Badge tone="danger">{row.critical}</Badge> : <span className={s.muted}>—</span>}</td>
                    <td>{row.high ? <Badge tone="warning">{row.high}</Badge> : <span className={s.muted}>—</span>}</td>
                    <td>{row.slaBreached ? <Badge tone="danger">{row.slaBreached}</Badge> : <span className={s.muted}>—</span>}</td>

                    <td className={s.muted}>
                      {row.oldestOpenDays === null ? '—' : `${row.oldestOpenDays} d`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <p className={s.hint} style={{ marginTop: 'var(--s-3)' }}>
            <strong>Waiting</strong> counts tickets held up by the requester or a third
            party rather than by the person holding them — open, but not their delay.{' '}
            <strong>Oldest</strong> is the age of their longest-standing open ticket,
            which finds work that has quietly stopped moving better than a total does.
          </p>
        </CardBody>
      </Card>
    </>
  );
}
