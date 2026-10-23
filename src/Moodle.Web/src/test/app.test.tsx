import type { ReactNode } from 'react';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../app/providers/AppProviders';

vi.mock('../features/auth/AuthGate', () => ({
  AuthGate: ({ children }: { children: ReactNode }) => <>{children}</>,
}));

vi.mock('../components/layout/AppLayout', async () => {
  const { Outlet } = await import('react-router-dom');
  return { AppLayout: () => <Outlet /> };
});

vi.mock('../features/courses/MyCoursesPage', () => ({
  MyCoursesPage: () => <h1>Meus Cursos</h1>,
}));

vi.mock('../features/courses/CoursePanelPage', () => ({
  CoursePanelPage: () => <h1>Detalhes da turma</h1>,
}));

vi.mock('../features/courses/course-overview-gateway', () => ({
  courseOverviewFeatureQueryKey: ['app', 'course-overview-feature'],
  getCourseOverviewFeatureEnabled: vi.fn(),
  courseOverviewGateway: { get: vi.fn() },
}));

import { App } from '../app/App';
import { getCourseOverviewFeatureEnabled } from '../features/courses/course-overview-gateway';

describe('App', () => {
  afterEach(() => {
    cleanup();
    window.history.replaceState({}, '', '/');
    vi.unstubAllGlobals();
  });

  beforeEach(() => {
    window.history.replaceState({}, '', '/cursos/demo/42');
    vi.mocked(getCourseOverviewFeatureEnabled).mockResolvedValue(false);
  });

  it('redirects legacy course detail URLs to the course catalogue', async () => {
    render(<AppProviders><App /></AppProviders>);

    expect(await screen.findByRole('heading', { name: 'Meus Cursos' })).toBeInTheDocument();
    expect(window.location.pathname).toBe('/meus-cursos');
  });

  it('opens the course overview route when the server feature flag is enabled', async () => {
    vi.mocked(getCourseOverviewFeatureEnabled).mockResolvedValue(true);
    render(<AppProviders><App /></AppProviders>);

    expect(await screen.findByRole('heading', { name: 'Detalhes da turma' })).toBeInTheDocument();
  });
});
