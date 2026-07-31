import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { AGENTS } from "../data/agents";
import { TOOLS } from "../data/tools";
import { absolute } from "./site";

/**
 * The sitemap, as data and pure functions. `src/pages/sitemap.xml.ts` is the wiring.
 *
 * Hand-rolled rather than pulled from `@astrojs/sitemap`: the integration's value is discovering
 * routes you did not enumerate, and here every route is already enumerated, because the guide pages
 * are generated from `AGENTS` and `TOOLS`.
 *
 * `<changefreq>` and `<priority>` are deliberately absent — Google ignores both, and carrying them
 * invited the reading that the sitemap said something about freshness while the one field that does
 * was missing.
 *
 * Split out of the endpoint so the git-backed dating and the XML rendering can be exercised
 * separately: the rendering is pure and takes the dates as an argument, so the no-dates branch is
 * testable without contriving a repository that has no history.
 */

/** Files whose content ends up in every page, so a change to one dates all of them. */
export const SHARED_SOURCES = [
  "src/layouts/Base.astro",
  "src/components/Blocks.astro",
  "src/lib/site.ts",
  "src/lib/blocks.ts",
];

export interface SitemapRoute {
  /** In-site path, the same one the page passes to the layout. */
  path: string;
  /** Files this page is built from, excluding {@link SHARED_SOURCES}. */
  sources: string[];
}

/** Every indexable route, derived from the same arrays the pages are generated from. */
export const SITEMAP_ROUTES: SitemapRoute[] = [
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
 * A source path that does not exist is a defect in this file, not a reason to publish a sitemap
 * without dates — in the output the two are indistinguishable, which is how the original bug here
 * survived: paths were anchored to `import.meta.url`, which points at the bundled chunk at build
 * time, so every one of them resolved to nothing and `git log` answered with an empty line and
 * exit 0. Renaming a page or a data module now breaks the build instead.
 */
export function assertSourcesExist(paths: string[], root: string): void {
  const missing = paths.filter((p) => !existsSync(resolve(root, p)));
  if (missing.length > 0) {
    throw new Error(`sitemap sources do not exist (root ${root}): ${missing.join(", ")}`);
  }
}

/** Every source path the sitemap depends on, deduplicated. */
export function allSources(routes: SitemapRoute[] = SITEMAP_ROUTES): string[] {
  return [...new Set([...SHARED_SOURCES, ...routes.flatMap((r) => r.sources)])];
}

/** What {@link renderSitemap} needs per URL: where it lives and when it last changed. */
export interface SitemapEntry {
  path: string;
  lastmod: string | null;
}

/**
 * The sitemap XML. Pure — dates come in as data, so this cannot depend on the repository it is
 * built in. Entries with no date emit no `<lastmod>` element rather than an empty one, which is the
 * difference between a crawler learning nothing and a crawler reading a parse error.
 */
export function renderSitemap(entries: SitemapEntry[]): string {
  const urls = entries
    .map(
      (e) => `  <url>
    <loc>${absolute(e.path)}</loc>${e.lastmod ? `\n    <lastmod>${e.lastmod}</lastmod>` : ""}
  </url>`,
    )
    .join("\n");
  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;
}

/**
 * A function answering "when did these files last change", or one that always answers `null`.
 *
 * A shallow clone — what `actions/checkout` produces by default — has one commit, so `git log -1`
 * on every file returns the same date: the last push. That would stamp all 28 URLs as changed on
 * every deploy and teach a crawler to distrust the field. So a shallow repository, or no git at
 * all, yields dates of `null` throughout: an absent field costs a slower recrawl, a wrong one costs
 * the field's credibility. `.github/workflows/site.yml` sets `fetch-depth: 0` to keep the real
 * dates available in CI; the warning below is the signal that it stopped doing so.
 */
export function createCommitDater(root: string): (paths: string[]) => string | null {
  const git = (args: string[]): string =>
    execFileSync("git", args, { cwd: root, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();

  try {
    if (git(["rev-parse", "--is-shallow-repository"]) === "true") {
      console.warn("[sitemap] shallow clone — omitting <lastmod>; set fetch-depth: 0 to restore it");
      return () => null;
    }
  } catch {
    console.warn("[sitemap] git unavailable — omitting <lastmod>");
    return () => null;
  }

  return (paths) => {
    try {
      const dates = paths.map((p) => git(["log", "-1", "--format=%cI", "--", p])).filter((d) => d !== "");
      return dates.length === 0 ? null : dates.sort().at(-1)!;
    } catch {
      return null;
    }
  };
}
