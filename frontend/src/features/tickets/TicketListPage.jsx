import { useMemo, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { ticketKeys, ticketService } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { Button, Card, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import { PriorityBadge, StatusBadge, TypeBadge } from '@/components/ui/TicketBadges';
import { formatRelative } from '@/utils/datetime';
import { useRevealList } from '@/motion/hooks';
import s from './TicketListPage.module.css';

const STATUSES = [
  'New', 'Assigned', 'InProgress', 'WaitingForRequester', 'WaitingForThirdParty',
  'Escalated', 'Resolved', 'Closed', 'Reopened', 'Cancelled',
];

const PRIORITIES = ['Critical', 'High', 'Medium', 'Low'];

export function TicketListPage() {
  const { can, user } = useAuth();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // Filters live in the URL so a filtered view can be bookmarked, shared with a
  // colleague, and survives a page reload.
  const filters = useMemo(
    () => ({
      page: Number(searchParams.get('page') ?? 1),
      pageSize: 25,
      search: searchParams.get('search') ?? '',
      status: searchParams.get('status') ?? '',
      priority: searchParams.get('priority') ?? '',
      openOnly: searchParams.get('openOnly') === 'true' ? true : undefined,
      unassigned: searchParams.get('unassigned') === 'true' ? true : undefined,
      assignedStaffId: searchParams.get('mine') === 'true' ? user?.id : undefined,
      sortBy: searchParams.get('sortBy') ?? 'created',
      sortDescending: searchParams.get('sortDescending') !== 'false',
    }),
    [searchParams, user?.id],
  );

  const [searchDraft, setSearchDraft] = useState(filters.search);

  const { data, isPending, isError, error, refetch, isFetching } = useQuery({
    queryKey: ticketKeys.list(filters),
    queryFn: () => ticketService.list(filters),
    // Keeps the previous page visible while the next loads, so the table does not
    // collapse to a spinner on every filter change.
    placeholderData: keepPreviousData,
  });

  /*
    Rows arrive in order rather than all at once. Keyed on the filters, so paging or
    re-filtering replays it — which is the moment the reader needs to notice the list
    changed — while an unrelated re-render does not.
  */
  const tableRef = useRevealList('[data-row]', [searchParams.toString()], { distance: 4 });

  function update(next) {
    const merged = new URLSearchParams(searchParams);

    for (const [key, value] of Object.entries(next)) {
      if (value === undefined || value === null || value === '' || value === false) {
        merged.delete(key);
      } else {
        merged.set(key, String(value));
      }
    }

    // Any filter change resets to the first page; staying on page 4 of a narrower
    // result set shows an empty table that looks like a bug.
    if (!('page' in next)) {
      merged.delete('page');
    }

    setSearchParams(merged);
  }

  function submitSearch(event) {
    event.preventDefault();
    update({ search: searchDraft });
  }

  const activeFilterCount = ['status', 'priority', 'openOnly', 'unassigned', 'mine', 'search']
    .filter((key) => searchParams.get(key)).length;

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Tickets</h2>
          <p className={s.subtitle}>
            {data ? `${data.totalCount} ticket${data.totalCount === 1 ? '' : 's'} you can see` : 'Loading…'}
          </p>
        </div>

        {can('ticket.create') ? (
          <Button onClick={() => navigate('/tickets/new')}>Raise a ticket</Button>
        ) : null}
      </header>

      <Card className={s.filterCard}>
        <form className={s.filters} onSubmit={submitSearch} role="search">
          <div className={s.searchWrap}>
            <label className="sr-only" htmlFor="ticket-search">
              Search tickets
            </label>
            <input
              id="ticket-search"
              className={s.search}
              type="search"
              placeholder="Search by number, subject or description…"
              value={searchDraft}
              onChange={(e) => setSearchDraft(e.target.value)}
            />
          </div>

          <label className="sr-only" htmlFor="filter-status">Status</label>
          <select
            id="filter-status"
            className={s.select}
            value={filters.status}
            onChange={(e) => update({ status: e.target.value })}
          >
            <option value="">Any status</option>
            {STATUSES.map((status) => (
              <option key={status} value={status}>
                {status.replace(/([a-z])([A-Z])/g, '$1 $2')}
              </option>
            ))}
          </select>

          <label className="sr-only" htmlFor="filter-priority">Priority</label>
          <select
            id="filter-priority"
            className={s.select}
            value={filters.priority}
            onChange={(e) => update({ priority: e.target.value })}
          >
            <option value="">Any priority</option>
            {PRIORITIES.map((priority) => (
              <option key={priority} value={priority}>{priority}</option>
            ))}
          </select>

          <label className={s.toggle}>
            <input
              type="checkbox"
              checked={searchParams.get('openOnly') === 'true'}
              onChange={(e) => update({ openOnly: e.target.checked })}
            />
            Open only
          </label>

          {can('ticket.view_team') ? (
            <>
              <label className={s.toggle}>
                <input
                  type="checkbox"
                  checked={searchParams.get('unassigned') === 'true'}
                  onChange={(e) => update({ unassigned: e.target.checked })}
                />
                Unassigned
              </label>

              <label className={s.toggle}>
                <input
                  type="checkbox"
                  checked={searchParams.get('mine') === 'true'}
                  onChange={(e) => update({ mine: e.target.checked })}
                />
                Assigned to me
              </label>
            </>
          ) : null}

          <Button type="submit" size="sm" variant="secondary">Search</Button>

          {activeFilterCount > 0 ? (
            <Button type="button" size="sm" variant="ghost" onClick={() => setSearchParams(new URLSearchParams())}>
              Clear ({activeFilterCount})
            </Button>
          ) : null}
        </form>
      </Card>

      <Card className={s.tableCard}>
        {isPending ? (
          <div className={s.skeletons}>
            {Array.from({ length: 6 }, (_, i) => (
              <Skeleton key={i} height={44} />
            ))}
          </div>
        ) : isError ? (
          <ErrorState error={error} onRetry={refetch} title="Could not load tickets" />
        ) : data.items.length === 0 ? (
          <EmptyState
            icon="≡"
            title={activeFilterCount > 0 ? 'No tickets match these filters' : 'No tickets yet'}
            message={
              activeFilterCount > 0
                ? 'Try widening or clearing the filters.'
                : 'Once tickets are raised they will appear here.'
            }
            actions={
              activeFilterCount > 0 ? (
                <Button size="sm" variant="secondary" onClick={() => setSearchParams(new URLSearchParams())}>
                  Clear filters
                </Button>
              ) : can('ticket.create') ? (
                <Button size="sm" onClick={() => navigate('/tickets/new')}>Raise the first one</Button>
              ) : null
            }
          />
        ) : (
          <>
            <div className={s.tableWrap} aria-busy={isFetching}>
              <table className={s.table}>
                <caption className="sr-only">
                  Tickets, {data.totalCount} in total, page {data.page} of {data.totalPages}
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Number</th>
                    <th scope="col">Subject</th>
                    <th scope="col">Status</th>
                    <th scope="col">Priority</th>
                    <th scope="col">Requester</th>
                    <th scope="col">Assigned to</th>
                    <th scope="col">Raised</th>
                  </tr>
                </thead>
                <tbody ref={tableRef}>
                  {data.items.map((ticket) => (
                    <tr key={ticket.id} className={s.row} data-row>
                      <td>
                        <Link className={s.number} to={`/tickets/${ticket.id}`}>
                          {ticket.ticketNumber}
                        </Link>
                      </td>
                      <td>
                        <Link className={s.subject} to={`/tickets/${ticket.id}`}>
                          {ticket.subject}
                        </Link>
                        <div className={s.meta}>
                          <TypeBadge type={ticket.type} />
                          {ticket.categoryName ? <span>{ticket.categoryName}</span> : null}
                          {ticket.commentCount > 0 ? <span>{ticket.commentCount} replies</span> : null}
                        </div>
                      </td>
                      <td><StatusBadge status={ticket.status} /></td>
                      <td><PriorityBadge priority={ticket.priority} /></td>
                      <td className={s.person}>{ticket.requesterName}</td>
                      <td className={s.person}>
                        {ticket.assignedStaffName ?? (
                          <span className={s.unassigned}>Unassigned</span>
                        )}
                      </td>
                      <td className={s.when} title={new Date(ticket.createdAtUtc).toLocaleString()}>
                        {formatRelative(ticket.createdAtUtc)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <nav className={s.pager} aria-label="Pagination">
              <span className={s.pageInfo}>
                Page {data.page} of {data.totalPages || 1}
              </span>
              <div className={s.pageButtons}>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={!data.hasPrevious}
                  onClick={() => update({ page: data.page - 1 })}
                >
                  Previous
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={!data.hasNext}
                  onClick={() => update({ page: data.page + 1 })}
                >
                  Next
                </Button>
              </div>
            </nav>
          </>
        )}
      </Card>
    </>
  );
}
