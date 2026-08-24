import { useMemo } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api } from '@/services/apiClient';
import { ticketKeys, ticketService } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Card, CardBody, CardHeader, Field } from '@/components/ui';
import { PriorityBadge } from '@/components/ui/TicketBadges';
import s from './CreateTicketPage.module.css';

const LEVELS = ['Low', 'Medium', 'High', 'Critical'];

const TYPES = [
  'Incident', 'ServiceRequest', 'SoftwareBug', 'DataCorrection', 'AccessRequest',
  'FeatureRequest', 'TrainingRequest', 'SecurityIncident', 'IntegrationFailure',
];

const schema = z.object({
  subject: z.string().min(5, 'Give the issue a short, specific title.').max(300),
  description: z.string().min(20, 'Describe what happened, what you expected, and any error text.').max(20_000),
  type: z.enum(TYPES),
  impact: z.enum(LEVELS),
  urgency: z.enum(LEVELS),
  categoryId: z.string().optional(),
  subcategoryId: z.string().optional(),
  applicationId: z.string().optional(),
  applicationModuleId: z.string().optional(),
  contactPhone: z.string().max(50).optional(),
});

/*
  Mirrors the server's built-in rule so the requester sees the consequence of their
  answers as they pick them. It is a preview only: the ticket that comes back carries
  whatever the organization's configured matrix decided, which may differ.
*/
function previewPriority(impact, urgency) {
  const score = (LEVELS.indexOf(impact) + 1 + LEVELS.indexOf(urgency) + 1) / 2;
  return LEVELS[Math.min(Math.ceil(score), 4) - 1];
}

const IMPACT_HELP = {
  Low: 'Just me, and I can keep working.',
  Medium: 'A few people, or my own work is slowed.',
  High: 'A whole team or an important process is blocked.',
  Critical: 'The business has stopped and there is no workaround.',
};

const URGENCY_HELP = {
  Low: 'Whenever someone gets to it.',
  Medium: 'In the next day or two.',
  High: 'Today.',
  Critical: 'Right now.',
};

export function CreateTicketPage() {
  const navigate = useNavigate();
  const toast = useToast();
  const { can } = useAuth();
  const queryClient = useQueryClient();

  const { data: categories = [] } = useQuery({
    queryKey: ['catalog', 'categories'],
    queryFn: () => api.get('/categories'),
    staleTime: 5 * 60_000,
  });

  const { data: applications = [] } = useQuery({
    queryKey: ['catalog', 'applications'],
    queryFn: () => api.get('/applications'),
    staleTime: 5 * 60_000,
  });

  const {
    register,
    control,
    handleSubmit,
    setValue,
    formState: { errors, isSubmitting },
  } = useForm({
    resolver: zodResolver(schema),
    defaultValues: {
      subject: '',
      description: '',
      type: 'Incident',
      impact: 'Medium',
      urgency: 'Medium',
      categoryId: '',
      subcategoryId: '',
      applicationId: '',
      applicationModuleId: '',
      contactPhone: '',
    },
  });

  const [impact, urgency, categoryId, applicationId] = useWatch({
    control,
    name: ['impact', 'urgency', 'categoryId', 'applicationId'],
  });

  const subcategories = useMemo(
    () => categories.find((c) => c.id === categoryId)?.subcategories ?? [],
    [categories, categoryId],
  );

  const modules = useMemo(
    () => applications.find((a) => a.id === applicationId)?.modules ?? [],
    [applications, applicationId],
  );

  const createTicket = useMutation({
    mutationFn: (values) =>
      ticketService.create({
        subject: values.subject,
        description: values.description,
        type: values.type,
        impact: values.impact,
        urgency: values.urgency,
        // Empty strings from unselected dropdowns must become null, not "".
        categoryId: values.categoryId || null,
        subcategoryId: values.subcategoryId || null,
        applicationId: values.applicationId || null,
        applicationModuleId: values.applicationModuleId || null,
        contactPhone: values.contactPhone || null,
      }),
    onSuccess: (ticket) => {
      queryClient.invalidateQueries({ queryKey: ticketKeys.all });
      toast.success(`Ticket ${ticket.ticketNumber} raised`, 'Support can see it now.');
      navigate(`/tickets/${ticket.id}`);
    },
    onError: (error) => {
      toast.error('Could not raise the ticket', error.detail ?? 'Please try again.');
    },
  });

  // Staff are believed; only a requester is capped. Showing the warning to somebody it
  // does not apply to would be worse than not showing it at all.
  const capped = !can('ticket.claim_any_severity');

  const preview = previewPriority(impact, urgency);

  return (
    <form onSubmit={handleSubmit((values) => createTicket.mutateAsync(values))} noValidate>
      <header className={s.header}>
        <div>
          <h2 className={s.title}>Raise a ticket</h2>
          <p className={s.subtitle}>
            The more specific the description, the sooner it reaches the right person.
          </p>
        </div>
      </header>

      <div className={s.grid}>
        <div className={s.main}>
          <Card>
            <CardHeader title="What is the problem?" />
            <CardBody className={s.stack}>
              <Field
                label="Subject"
                placeholder="Shared printer on the second floor is offline"
                required
                error={errors.subject?.message}
                {...register('subject')}
              />

              <div className={s.field}>
                <label className={s.label} htmlFor="description">
                  Description<span className={s.required} aria-hidden="true">*</span>
                </label>
                <textarea
                  id="description"
                  className={`${s.textarea} ${errors.description ? s.invalid : ''}`}
                  rows={8}
                  placeholder="What were you doing, what happened, and what did you expect instead? Include any error message exactly as it appeared."
                  aria-invalid={errors.description ? 'true' : undefined}
                  aria-describedby={errors.description ? 'description-error' : undefined}
                  {...register('description')}
                />
                {errors.description ? (
                  <span id="description-error" className={s.error} role="alert">
                    {errors.description.message}
                  </span>
                ) : null}
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title="Where does it happen?" subtitle="Optional, but it speeds up routing" />
            <CardBody className={s.twoUp}>
              <div className={s.field}>
                <label className={s.label} htmlFor="categoryId">Category</label>
                <select
                  id="categoryId"
                  className={s.select}
                  {...register('categoryId')}
                  onChange={(e) => {
                    setValue('categoryId', e.target.value);
                    // The old subcategory belongs to a different category and would be
                    // rejected by the server, so clear it.
                    setValue('subcategoryId', '');
                  }}
                >
                  <option value="">Not sure</option>
                  {categories.map((category) => (
                    <option key={category.id} value={category.id}>{category.name}</option>
                  ))}
                </select>
              </div>

              <div className={s.field}>
                <label className={s.label} htmlFor="subcategoryId">Subcategory</label>
                <select
                  id="subcategoryId"
                  className={s.select}
                  disabled={subcategories.length === 0}
                  {...register('subcategoryId')}
                >
                  <option value="">
                    {subcategories.length === 0 ? 'Pick a category first' : 'Not sure'}
                  </option>
                  {subcategories.map((sub) => (
                    <option key={sub.id} value={sub.id}>{sub.name}</option>
                  ))}
                </select>
              </div>

              <div className={s.field}>
                <label className={s.label} htmlFor="applicationId">Application</label>
                <select
                  id="applicationId"
                  className={s.select}
                  {...register('applicationId')}
                  onChange={(e) => {
                    setValue('applicationId', e.target.value);
                    setValue('applicationModuleId', '');
                  }}
                >
                  <option value="">Not application-specific</option>
                  {applications.map((app) => (
                    <option key={app.id} value={app.id}>{app.name}</option>
                  ))}
                </select>
              </div>

              <div className={s.field}>
                <label className={s.label} htmlFor="applicationModuleId">Module</label>
                <select
                  id="applicationModuleId"
                  className={s.select}
                  disabled={modules.length === 0}
                  {...register('applicationModuleId')}
                >
                  <option value="">
                    {modules.length === 0 ? 'Pick an application first' : 'Not sure'}
                  </option>
                  {modules.map((module) => (
                    <option key={module.id} value={module.id}>{module.name}</option>
                  ))}
                </select>
              </div>
            </CardBody>
          </Card>
        </div>

        <aside className={s.side}>
          <Card>
            <CardHeader title="How serious is it?" />
            <CardBody className={s.stack}>
              <div className={s.field}>
                <label className={s.label} htmlFor="type">Ticket type</label>
                <select id="type" className={s.select} {...register('type')}>
                  {TYPES.map((type) => (
                    <option key={type} value={type}>
                      {type.replace(/([a-z])([A-Z])/g, '$1 $2')}
                    </option>
                  ))}
                </select>
              </div>

              <fieldset className={s.fieldset}>
                <legend className={s.legend}>Impact — who is affected?</legend>
                {LEVELS.map((level) => (
                  <label key={level} className={s.radio}>
                    <input type="radio" value={level} {...register('impact')} />
                    <span>
                      <strong>{level}</strong>
                      <span className={s.radioHelp}>{IMPACT_HELP[level]}</span>
                    </span>
                  </label>
                ))}
              </fieldset>

              <fieldset className={s.fieldset}>
                <legend className={s.legend}>Urgency — how soon?</legend>
                {LEVELS.map((level) => (
                  <label key={level} className={s.radio}>
                    <input type="radio" value={level} {...register('urgency')} />
                    <span>
                      <strong>{level}</strong>
                      <span className={s.radioHelp}>{URGENCY_HELP[level]}</span>
                    </span>
                  </label>
                ))}
              </fieldset>

              {/*
                Priority is shown, never chosen. Letting people pick it directly means
                everything arrives as Critical and the queue order stops meaning anything.
              */}
              <div className={s.preview}>
                <span className={s.previewLabel}>Calculated priority</span>
                <PriorityBadge priority={preview} />
                <p className={s.previewNote}>
                  Worked out from impact and urgency using your organization's matrix.
                  It cannot be set by hand here — support can adjust it later with a reason.
                </p>

                {/* Said before they submit rather than discovered afterwards. A
                    requester whose Critical silently became High concludes the system
                    ignored them; one who was told the ceiling knows a person will
                    look. Only shown to people the cap applies to. */}
                {capped && (impact === 'Critical' || urgency === 'Critical') ? (
                  <p className={s.previewCap}>
                    Critical is reserved for support to set. This will be logged as{' '}
                    <strong>High</strong> with what you asked for recorded alongside it,
                    and a team lead can raise it once they have looked.
                  </p>
                ) : null}
              </div>
            </CardBody>
          </Card>

          <Card>
            <CardHeader title="How can we reach you?" />
            <CardBody>
              <Field
                label="Phone (optional)"
                placeholder="Only if we should call"
                hint="Your email is taken from your profile."
                error={errors.contactPhone?.message}
                {...register('contactPhone')}
              />
            </CardBody>
          </Card>

          <div className={s.actions}>
            <Button type="submit" size="lg" fullWidth loading={isSubmitting || createTicket.isPending}>
              Raise ticket
            </Button>
            <Button type="button" variant="secondary" fullWidth onClick={() => navigate('/tickets')}>
              Cancel
            </Button>
          </div>
        </aside>
      </div>
    </form>
  );
}
