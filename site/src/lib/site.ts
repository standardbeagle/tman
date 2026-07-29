/**
 * One place that knows how a path becomes a URL.
 *
 * `import.meta.env.BASE_URL` is `/tman` or `/tman/` depending on Astro version and config, and
 * every page needs both a root-relative href (for links) and an absolute one (for canonical, Open
 * Graph, JSON-LD, and the sitemap). Building those by string concatenation at each call site is how
 * a site ends up shipping `//` in its canonical tags and losing the page from the index.
 */

/** Origin the site is served from, without a trailing slash. Must match `site` in astro.config. */
export const ORIGIN = "https://standardbeagle.github.io";

/** Deploy base path, normalised to no trailing slash (`/tman`, or `""` at a domain root). */
export const BASE = import.meta.env.BASE_URL.replace(/\/+$/, "");

/**
 * Root-relative href for an in-site path. `href("/docs")` -> `/tman/docs/`.
 *
 * The trailing slash is not cosmetic. Astro emits `docs/index.html`, which the host serves at
 * `/tman/docs/` and reaches from `/tman/docs` only via a redirect. Linking and declaring canonical
 * without it means every internal click and every crawl of the site takes a redirect hop to a URL
 * that differs from the one the page claims for itself. Paths naming a file — `/sitemap.xml` — are
 * left alone.
 */
export function href(path: string): string {
  const trimmed = path.replace(/^\/+|\/+$/g, "");
  if (trimmed === "") return `${BASE}/`;
  const isFile = /\.[a-z0-9]+$/i.test(trimmed);
  return `${BASE}/${trimmed}${isFile ? "" : "/"}`;
}

/** Absolute URL for an in-site path — canonical tags, og:url, JSON-LD, sitemap. */
export function absolute(path: string): string {
  return `${ORIGIN}${href(path)}`;
}

/** The repository, referenced from enough places to be worth naming once. */
export const REPO = "https://github.com/standardbeagle/tman";
