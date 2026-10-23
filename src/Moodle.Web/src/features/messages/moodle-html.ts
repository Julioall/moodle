const allowedTags = new Set([
  'a', 'b', 'blockquote', 'br', 'code', 'del', 'div', 'em', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
  'i', 'li', 'ol', 'p', 'pre', 's', 'span', 'strong', 'table', 'tbody', 'td', 'th', 'thead', 'tr',
  'u', 'ul'
]);

const allowedAttributes = new Set(['alt', 'class', 'colspan', 'rowspan', 'title']);

function isSafeUrl(value: string) {
  const normalized = value.trim().toLowerCase();
  return normalized.startsWith('/') || normalized.startsWith('./') || normalized.startsWith('../') ||
    normalized.startsWith('#') || normalized.startsWith('https://') || normalized.startsWith('http://') ||
    normalized.startsWith('mailto:');
}

function copySafeNode(node: Node, documentRef: Document): Node | null {
  if (node.nodeType === Node.TEXT_NODE) return documentRef.createTextNode(node.nodeValue ?? '');
  if (node.nodeType !== Node.ELEMENT_NODE) return null;

  const source = node as HTMLElement;
  const tagName = source.tagName.toLowerCase();
  if (['script', 'style', 'iframe', 'object', 'embed', 'form', 'input', 'button', 'textarea', 'select', 'link', 'meta'].includes(tagName)) return null;

  if (!allowedTags.has(tagName)) {
    const fragment = documentRef.createDocumentFragment();
    source.childNodes.forEach((child) => {
      const safeChild = copySafeNode(child, documentRef);
      if (safeChild) fragment.appendChild(safeChild);
    });
    return fragment;
  }

  const target = documentRef.createElement(tagName);
  for (const attribute of Array.from(source.attributes)) {
    const name = attribute.name.toLowerCase();
    if (name.startsWith('on') || name.startsWith('data-') || name === 'style' || name === 'srcdoc') continue;
    if (name === 'href') {
      if (!isSafeUrl(attribute.value)) continue;
      target.setAttribute('href', attribute.value);
      target.setAttribute('rel', 'noreferrer noopener');
      target.setAttribute('target', '_blank');
      continue;
    }
    if (allowedAttributes.has(name)) target.setAttribute(name, attribute.value);
  }

  source.childNodes.forEach((child) => {
    const safeChild = copySafeNode(child, documentRef);
    if (safeChild) target.appendChild(safeChild);
  });
  return target;
}

export function sanitizeMoodleHtml(value: string) {
  if (typeof document === 'undefined') return value;
  const parsed = new DOMParser().parseFromString(value, 'text/html');
  const output = document.createElement('div');
  parsed.body.childNodes.forEach((node) => {
    const safeNode = copySafeNode(node, document);
    if (safeNode) output.appendChild(safeNode);
  });
  return output.innerHTML;
}

export function moodleHtmlToText(value: string) {
  if (typeof document === 'undefined') return value.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim();
  const parsed = new DOMParser().parseFromString(sanitizeMoodleHtml(value), 'text/html');
  const blockTags = new Set(['blockquote', 'div', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'li', 'p', 'pre']);
  const toText = (node: Node): string => {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue ?? '';
    if (node.nodeType !== Node.ELEMENT_NODE) return '';
    const element = node as HTMLElement;
    if (element.tagName.toLowerCase() === 'br') return '\n';
    const content = Array.from(element.childNodes, toText).join('');
    return blockTags.has(element.tagName.toLowerCase()) ? `\n${content}\n` : content;
  };
  return Array.from(parsed.body.childNodes, toText).join('').replace(/\s+/g, ' ').trim();
}
