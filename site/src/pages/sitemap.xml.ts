import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
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
 *
 * `<changefreq>` and `<priority>` were dropped: Google ignores both, and they invited the reading
 * that the sitemap said something about freshness when the one field that does was missing.
 */

/** Files whose content ends up in every page, so a change to one dates all of them. */
const SHARED = [
  "src/layouts/Base.astro",
  "src/components/Blocks.astro",
  "src/lib/site.ts",
  "src/lib/blocks.ts",
];

/** Each route with the sources it is built from, nearest-first. */
const routes: { path: string; sources: string[] }[] = [
  { path: "/", sources: ["src/pages/index.astro"] },
  { path: "/docs", sources: ["src/pages/docs.astro"] },
  { path: "/setup", sources: ["src/pages/setup/index.astro", "src/data/agents.ts"] },
  { path: "/tuning", sources: ["src/pages/tuning/index.astro", "src/data/tools.ts"] },
  ...AGENTS.map((a) => ({
    path: `/setup/${a.slug}`,
    sources: ["src/pages/setup/[slug].astro", "src/data/agents.ts"],
  })),
  ...TOOLS.map((t) => ({
    path: `/tuning/${t.slug}`,
    sources: ["src/pages/tuning/[slug].astro", "src/data/tools.ts"],
  })),
];

/**
 * `astro build` runs with the site directory as its cwd, and that is the anchor these paths are
 * written against. Deliberately not `import.meta.url`: this module is bundled into a generated
 * chunk before it runs, so that URL points at the chunk and every path below silently resolved to
 * nothing — `git log` on a path that does not exist succeeds and prints an empty line, so the first
 * version of this file emitted 28 URLs with no `lastmod` and no error.
 */
const siteRoot = process.cwd();

function git(args: string[]): string {
  return execFileSync("git", args, { cwd: siteRoot, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
}

/**
 * A source path that does not exist is a defect in this file, not a reason to publish a sitemap
 * without dates — the two are indistinguishable in the output, which is how the bug above survived.
 * Renaming a page or a data module now breaks the build here instead.
 */
function assertSourcesExist(paths: string[]): void {
  const missing = paths.filter((p) => !existsSync(resolve(siteRoot, p)));
  if (missing.length > 0) {
    throw new Error(`sitemap.xml.ts names sources that do not exist (cwd ${siteRoot}): ${missing.join(", ")}`);
  }
}

/**
 * Whether commit dates here mean anything.
 *
 * A shallow clone — what `actions/checkout` produces by default — has one commit, so `git log -1`
 * on every file returns the same date: the last push. That would stamp all 28 URLs as changed on
 * every deploy and teach a crawler to distrust the field. The build emits no `lastmod` at all
 * rather than a uniform one, because an absent field costs a slower recrawl while a wrong one costs
 * the field's credibility. `.github/workflows/site.yml` sets `fetch-depth: 0` so this stays true in
 * CI; if that is ever dropped, the warning below is the signal.
 */
const datesAreReal = (() => {
  try {
    if (git(["rev-parse", "--is-shallow-repository"]) === "true") {
      console.warn("[sitemap] shallow clone — omitting <lastmod>; set fetch-depth: 0 to restore it");
      return false;
    }
    return true;
  } catch {
    console.warn("[sitemap] git unavailable — omitting <lastmod>");
    return false;
  }
})();

/** ISO 8601 commit date of the newest change to any of `paths`, or null if git cannot say. */
function lastModified(paths: string[]): string | null {
  if (!datesAreReal) return null;
  try {
    const dates = paths
      .map((p) => git(["log", "-1", "--format=%cI", "--", p]))
      .filter((d) => d !== "");
    if (dates.length === 0) return null;
    return dates.sort().at(-1)!;
  } catch {
    return null;
  }
}

export const GET: APIRoute = () => {
  assertSourcesExist([...SHARED, ...new Set(routes.flatMap((r) => r.sources))]);
  const body = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${routes
  .map((r) => {
    const lastmod = lastModified([...r.sources, ...SHARED]);
    return `  <url>
    <loc>${absolute(r.path)}</loc>${lastmod ? `\n    <lastmod>${lastmod}</lastmod>` : ""}
  </url>`;
  })
  .join("\n")}
</urlset>
`;
  return new Response(body, {
    headers: { "Content-Type": "application/xml; charset=utf-8" },
  });
};
