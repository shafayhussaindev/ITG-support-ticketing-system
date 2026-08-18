import { useMutation } from '@tanstack/react-query';
import { api } from '@/services/apiClient';
import { useAuth } from '@/contexts/AuthContext';
import { Badge, Button, Card, CardBody, CardHeader } from '@/components/ui';
import { PriorityBadge } from '@/components/ui/TicketBadges';
import s from './AiSuggestionPanel.module.css';

/**
 * Asks the model what priority it would choose, and shows the answer next to the rule.
 *
 * The panel deliberately never changes anything. It displays a suggestion and, if the
 * user agrees, sends them to the ordinary priority control — which enforces the same
 * permission and reason requirements it always has. There is no path here by which
 * the model edits a ticket.
 */
export function AiSuggestionPanel({ ticketId, currentPriority }) {
  const { can } = useAuth();

  const ask = useMutation({
    mutationFn: () => api.post(`/ai/tickets/${ticketId}/priority-recommendation`, {}),
  });

  if (!can('ai.use')) {
    return null;
  }

  const result = ask.data;

  return (
    <Card>
      <CardHeader
        title="AI assistance"
        subtitle="Suggestions only — nothing is applied automatically"
      />

      <CardBody>
        {!result ? (
          <>
            <p className={s.intro}>
              Ask the model what priority it would give this ticket. The rule engine
              remains the answer of record either way.
            </p>
            <Button size="sm" variant="secondary" loading={ask.isPending} onClick={() => ask.mutate()}>
              Suggest a priority
            </Button>
          </>
        ) : result.usedFallback ? (
          // Reported plainly rather than dressed up. An unavailable model is a fact
          // the user should see, not something to paper over with the rule's answer.
          <div className={s.fallback}>
            <Badge tone="neutral">AI unavailable</Badge>
            <p className={s.fallbackText}>{result.unavailableReason}</p>
            <p className={s.fallbackText}>
              The deterministic matrix calculated{' '}
              <strong>{result.deterministicValue}</strong> from the reported impact and
              urgency, and that is what the ticket uses.
            </p>
          </div>
        ) : (
          <div className={s.result}>
            <div className={s.row}>
              <span className={s.rowLabel}>Rule engine</span>
              <PriorityBadge priority={result.deterministicValue} />
            </div>

            <div className={s.row}>
              <span className={s.rowLabel}>AI suggests</span>
              {result.suggestedValue ? (
                <PriorityBadge priority={result.suggestedValue} />
              ) : (
                <span className={s.none}>no usable answer</span>
              )}
              <Badge tone={result.confidence >= 0.8 ? 'success' : 'warning'}>
                {Math.round(result.confidence * 100)}% confident
              </Badge>
            </div>

            {result.explanation ? <p className={s.explanation}>{result.explanation}</p> : null}

            {result.agrees ? (
              <p className={s.agrees}>The model agrees with the rule. Nothing to decide.</p>
            ) : result.suggestedValue ? (
              <p className={s.differs}>
                This differs from the calculated priority. Changing it goes through the
                normal priority control and needs a written reason.
              </p>
            ) : null}

            <p className={s.current}>Currently set to {currentPriority}.</p>
          </div>
        )}
      </CardBody>
    </Card>
  );
}
