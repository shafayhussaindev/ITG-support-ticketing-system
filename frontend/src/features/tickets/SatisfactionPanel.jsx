import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { reportingKeys, reportingService } from '@/services/reportingService';
import { useToast } from '@/contexts/ToastContext';
import { Button, Card, CardBody, CardHeader } from '@/components/ui';
import s from './SatisfactionPanel.module.css';

const SCALE = [1, 2, 3, 4, 5];

const RATING_WORD = {
  1: 'Poor',
  2: 'Below expectations',
  3: 'Acceptable',
  4: 'Good',
  5: 'Excellent',
};

/**
 * Asks the requester how the ticket was handled, once it is finished.
 *
 * Shown only to the requester and only on a resolved or closed ticket, mirroring the
 * server rules rather than duplicating judgement. A rating cannot be changed after
 * submission, so the panel switches to a read-only summary rather than offering an
 * edit that the API would refuse.
 */
export function SatisfactionPanel({ ticketId, ticketStatus, isRequester }) {
  const toast = useToast();
  const queryClient = useQueryClient();

  const [rating, setRating] = useState(0);
  const [resolutionRating, setResolutionRating] = useState(0);
  const [staffRating, setStaffRating] = useState(0);
  const [comment, setComment] = useState('');

  const finished = ticketStatus === 'Resolved' || ticketStatus === 'Closed';

  const { data: existing, isPending } = useQuery({
    queryKey: reportingKeys.rating(ticketId),
    queryFn: () => reportingService.ticketRating(ticketId),
    enabled: finished,
  });

  const submit = useMutation({
    mutationFn: () =>
      reportingService.submitRating(ticketId, {
        rating,
        resolutionRating: resolutionRating || null,
        staffRating: staffRating || null,
        comment: comment.trim() || null,
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: reportingKeys.rating(ticketId) });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      toast.success('Thank you', 'Your feedback has been recorded.');
    },
    onError: (error) => toast.error('Could not submit your rating', error.detail),
  });

  if (!finished || isPending) {
    return null;
  }

  if (existing) {
    return (
      <Card>
        <CardHeader title="Your feedback" subtitle="Ratings cannot be changed once submitted" />
        <CardBody>
          <div className={s.summary}>
            <Stars value={existing.rating} readOnly />
            <span className={s.word}>{RATING_WORD[existing.rating]}</span>
          </div>
          {existing.comment ? <p className={s.comment}>{existing.comment}</p> : null}
        </CardBody>
      </Card>
    );
  }

  if (!isRequester) {
    // Only the person who experienced the support can rate it, so nobody else is
    // shown a form the API would reject.
    return null;
  }

  return (
    <Card>
      <CardHeader title="How did we do?" subtitle="Your answer is visible to the support team" />
      <CardBody>
        <form
          className={s.form}
          onSubmit={(event) => {
            event.preventDefault();
            if (rating > 0) {
              submit.mutate();
            }
          }}
        >
          <fieldset className={s.fieldset}>
            <legend className={s.legend}>Overall</legend>
            <Stars value={rating} onChange={setRating} name="overall" />
            {rating > 0 ? <span className={s.word}>{RATING_WORD[rating]}</span> : null}
          </fieldset>

          <fieldset className={s.fieldset}>
            <legend className={s.legend}>Did it fix the problem?</legend>
            <Stars value={resolutionRating} onChange={setResolutionRating} name="resolution" />
          </fieldset>

          <fieldset className={s.fieldset}>
            <legend className={s.legend}>How was the staff member?</legend>
            <Stars value={staffRating} onChange={setStaffRating} name="staff" />
          </fieldset>

          <label className={s.commentLabel} htmlFor="csat-comment">
            Anything else? (optional)
          </label>
          <textarea
            id="csat-comment"
            className={s.textarea}
            rows={3}
            value={comment}
            onChange={(event) => setComment(event.target.value)}
            placeholder="What went well, or what could have gone better."
          />

          <Button type="submit" size="sm" loading={submit.isPending} disabled={rating === 0}>
            Submit feedback
          </Button>
        </form>
      </CardBody>
    </Card>
  );
}

/**
 * A radio group styled as stars.
 *
 * Built on real radio inputs rather than clickable spans so it is keyboard operable
 * and announced correctly; the stars are decorative and the accessible name is the
 * numeric label.
 */
function Stars({ value, onChange, name, readOnly = false }) {
  if (readOnly) {
    return (
      <span className={s.stars} aria-label={`${value} out of 5`}>
        {SCALE.map((n) => (
          <span key={n} className={n <= value ? s.starOn : s.starOff} aria-hidden="true">★</span>
        ))}
      </span>
    );
  }

  return (
    <span className={s.stars}>
      {SCALE.map((n) => (
        <label key={n} className={s.starLabel}>
          <input
            type="radio"
            name={name}
            value={n}
            checked={value === n}
            onChange={() => onChange(n)}
            className={s.starInput}
          />
          <span className={n <= value ? s.starOn : s.starOff} aria-hidden="true">★</span>
          <span className="sr-only">{n} out of 5</span>
        </label>
      ))}
    </span>
  );
}
