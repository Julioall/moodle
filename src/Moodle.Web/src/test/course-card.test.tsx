import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';

import { CourseCard } from '../features/courses/components/CourseCard';

describe('CourseCard', () => {
  it('renders course information without a details link when the feature is off', () => {
    render(
      <MemoryRouter>
        <CourseCard
          course={{
            connectionRef: 'demo',
            courseId: '42',
            fullName: 'Curso de demonstração',
            displayName: 'Curso de demonstração',
          }}
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole('heading', { name: 'Curso de demonstração' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Curso de demonstração/ })).not.toBeInTheDocument();
  });

  it('links the course title to its details when the feature is enabled', () => {
    render(
      <MemoryRouter>
        <CourseCard
          course={{ connectionRef: 'demo', courseId: '42', fullName: 'Curso de demonstração', displayName: 'Curso de demonstração' }}
          detailsHref="/cursos/demo/42"
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole('link', { name: 'Abrir detalhes de Curso de demonstração' })).toHaveAttribute('href', '/cursos/demo/42');
  });
});
