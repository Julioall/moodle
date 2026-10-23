import { describe, expect, it, vi } from 'vitest';
import { createAppClient } from '../integrations/http/api-client';
import { createReportTemplatesGateway, type ReportDefinition } from '../features/reports/templates/report-templates-gateway';
import { createReportsGateway } from '../features/reports/reports-gateway';

describe('report JSON HTTP contracts', () => {
  it('sends JSON media type and antiforgery through the real app client for every body mutation', async () => {
    const bodies: unknown[] = [];
    const fetcher = vi.fn(async (path: RequestInfo | URL, options?: RequestInit) => {
      if (path === '/api/csrf') return new Response(JSON.stringify({ token: 'synthetic-xsrf' }));
      const headers = new Headers(options?.headers);
      if (headers.get('Content-Type') !== 'application/json') return new Response('', { status: 415 });
      if (headers.get('X-CSRF-TOKEN') !== 'synthetic-xsrf') return new Response('', { status: 400 });
      bodies.push(JSON.parse(String(options?.body)));
      return new Response(JSON.stringify({ id: 'synthetic-model', revision: 1 }));
    });
    const gateway = createReportTemplatesGateway(createAppClient(fetcher));
    const definition: ReportDefinition = { schemaVersion: 1, sourceId: 'student_course', columns: [{ fieldId: 'student.name' }] };
    await gateway.create({ name: 'Synthetic model', definition });
    await gateway.update('synthetic-model', { name: 'Edited model', definition }, 1);
    await gateway.preview({ templateId: 'synthetic-model', expectedRevision: 2, connectionRef: 'synthetic', courseIds: ['1'] }, new AbortController().signal);
    await gateway.generate({ templateId: 'synthetic-model', expectedRevision: 2 }, 'synthetic', ['1']);
    await createReportsGateway(createAppClient(fetcher)).refreshJob('synthetic-job');
    expect(bodies).toHaveLength(5);
    expect(bodies[1]).toMatchObject({ expectedRevision: 1 });
    expect(bodies[3]).toMatchObject({
      templateId: 'synthetic-model',
      expectedRevision: 2,
      reportType: 'template',
      scopeType: 'courses',
      connectionRef: 'synthetic',
      courseIds: ['1'],
    });
    expect(bodies[4]).toEqual({ refreshFromJobId: 'synthetic-job' });
  });
});
