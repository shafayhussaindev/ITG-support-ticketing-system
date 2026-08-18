import { QueryClient } from '@tanstack/react-query';
import { ApiError } from '@/services/apiClient';

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        gcTime: 5 * 60_000,
        refetchOnWindowFocus: false,

        retry(failureCount, error) {
          // Retrying a 401, 403 or 404 cannot succeed and only delays the error the
          // user needs to see. A 429 must not be retried either — that is precisely
          // what the rate limiter is asking the client to stop doing.
          if (error instanceof ApiError) {
            if (error.status === 0) {
              return failureCount < 2;
            }
            if (error.status >= 400 && error.status < 500) {
              return false;
            }
          }

          return failureCount < 2;
        },
      },
      mutations: {
        // A mutation may already have taken effect server-side, so an automatic
        // retry risks performing the action twice.
        retry: false,
      },
    },
  });
}
