import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '@/services/apiClient';
import { Badge, Button, Card, CardBody, CardHeader, ErrorState, LoadingState } from '@/components/ui';
import s from './AiSettingsPage.module.css';

const CAPABILITIES = [
  {
    key: 'classificationEnabled',
    label: 'Suggest a category',
    detail: 'Reads the subject and description and proposes a category and subcategory.',
  },
  {
    key: 'priorityRecommendationEnabled',
    label: 'Suggest a priority',
    detail: 'Offers a second opinion beside the impact-by-urgency matrix. The matrix still decides.',
  },
  {
    key: 'summarisationEnabled',
    label: 'Summarise a thread',
    detail: 'Condenses a long conversation for someone picking the ticket up.',
  },
];

function Toggle({ id, checked, disabled, onChange, label, detail }) {
  return (
    <div className={`${s.toggleRow} ${disabled ? s.disabledRow : ''}`}>
      <div className={s.toggleText}>
        <label className={s.toggleLabel} htmlFor={id}>{label}</label>
        <p className={s.toggleDetail}>{detail}</p>
      </div>
      <input
        id={id}
        type="checkbox"
        className={s.switch}
        role="switch"
        checked={checked}
        disabled={disabled}
        onChange={(event) => onChange(event.target.checked)}
      />
    </div>
  );
}

/**
 * Where an administrator decides whether ticket text may leave the building.
 *
 * Everything here is off until somebody turns it on, and the whole panel is inert
 * until the server actually holds a provider key — a switch that claims to enable
 * something the deployment cannot do is worse than no switch at all.
 */
export function AiSettingsPage() {
  const queryClient = useQueryClient();

  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['ai', 'status'],
    queryFn: () => api.get('/ai/status'),
  });

  const [draft, setDraft] = useState(null);

  useEffect(() => {
    if (data) {
      setDraft({
        enabled: data.enabled,
        autoApplyEnabled: data.autoApplyEnabled,
        autoApplyConfidenceThreshold: data.autoApplyConfidenceThreshold,
        ...data.capabilities,
      });
    }
  }, [data]);

  const save = useMutation({
    mutationFn: (body) => api.put('/ai/configuration', body),
    onSuccess: (result) => queryClient.setQueryData(['ai', 'status'], result),
  });

  if (isError) return <ErrorState error={error} onRetry={refetch} title="Could not load AI settings" />;
  if (isPending || !draft) return <LoadingState label="Loading AI settings" />;

  const locked = !data.providerConfigured;
  const set = (patch) => setDraft((d) => ({ ...d, ...patch }));
  const usage = data.usageThisMonth;

  return (
    <>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>AI assistance</h2>
          <p className={s.subtitle}>
            Optional. The ticketing rules — priority matrix, SLA targets, routing — are
            deterministic and stay that way. AI only ever proposes.
          </p>
        </div>
        <Badge tone={locked ? 'warning' : data.enabled ? 'success' : 'neutral'}>
          {locked ? 'No provider key' : data.enabled ? 'Enabled' : 'Disabled'}
        </Badge>
      </header>

      {locked ? (
        <Card className={s.notice}>
          <CardBody>
            <p className={s.noticeText}>
              No provider key is configured on the server, so these settings are read-only.
              Set <code>OpenAi__ApiKey</code> in the API environment or user-secrets and restart.
              The key is never sent to the browser and never stored in the database.
            </p>
          </CardBody>
        </Card>
      ) : null}

      <div className={s.grid}>
        <Card>
          <CardHeader title="Capabilities" subtitle="Each one is off until switched on" />
          <CardBody>
            <Toggle
              id="ai-enabled"
              checked={draft.enabled}
              disabled={locked}
              onChange={(v) => set({ enabled: v })}
              label="AI assistance"
              detail="The master switch. With this off, no ticket text is sent anywhere."
            />

            <div className={s.divider} />

            {CAPABILITIES.map((cap) => (
              <Toggle
                key={cap.key}
                id={`ai-${cap.key}`}
                checked={Boolean(draft[cap.key])}
                disabled={locked || !draft.enabled}
                onChange={(v) => set({ [cap.key]: v })}
                label={cap.label}
                detail={cap.detail}
              />
            ))}
          </CardBody>
        </Card>

        <Card>
          <CardHeader
            title="Automatic application"
            subtitle="Off by default, and deliberately hard to justify"
          />
          <CardBody>
            <Toggle
              id="ai-auto"
              checked={draft.autoApplyEnabled}
              disabled={locked || !draft.enabled}
              onChange={(v) => set({ autoApplyEnabled: v })}
              label="Apply confident suggestions without review"
              detail="A suggestion above the threshold is applied and attributed to AI in the timeline. Anything less confident still waits for a person."
            />

            <label className={s.sliderLabel} htmlFor="ai-threshold">
              Confidence threshold
              <strong className={s.sliderValue}>
                {Math.round(draft.autoApplyConfidenceThreshold * 100)}%
              </strong>
            </label>
            <input
              id="ai-threshold"
              type="range"
              className={s.slider}
              min={50}
              max={100}
              step={1}
              disabled={locked || !draft.enabled || !draft.autoApplyEnabled}
              value={Math.round(draft.autoApplyConfidenceThreshold * 100)}
              onChange={(e) => set({ autoApplyConfidenceThreshold: Number(e.target.value) / 100 })}
            />
            <p className={s.hint}>
              The server clamps this between 50% and 100% regardless of what is sent.
            </p>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="This month" subtitle={`Model: ${data.modelIdentifier}`} />
          <CardBody>
            <dl className={s.stats}>
              <div><dt>Calls</dt><dd>{usage.calls}</dd></div>
              <div>
                <dt>Failed</dt>
                <dd className={usage.failedCalls > 0 ? s.bad : undefined}>{usage.failedCalls}</dd>
              </div>
              <div><dt>Tokens</dt><dd>{usage.totalTokens.toLocaleString()}</dd></div>
              <div><dt>Estimated cost</dt><dd>${usage.estimatedCostUsd.toFixed(2)}</dd></div>
            </dl>
            <p className={s.hint}>
              Every call is recorded, failures included, so cost and reliability are both
              visible before anyone is surprised by an invoice.
            </p>
          </CardBody>
        </Card>
      </div>

      <div className={s.actions}>
        {save.isError ? <span className={s.saveError}>{save.error.message}</span> : null}
        {save.isSuccess ? <span className={s.saved}>Saved.</span> : null}
        <Button
          disabled={locked}
          loading={save.isPending}
          onClick={() =>
            save.mutate({
              enabled: draft.enabled,
              classificationEnabled: Boolean(draft.classificationEnabled),
              priorityRecommendationEnabled: Boolean(draft.priorityRecommendationEnabled),
              summarisationEnabled: Boolean(draft.summarisationEnabled),
              autoApplyEnabled: draft.autoApplyEnabled,
              autoApplyConfidenceThreshold: draft.autoApplyConfidenceThreshold,
            })
          }
        >
          Save settings
        </Button>
      </div>
    </>
  );
}
