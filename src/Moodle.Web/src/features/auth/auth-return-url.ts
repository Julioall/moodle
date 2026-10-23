export function getSafeLocalReturnUrl(returnUrl: string | null, origin = window.location.origin): string | null {
  if (!returnUrl || !returnUrl.startsWith('/') || returnUrl.startsWith('//')) return null;

  try {
    const target = new URL(returnUrl, origin);
    if (target.origin !== origin) return null;
    return `${target.pathname}${target.search}${target.hash}`;
  } catch {
    return null;
  }
}
