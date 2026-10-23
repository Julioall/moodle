import { readdir, readFile } from 'node:fs/promises';
import { extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../src/', import.meta.url));
const forbidden = [
  [/supabase/i, 'Supabase academic'],
  [/(?:\bopenai\b|@ai-sdk|\bllm\b)/i, 'OpenAI/LLM'],
  [/mcp/i, 'MCP'],
  [/floatingclarischat|clarissuggestions|gradesuggestion|assignment.?suggestion/i, 'chat/suggestions/grading'],
  [/(?:moodle[-_/](?:client|rest)|(?:webservice|rest)[-_\s/]*moodle)/i, 'direct Moodle client'],
];
const files = [];
async function walk(dir) {
  for (const item of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, item.name);
    if (item.isDirectory()) await walk(path);
    else if (['.ts', '.tsx', '.js', '.jsx'].includes(extname(item.name))) files.push(path);
  }
}
await walk(root);
const findings = [];
for (const file of files) {
  const content = await readFile(file, 'utf8');
  for (const [pattern, label] of forbidden) if (pattern.test(content)) findings.push(`${label}: ${file}`);
}
if (findings.length) { console.error(findings.join('\n')); process.exit(1); }
console.log(`Production Web guard passed (${files.length} source files).`);
