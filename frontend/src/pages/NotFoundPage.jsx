import { useNavigate } from 'react-router-dom';
import { Button, Card, CardBody, EmptyState } from '@/components/ui';

export function NotFoundPage() {
  const navigate = useNavigate();

  return (
    <Card>
      <CardBody>
        <EmptyState
          icon="⌕"
          title="Page not found"
          message="That address does not match anything in the support desk. It may have been mistyped, or the page may belong to a module that has not been built yet."
          actions={
            <>
              <Button size="sm" onClick={() => navigate('/dashboard')}>
                Go to dashboard
              </Button>
              <Button size="sm" variant="secondary" onClick={() => navigate(-1)}>
                Go back
              </Button>
            </>
          }
        />
      </CardBody>
    </Card>
  );
}
