import type { APIRoute } from "astro";
import { AGENTS } from "../data/agents";
import { TOOLS } from "../data/tools";
import { absolute } from "../lib/site";

/**
 * Hand-rolled rather than pulled from `@astrojs/sitemap`.
 *
 * The integration's value is discovering routes you did not enumerate; here every route is already
 * enumerated, because the guide pages are generated from `AGENTS` and `TOOLS`. Adding a dependency
 * to re-derive a list this file already imports would be a net loss.
 */
const routes: { path: string; priority: number }[] = [
  { path: "/", priority: 1.0 },
  { path: "/docs", priority: 0.9 },
  { path: "/setup", priority: 0.9 },
  { path: "/tuning", priority: 0.9 },
  ...AGENTS.map((a) => ({ path: `/setup/${a.slug}`, priority: 0.8 })),
  ...TOOLS.map((t) => ({ path: `/tuning/${t.slug}`, priority: 0.7 })),
];

export const GET: APIRoute = () => {
  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${routes
  .map(
    (r) => `  <url>
    <loc>${absolute(r.path)}</loc>
    <changefreq>monthly</changefreq>
    <priority>${r.priority.toFixed(1)}</priority>
  </url>`,
  )
  .join("\n")}
</urlset>
`;
  return new Response(body, {
    headers: { "Content-Type": "application/xml; charset=utf-8" },
  });
};
