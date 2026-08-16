/**
 * Where the API lives, decided at runtime rather than baked into the bundle.
 *
 * Three cases, in order of precedence:
 *
 * 1. A `<meta name="opc-api-base">` in `index.html`. A reverse proxy or an
 *    operator can rewrite one line of HTML; nobody wants to rebuild an Angular
 *    bundle to change a hostname. This is the same "endpoint is configuration"
 *    principle the service itself follows.
 * 2. Port 4200 — the Angular dev server. The API is then a separate origin on
 *    8080, which is exactly the origin the service allows by CORS.
 * 3. Anything else: same origin, because the built bundle is served by something
 *    that also proxies `/api` and `/hubs`.
 */
export function resolveApiBase(): string {
  const configured = document
    .querySelector('meta[name="opc-api-base"]')
    ?.getAttribute('content')
    ?.trim();

  if (configured) return stripTrailingSlash(configured);

  if (location.port === '4200') {
    return `${location.protocol}//${location.hostname}:8080`;
  }

  return '';
}

function stripTrailingSlash(value: string): string {
  return value.endsWith('/') ? value.slice(0, -1) : value;
}
