import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ticketKeys, ticketService } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Badge, Button, Card, CardBody, CardHeader, Spinner } from '@/components/ui';
import { formatDuration } from '@/utils/datetime';
import s from './WorkLogPanel.module.css';

/** Common spells of work, because the friction in a timesheet is what stops it being filled in. */
const QUICK_MINUTES = [15, 30, 60, 120];

function today() {
  // Local date, not UTC. Somebody in Karachi logging work at 2am should see today's
  // date in the field, not yesterday's.
  const now = new Date();
  const offset = now.getTimezoneOffset() * 60_000;
  return new Date(now.getTime() - offset).toISOString().slice(0, 10);
}

const EMPTY = { minutes: '', workDate: today(), description: '', isBillable: false };

/**
 * Time spent on this ticket, by whom, on which day.
 *
 * <p>Separate from the closing "work performed" summary, which is one narrative written
 * at the end. This is the running account an invoice or a capacity argument is built
 * from, so it records the day the work happened rather than the day it was typed.</p>
 *
 * <p>Never shown to a requester. How many hours the desk poured into their ticket — or
 * did not — is not a conversation this page should start on their behalf, so the whole
 * panel is behind the same permission as the endpoint.</p>
 */
export function WorkLogPanel({ ticketId }) {
  const { can } = useAuth();
  const toast = useToast();
  const queryClient = useQueryClient();

  const [logging, setLogging] = useState(false);
  const [form, setForm] = useState(EMPTY);

  const mayLog = can('ticket.log_work');

  const { data, isPending } = useQuery({
    queryKey: ticketKeys.work(ticketId),
    queryFn: () => ticketService.work(ticketId),
    enabled: mayLog,
  });

  const refresh = () => queryClient.invalidateQueries({ queryKey: ticketKeys.work(ticketId) });

  const add = useMutation({
    mutationFn: () =>
      ticketService.logWork(ticketId, {
        minutesSpent: Number(form.minutes),

        // Sent as an explicit instant rather than a bare date, so the server is not left
        // guessing which timezone "2026-08-24" was meant in.
        workDateUtc: `${form.workDate}T00:00:00Z`,
        description: form.description.trim(),
        isBillable: form.isBillable,
      }),
    onSuccess: () => {
      // Keeps the chosen day, because a timesheet catch-up logs several entries
      // against the same date in a row.
      setForm((prev) => ({ ...EMPTY, workDate: prev.workDate }));
      setLogging(false);
      refresh();
      toast.success('Work logged');
    },
    onError: (error) => toast.error('Could not log that', error.detail),
  });

  const remove = useMutation({
    mutationFn: (workLogId) => ticketService.deleteWork(ticketId, workLogId),
    onSuccess: () => {
      refresh();
      toast.success('Entry withdrawn');
    },
    onError: (error) => toast.error('Could not withdraw that entry', error.detail),
  });

  // Not a permission failure to show an empty box — the panel simply is not theirs.
  if (!mayLog) return null;

  const entries = data?.entries ?? [];
  const valid = Number(form.minutes) > 0 && form.description.trim().length > 0;

  return (
    <Card>
      <CardHeader
        title="Work logged"
        subtitle={
          isPending
            ? 'Loading'
            : entries.length === 0
              ? 'Nothing recorded against this ticket yet'
              : `${formatDuration(data.totalMinutes)} across ${data.contributors} ${
                  data.contributors === 1 ? 'person' : 'people'
                }${data.billableMinutes > 0 ? ` · ${formatDuration(data.billableMinutes)} billable` : ''}`
        }
        actions={
          !logging ? (
            <Button size="sm" variant="secondary" onClick={() => setLogging(true)}>
              Log work
            </Button>
          ) : null
        }
      />

      <CardBody>
        {isPending ? (
          <Spinner />
        ) : entries.length === 0 && !logging ? (
          <p className={s.empty}>
            Time recorded here is what an invoice or a capacity argument is built from.
            Log it against the day the work happened, not the day you write it down.
          </p>
        ) : null}

        {entries.length > 0 ? (
          <ul className={s.list}>
            {entries.map((entry) => (
              <li key={entry.id} className={s.item}>
                <div className={s.itemMain}>
                  <div className={s.itemHead}>
                    <span className={s.duration}>{formatDuration(entry.minutesSpent)}</span>
                    <span className={s.person}>{entry.userName}</span>
                    <span className={s.date}>{entry.workDateUtc.slice(0, 10)}</span>
                    {entry.isBillable ? <Badge tone="success">billable</Badge> : null}
                  </div>

                  <p className={s.description}>{entry.description}</p>
                </div>

                {/* Decided by the server, so this button and the endpoint cannot
                    disagree about whose entry it is. */}
                {entry.canDelete ? (
                  <button
                    type="button"
                    className={s.remove}
                    onClick={() => remove.mutate(entry.id)}
                    aria-label={`Withdraw your ${formatDuration(entry.minutesSpent)} entry`}
                    disabled={remove.isPending}
                  >
                    &times;
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        ) : null}

        {logging ? (
          <form
            className={s.form}
            onSubmit={(event) => {
              event.preventDefault();
              if (valid) add.mutate();
            }}
          >
            <div className={s.row}>
              <div className={s.minutesField}>
                <label className={s.label} htmlFor="work-minutes">Minutes</label>
                <input
                  id="work-minutes"
                  className={s.input}
                  type="number"
                  min="1"
                  max="1440"
                  inputMode="numeric"
                  placeholder="45"
                  value={form.minutes}
                  onChange={(e) => setForm((prev) => ({ ...prev, minutes: e.target.value }))}
                />

                <div className={s.quick}>
                  {QUICK_MINUTES.map((minutes) => (
                    <button
                      key={minutes}
                      type="button"
                      className={s.quickButton}
                      onClick={() => setForm((prev) => ({ ...prev, minutes: String(minutes) }))}
                    >
                      {formatDuration(minutes)}
                    </button>
                  ))}
                </div>
              </div>

              <div className={s.dateField}>
                <label className={s.label} htmlFor="work-date">Day the work happened</label>
                <input
                  id="work-date"
                  className={s.input}
                  type="date"
                  value={form.workDate}

                  // The server refuses a future date anyway; stopping it here means
                  // nobody has to be told off for something the field could prevent.
                  max={today()}
                  onChange={(e) => setForm((prev) => ({ ...prev, workDate: e.target.value }))}
                />
              </div>
            </div>

            <label className={s.label} htmlFor="work-description">What was done</label>
            <input
              id="work-description"
              className={s.input}
              type="text"
              maxLength={2000}
              placeholder="Traced the fault to the label printer spooler and cleared the queue."
              value={form.description}
              onChange={(e) => setForm((prev) => ({ ...prev, description: e.target.value }))}
            />

            <label className={s.billable}>
              <input
                type="checkbox"
                checked={form.isBillable}
                onChange={(e) => setForm((prev) => ({ ...prev, isBillable: e.target.checked }))}
              />
              Billable to the requesting department
            </label>

            <div className={s.formActions}>
              <Button type="submit" size="sm" disabled={!valid || add.isPending}>
                {add.isPending ? 'Saving' : 'Save entry'}
              </Button>
              <Button
                type="button"
                size="sm"
                variant="ghost"
                onClick={() => {
                  setForm(EMPTY);
                  setLogging(false);
                }}
              >
                Cancel
              </Button>
            </div>
          </form>
        ) : null}
      </CardBody>
    </Card>
  );
}
