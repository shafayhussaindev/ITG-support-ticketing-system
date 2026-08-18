import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { ThemeProvider } from '@/contexts/ThemeContext';
import { ToastProvider } from '@/contexts/ToastContext';
import { AuthProvider } from '@/contexts/AuthContext';

function testQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
    // Keeps expected query failures out of the test output.
    logger: { log: () => {}, warn: () => {}, error: () => {} },
  });
}

export function renderWithProviders(ui, { route = '/', withAuth = true } = {}) {
  const client = testQueryClient();

  const tree = (
    <QueryClientProvider client={client}>
      <ThemeProvider>
        <ToastProvider>
          <MemoryRouter initialEntries={[route]}>
            {withAuth ? <AuthProvider>{ui}</AuthProvider> : ui}
          </MemoryRouter>
        </ToastProvider>
      </ThemeProvider>
    </QueryClientProvider>
  );

  return { client, ...render(tree) };
}
