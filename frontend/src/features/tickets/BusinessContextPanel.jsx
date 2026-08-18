import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/services/apiClient';
import { ticketKeys } from '@/services/ticketService';
import { useAuth } from '@/contexts/AuthContext';
import { useToast } from '@/contexts/ToastContext';
import { Button, Card, CardBody, CardHeader } from '@/components/ui';
import s from './BusinessContextPanel.module.css';

const RECORD_TYPES = [
  'PurchaseOrder', 'Style', 'Customer', 'Supplier', 'Factory', 'Merchant',
  'ProductionOrder', 'Inspection', 'Shipment', 'Invoice', 'DebitNote',
  'CommissionInvoice', 'DigitalProductPassport', 'Integration', 'Other',
];

function humanize(value = '') {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2');
}

/**
 * Operational records this ticket relates to.
 *
 * Shows references rather than mirrored ERP data — a purchase order number and an
 * optional deep link, not a copy of the order. That keeps one source of truth and
 * keeps commercial detail out of the support database.
 */
export function BusinessContextPanel({ ticketId, records }) {
  const { can } = useAuth();
  const toast = useToast();
  const queryClient = useQueryClient();

  const [adding, setAdding] = useState(false);
  const [form, setForm] = useState({ recordType: 'PurchaseOrder', recordReference: '', recordUrl: '' });

  const canLink = can('ticket.link_records');

  function refresh() {
    queryClient.invalidateQueries({ queryKey: ticketKeys.detail(ticketId) });
  }

  const add = useMutation({
    mutationFn: () =>
      api.post(`/tickets/${ticketId}/related-records`, {
        recordType: form.recordType,
        recordReference: form.recordReference.trim(),
        recordUrl: form.recordUrl.trim() || null,
      }),
    onSuccess: () => {
      setForm({ recordType: 'PurchaseOrder', recordReference: '', recordUrl: '' });
      setAdding(false);
      refresh();
      toast.success('Record linked');
    },
    onError: (error) => toast.error('Could not link that record', error.detail),
  });

  const remove = useMutation({
    mutationFn: (recordId) => api.delete(`/tickets/${ticketId}/related-records/${recordId}`),
    onSuccess: () => {
      refresh();
      toast.success('Record unlinked');
    },
    onError: (error) => toast.error('Could not unlink that record', error.detail),
  });

  if (records.length === 0 && !canLink) {
    return null;
  }

  return (
    <Card>
      <CardHeader
        title="Business context"
        subtitle="References into operational systems"
        actions={
          canLink && !adding ? (
            <Button size="sm" variant="secondary" onClick={() => setAdding(true)}>Link</Button>
          ) : null
        }
      />

      <CardBody>
        {records.length === 0 ? (
          <p className={s.empty}>
            Nothing linked yet. Adding a purchase order or shipment reference makes this
            ticket findable from the record it concerns.
          </p>
        ) : (
          <ul className={s.list}>
            {records.map((record) => (
              <li key={record.id} className={s.item}>
                <div className={s.itemMain}>
                  <span className={s.type}>{humanize(record.recordType)}</span>

                  {record.recordUrl ? (
                    <a
                      className={s.reference}
                      href={record.recordUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      {record.recordReference}
                    </a>
                  ) : (
                    <span className={s.reference}>{record.recordReference}</span>
                  )}

                  {record.recordLabel ? <span className={s.label}>{record.recordLabel}</span> : null}
                  {record.sourceSystem ? <span className={s.source}>{record.sourceSystem}</span> : null}
                </div>

                {canLink ? (
                  <button
                    type="button"
                    className={s.remove}
                    onClick={() => remove.mutate(record.id)}
                    aria-label={`Unlink ${record.recordReference}`}
                    disabled={remove.isPending}
                  >
                    &times;
                  </button>
                ) : null}
              </li>
            ))}
          </ul>
        )}

        {adding ? (
          <form
            className={s.form}
            onSubmit={(event) => {
              event.preventDefault();
              if (form.recordReference.trim()) {
                add.mutate();
              }
            }}
          >
            <label className="sr-only" htmlFor="record-type">Record type</label>
            <select
              id="record-type"
              className={s.select}
              value={form.recordType}
              onChange={(e) => setForm((f) => ({ ...f, recordType: e.target.value }))}
            >
              {RECORD_TYPES.map((type) => (
                <option key={type} value={type}>{humanize(type)}</option>
              ))}
            </select>

            <label className="sr-only" htmlFor="record-reference">Reference</label>
            <input
              id="record-reference"
              className={s.input}
              placeholder="PO-2026-11841"
              value={form.recordReference}
              onChange={(e) => setForm((f) => ({ ...f, recordReference: e.target.value }))}
            />

            <label className="sr-only" htmlFor="record-url">Link (optional)</label>
            <input
              id="record-url"
              className={s.input}
              placeholder="https://erp.example.com/po/11841"
              value={form.recordUrl}
              onChange={(e) => setForm((f) => ({ ...f, recordUrl: e.target.value }))}
            />

            <div className={s.formActions}>
              <Button type="button" size="sm" variant="ghost" onClick={() => setAdding(false)}>
                Cancel
              </Button>
              <Button
                type="submit"
                size="sm"
                loading={add.isPending}
                disabled={!form.recordReference.trim()}
              >
                Link record
              </Button>
            </div>
          </form>
        ) : null}
      </CardBody>
    </Card>
  );
}
