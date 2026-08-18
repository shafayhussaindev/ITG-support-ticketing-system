import { useState } from 'react';
import { useQuery, keepPreviousData } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { auditKeys, auditService } from '@/services/reportsService';
import { Badge, Button, Card, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import { formatDateTime, formatRelative } from '@/utils/datetime';
import s from './AuditLogPage.module.css';

const SOURCE_TONE = {
  Human: 'neutral',
  Rule: 'info',
  Ai: 'warning',
  System: 'info',
};

/** Turns PascalCase action names into something a person reads without decoding. */
function humanize(value = '') {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2');
}

const EMPTY_FILTERS = {
  search: '',
  action: '',
  entityType: '',
  actorId: '',
  failuresOnly: false,
};

function ChangeList({ changes }) {
  if (changes.length === 0) {
    return <p className={s.noChanges}>No field values were recorded with this action.</p>;
  }

  return (
    <dl className={s.changes}>
      {changes.map((change) => (
        <div key={change.field} className={s.change}>
          <dt>{humanize(change.field)}</dt>
          <dd>{change.value ?? <span className={s.null}>not set</span>}</dd>
        </div>
      ))}
    </dl>
  );
}

function Row({ entry, expanded, onToggle }) {
  return (
    <>
      <tr className={entry.isFailure ? s.failureRow : undefined}>
        <td className={s.when}>
          <span className={s.whenRelative}>{formatRelative(entry.occurredAtUtc)}</span>
          <span className={s.whenAbsolute}>{formatDateTime(entry.occurredAtUtc)}</span>
        </td>

        <td>
          <span className={s.action}>{humanize(entry.action)}</span>
          {entry.isFailure ? <Badge tone="danger">denied</Badge> : null}
        </td>

        <td>
          <span className={s.entityType}>{entry.entityType}</span>
          {entry.entityReference ? (
            <span className={s.entityReference}>{entry.entityReference}</span>
          ) : null}
        </td>

        <td>
          {entry.actorName ? (
            <>
              <span className={s.actorName}>{entry.actorName}</span>
              <span className={s.actorEmail}>{entry.actorEmail}</span>
            </>
          ) : (
            // Sign-in and token refresh are recorded before a principal exists, and
            // background sweeps have no person behind them at all. The entity column
            // still carries the account the row concerns.
            <span className={s.muted}>no signed-in actor</span>
          )}
        </td>

        <td><Badge tone={SOURCE_TONE[entry.source] ?? 'neutral'}>{entry.source}</Badge></td>

        <td className={s.rowActions}>
          <button
            type="button"
            className={s.expand}
            aria-expanded={expanded}
            onClick={onToggle}
          >
            {expanded ? 'Hide' : 'Detail'}
          </button>
        </td>
      </tr>

      {expanded ? (
        <tr className={s.detailRow}>
          <td colSpan={6}>
            <div className={s.detail}>
              <ChangeList changes={entry.changes} />

              <dl className={s.meta}>
                {entry.reason ? (
                  <div><dt>Reason</dt><dd>{entry.reason}</dd></div>
                ) : null}
                {entry.failureReason ? (
                  <div><dt>Failure</dt><dd>{entry.failureReason}</dd></div>
                ) : null}
                {entry.ipAddress ? (
                  <div><dt>IP address</dt><dd className={s.mono}>{entry.ipAddress}</dd></div>
                ) : null}
                {entry.correlationId ? (
                  <div>
                    <dt>Correlation</dt>
                    <dd className={s.mono}>{entry.correlationId}</dd>
                  </div>
                ) : null}
              </dl>

              {entry.entityType === 'Ticket' && entry.entityId ? (
                <Link className={s.entityLink} to={`/tickets/${entry.entityId}`}>
                  Open {entry.entityReference ?? 'this ticket'}
                </Link>
              ) : null}
            </div>
          </td>
        </tr>
      ) : null}
    </>
  );
}

export function AuditLogPage() {
  const [filters, setFilters] = useState(EMPTY_FILTERS);
  const [applied, setApplied] = useState(EMPTY_FILTERS);
  const [page, setPage] = useState(1);
  const [expanded, setExpanded] = useState(null);

  const params = { ...applied, page, pageSize: 50 };

  const { data, isPending, isError, error, refetch, isFetching } = useQuery({
    queryKey: auditKeys.search(params),
    queryFn: () => auditService.search(params),
    // Keeps the previous page on screen while the next one loads, so paging does
    // not blank the table and shift everything the reader was looking at.
    placeholderData: keepPreviousData,
  });

  const { data: options } = useQuery({
    queryKey: auditKeys.filters(),
    queryFn: auditService.filters,
    staleTime: 300_000,
  });

  function apply(event) {
    event.preventDefault();
    setPage(1);
    setExpanded(null);
    setApplied(filters);
  }

  function reset() {
    setFilters(EMPTY_FILTERS);
    setApplied(EMPTY_FILTERS);
    setPage(1);
  }

  const hasFilters = Object.entries(applied).some(([, value]) => value !== '' && value !== false);

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Audit log</h2>
          <p className={s.subtitle}>
            Every security- and business-significant action, including the ones that
            were refused. Rows are append-only: the persistence layer rejects any
            update or delete, so nothing here can be quietly corrected after the fact.
            Passwords, tokens and message bodies are never recorded.
          </p>
        </div>

        {options ? (
          <div className={s.stats}>
            <span className={s.statValue}>{options.totalEntries.toLocaleString()}</span>
            <span className={s.statLabel}>entries</span>
            {options.earliestEntryUtc ? (
              <span className={s.statHint}>since {formatDateTime(options.earliestEntryUtc)}</span>
            ) : null}
          </div>
        ) : null}
      </header>

      <Card className={s.filterCard}>
        <form className={s.filters} onSubmit={apply}>
          <label className={s.field}>
            <span className="sr-only">Search</span>
            <input
              type="search"
              className={s.input}
              placeholder="Ticket number, person or reason…"
              value={filters.search}
              onChange={(e) => setFilters((f) => ({ ...f, search: e.target.value }))}
            />
          </label>

          <label className={s.field}>
            <span className="sr-only">Action</span>
            <select
              className={s.select}
              value={filters.action}
              onChange={(e) => setFilters((f) => ({ ...f, action: e.target.value }))}
            >
              <option value="">Any action</option>
              {(options?.actions ?? []).map((action) => (
                <option key={action} value={action}>{humanize(action)}</option>
              ))}
            </select>
          </label>

          <label className={s.field}>
            <span className="sr-only">Entity type</span>
            <select
              className={s.select}
              value={filters.entityType}
              onChange={(e) => setFilters((f) => ({ ...f, entityType: e.target.value }))}
            >
              <option value="">Any entity</option>
              {(options?.entityTypes ?? []).map((type) => (
                <option key={type} value={type}>{type}</option>
              ))}
            </select>
          </label>

          <label className={s.field}>
            <span className="sr-only">Actor</span>
            <select
              className={s.select}
              value={filters.actorId}
              onChange={(e) => setFilters((f) => ({ ...f, actorId: e.target.value }))}
            >
              <option value="">Anyone</option>
              {(options?.actors ?? []).map((actor) => (
                <option key={actor.id} value={actor.id}>
                  {actor.name} ({actor.entryCount})
                </option>
              ))}
            </select>
          </label>

          <label className={s.checkbox}>
            <input
              type="checkbox"
              checked={filters.failuresOnly}
              onChange={(e) => setFilters((f) => ({ ...f, failuresOnly: e.target.checked }))}
            />
            Denied only
          </label>

          <div className={s.filterActions}>
            <Button type="submit" size="sm" loading={isFetching && !isPending}>Search</Button>
            {hasFilters ? (
              <Button type="button" size="sm" variant="ghost" onClick={reset}>Clear</Button>
            ) : null}
          </div>
        </form>
      </Card>

      <Card>
        {isPending ? (
          <div className={s.skeletons}>
            {Array.from({ length: 8 }, (_, i) => <Skeleton key={i} height={34} />)}
          </div>
        ) : isError ? (
          <ErrorState error={error} onRetry={refetch} title="Could not load the audit log" />
        ) : data.items.length === 0 ? (
          <EmptyState
            icon="◧"
            title="Nothing matches"
            message={
              hasFilters
                ? 'No entry matches those filters. The log only holds what has actually happened.'
                : 'The log is empty, which for a running system means something is wrong with auditing.'
            }
            actions={hasFilters ? (
              <Button size="sm" variant="secondary" onClick={reset}>Clear the filters</Button>
            ) : null}
          />
        ) : (
          <>
            <div className={s.tableWrap}>
              <table className={s.table}>
                <caption className="sr-only">
                  Audit entries, {data.totalCount} matching
                </caption>
                <thead>
                  <tr>
                    <th scope="col">When</th>
                    <th scope="col">Action</th>
                    <th scope="col">Entity</th>
                    <th scope="col">Who</th>
                    <th scope="col">Source</th>
                    <th scope="col"><span className="sr-only">Detail</span></th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((entry) => (
                    <Row
                      key={entry.id}
                      entry={entry}
                      expanded={expanded === entry.id}
                      onToggle={() => setExpanded(expanded === entry.id ? null : entry.id)}
                    />
                  ))}
                </tbody>
              </table>
            </div>

            <div className={s.pager}>
              <span className={s.pagerText}>
                Page {data.page} of {data.totalPages} · {data.totalCount.toLocaleString()} entries
              </span>

              <div className={s.pagerButtons}>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={!data.hasPrevious}
                  onClick={() => { setPage((p) => p - 1); setExpanded(null); }}
                >
                  Previous
                </Button>
                <Button
                  size="sm"
                  variant="secondary"
                  disabled={!data.hasNext}
                  onClick={() => { setPage((p) => p + 1); setExpanded(null); }}
                >
                  Next
                </Button>
              </div>
            </div>
          </>
        )}
      </Card>
    </>
  );
}
