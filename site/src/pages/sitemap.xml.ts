import type { APIRoute } from "astro";
import {
  SHARED_SOURCES,
  SITEMAP_ROUTES,
  allSources,
  assertSourcesExist,
  createCommitDater,
  renderSitemap,
} from "../lib/sitemap";

/**
 * Wiring only — the routes, the dating, and the rendering live in `src/lib/sitemap.ts` so they can
 * be tested without a build.
 *
 * `astro build` runs with the site directory as its cwd, and that is the anchor the source paths
 * are written against. Deliberately not `import.meta.url`: this module is bundled into a generated
 * chunk before it runs, so that URL points at the chunk rather than at `src/`.
 */
export const GET: APIRoute = () => {
  const root = process.cwd();
  assertSourcesExist(allSources(), root);
  const lastModified = createCommitDater(root);

  const body = renderSitemap(
    SITEMAP_ROUTES.map((r) => ({
      path: r.path,
      lastmod: lastModified([...r.sources, ...SHARED_SOURCES]),
    })),
  );

  return new Response(body, {
    headers: { "Content-Type": "application/xml; charset=utf-8" },
  });
};
