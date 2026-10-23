import { describe, expect, it } from 'vitest';

import { moodleHtmlToText, sanitizeMoodleHtml } from '../features/messages/moodle-html';

describe('Moodle message HTML', () => {
  it('keeps Moodle formatting while removing active content', () => {
    const html = sanitizeMoodleHtml('<p>Olá <strong>turma</strong>!</p><p><a href="https://moodle.test/a">Abrir</a><script>alert(1)</script></p>');

    expect(html).toContain('<strong>turma</strong>');
    expect(html).toContain('href="https://moodle.test/a"');
    expect(html).not.toContain('<script');
  });

  it('rejects javascript URLs and creates readable conversation previews', () => {
    const html = sanitizeMoodleHtml('<a href="javascript:alert(1)">link</a><p>Primeira<br>linha</p>');

    expect(html).not.toContain('javascript:');
    expect(moodleHtmlToText(html)).toBe('link Primeira linha');
  });
});
