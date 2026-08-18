import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { knowledgeKeys, knowledgeService } from '@/services/reportingService';
import { useAuth } from '@/contexts/AuthContext';
import { Badge, Button, Card, EmptyState, ErrorState, Skeleton } from '@/components/ui';
import { formatRelative } from '@/utils/datetime';
import s from './KnowledgePage.module.css';

const STATUS_TONE = {
  Published: 'success',
  Draft: 'neutral',
  InReview: 'warning',
  Archived: 'neutral',
};

export function KnowledgePage() {
  const { can } = useAuth();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const [draft, setDraft] = useState(searchParams.get('search') ?? '');

  const params = {
    search: searchParams.get('search') ?? '',
    status: searchParams.get('status') ?? '',
    page: Number(searchParams.get('page') ?? 1),
    pageSize: 20,
  };

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: knowledgeKeys.search(params),
    queryFn: () => knowledgeService.search(params),
    placeholderData: keepPreviousData,
  });

  function update(next) {
    const merged = new URLSearchParams(searchParams);

    for (const [key, value] of Object.entries(next)) {
      if (!value) {
        merged.delete(key);
      } else {
        merged.set(key, String(value));
      }
    }

    if (!('page' in next)) {
      merged.delete('page');
    }

    setSearchParams(merged);
  }

  const canSeeDrafts = can('knowledge.edit') || can('knowledge.create');

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Knowledge base</h2>
          <p className={s.subtitle}>
            {data ? `${data.totalCount} article${data.totalCount === 1 ? '' : 's'} you can read` : 'Loading…'}
          </p>
        </div>

        {can('knowledge.create') ? (
          <Button onClick={() => navigate('/knowledge/new')}>Write an article</Button>
        ) : null}
      </header>

      <Card className={s.filters}>
        <form
          className={s.filterRow}
          role="search"
          onSubmit={(event) => {
            event.preventDefault();
            update({ search: draft });
          }}
        >
          <label className="sr-only" htmlFor="kb-search">Search articles</label>
          <input
            id="kb-search"
            className={s.search}
            type="search"
            placeholder="Search titles, summaries and content…"
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
          />

          {canSeeDrafts ? (
            <>
              <label className="sr-only" htmlFor="kb-status">Status</label>
              <select
                id="kb-status"
                className={s.select}
                value={params.status}
                onChange={(event) => update({ status: event.target.value })}
              >
                <option value="">Any status</option>
                <option value="Published">Published</option>
                <option value="Draft">Draft</option>
                <option value="InReview">In review</option>
                <option value="Archived">Archived</option>
              </select>
            </>
          ) : null}

          <Button type="submit" size="sm" variant="secondary">Search</Button>
        </form>
      </Card>

      {isPending ? (
        <div className={s.grid}>
          {Array.from({ length: 4 }, (_, i) => (
            <Card key={i}><div style={{ padding: 16 }}><Skeleton height={64} /></div></Card>
          ))}
        </div>
      ) : isError ? (
        <ErrorState error={error} onRetry={refetch} title="Could not load articles" />
      ) : data.items.length === 0 ? (
        <Card>
          <EmptyState
            icon="❑"
            title={params.search ? 'Nothing matched that search' : 'No articles yet'}
            message={
              params.search
                ? 'Try fewer or different words.'
                : 'Articles written from resolved tickets will appear here.'
            }
          />
        </Card>
      ) : (
        <>
          <div className={s.grid}>
            {data.items.map((article) => (
              <Card key={article.id} className={s.card}>
                <button
                  type="button"
                  className={s.cardButton}
                  onClick={() => navigate(`/knowledge/${article.id}`)}
                >
                  <div className={s.cardHead}>
                    <h3 className={s.cardTitle}>{article.title}</h3>
                    {article.status !== 'Published' ? (
                      <Badge tone={STATUS_TONE[article.status] ?? 'neutral'}>
                        {article.status === 'InReview' ? 'In review' : article.status}
                      </Badge>
                    ) : null}
                    {article.visibility === 'Internal' ? (
                      // Marked plainly so an agent never pastes staff-only guidance
                      // into a reply the requester will read.
                      <Badge tone="warning">Staff only</Badge>
                    ) : null}
                  </div>

                  <p className={s.cardSummary}>{article.summary}</p>

                  <div className={s.cardMeta}>
                    {article.categoryName ? <span>{article.categoryName}</span> : null}
                    <span>{article.viewCount} views</span>
                    {article.helpfulRatio !== null && article.helpfulRatio !== undefined ? (
                      <span>{Math.round(article.helpfulRatio * 100)}% found it useful</span>
                    ) : null}
                    <span>{formatRelative(article.publishedAtUtc ?? article.createdAtUtc)}</span>
                  </div>
                </button>
              </Card>
            ))}
          </div>

          <nav className={s.pager} aria-label="Pagination">
            <span className={s.pageInfo}>Page {data.page} of {data.totalPages || 1}</span>
            <div className={s.pageButtons}>
              <Button size="sm" variant="secondary" disabled={!data.hasPrevious}
                      onClick={() => update({ page: data.page - 1 })}>Previous</Button>
              <Button size="sm" variant="secondary" disabled={!data.hasNext}
                      onClick={() => update({ page: data.page + 1 })}>Next</Button>
            </div>
          </nav>
        </>
      )}
    </>
  );
}
