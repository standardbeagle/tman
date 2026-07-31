import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { XMLParser, XMLValidator } from "fast-xml-parser";
import { afterAll, describe, expect, it } from "vitest";
import { AGENTS } from "../src/data/agents";
import { TOOLS } from "../src/data/tools";
import { absolute } from "../src/lib/site";
import {
  SITEMAP_ROUTES,
  allSources,
  assertSourcesExist,
  createCommitDater,
  renderSitemap,
} from "../src/lib/sitemap";
import { GET } from "../src/pages/sitemap.xml";

/**
 * The sitemap is the one artifact here whose consumer never reports a problem: a crawler that
 * cannot parse it, or that is handed URLs the site does not serve, simply indexes less. So these
 * assertions go through a real XML parser and through the endpoint itself, rather than matching
 * substrings in a string the same file just built.
 */

const parser = new XMLParser({ ignoreAttributes: false, isArray: (name) => name === "url" });

function urlsOf(xml: string): { loc: string; lastmod?: string }[] {
  expect(XMLValidator.validate(xml)).toBe(true);
  return parser.parse(xml).urlset?.url ?? [];
}

/** Whether git can supply real per-file dates here, which decides what the endpoint may emit. */
function repoHasRealHistory(): boolean {
  try {
    return (
      execFileSync("git", ["rev-parse", "--is-shallow-repository"], {
        cwd: process.cwd(),
        encoding: "utf8",
        stdio: ["ignore", "pipe", "ignore"],
      }).trim() === "false"
    );
  } catch {
    return false;
  }
}

describe("renderSitemap", () => {
  it("emits well-formed XML with one url per entry", () => {
    const xml = renderSitemap([
      { path: "/", lastmod: "2026-07-31T10:01:16-05:00" },
      { path: "/docs", lastmod: "2026-07-30T09:00:00-05:00" },
    ]);
    const urls = urlsOf(xml);
    expect(urls).toHaveLength(2);
    expect(urls[0]).toEqual({
      loc: "https://standardbeagle.github.io/tman/",
      lastmod: "2026-07-31T10:01:16-05:00",
    });
    expect(urls[1].loc).toBe("https://standardbeagle.github.io/tman/docs/");
  });

  it("omits lastmod entirely when a date is unknown, keeping the URL", () => {
    // the shallow-clone and no-git paths land here; an empty <lastmod></lastmod> would parse as a
    // date the crawler cannot read, which is worse than saying nothing
    const xml = renderSitemap([{ path: "/docs", lastmod: null }]);
    expect(xml).not.toContain("lastmod");
    const urls = urlsOf(xml);
    expect(urls).toHaveLength(1);
    expect(urls[0].loc).toBe("https://standardbeagle.github.io/tman/docs/");
  });

  it("stays valid XML when some entries are dated and others are not", () => {
    const xml = renderSitemap([
      { path: "/", lastmod: "2026-07-31T10:01:16-05:00" },
      { path: "/setup", lastmod: null },
    ]);
    const urls = urlsOf(xml);
    expect(urls.map((u) => u.lastmod)).toEqual(["2026-07-31T10:01:16-05:00", undefined]);
  });
});

describe("SITEMAP_ROUTES", () => {
  it("covers every generated page and nothing else", () => {
    // the drift this catches: adding an agent or a tool without the sitemap learning about it
    const expected = [
      "/",
      "/docs",
      "/setup",
      "/tuning",
      ...AGENTS.map((a) => `/setup/${a.slug}`),
      ...TOOLS.map((t) => `/tuning/${t.slug}`),
    ];
    expect(SITEMAP_ROUTES.map((r) => r.path).sort()).toEqual(expected.sort());
  });

  it("lists no path twice", () => {
    const paths = SITEMAP_ROUTES.map((r) => r.path);
    expect(new Set(paths).size).toBe(paths.length);
  });

  it("names only sources that exist", () => {
    expect(() => assertSourcesExist(allSources(), process.cwd())).not.toThrow();
  });
});

describe("assertSourcesExist", () => {
  it("throws naming the missing file, rather than letting the build emit dateless URLs", () => {
    expect(() => assertSourcesExist(["src/pages/index.astro", "src/pages/gone.astro"], process.cwd())).toThrow(
      /src\/pages\/gone\.astro/,
    );
  });
});

describe("createCommitDater", () => {
  // Built here rather than relying on the checkout this suite happens to run in: the shallow branch
  // is the one that fires in CI when someone drops fetch-depth, so it needs a test that fails when
  // it breaks, not one that is true because the local repository has full history.
  const scratch: string[] = [];

  function tempRepo(shallow: boolean): string {
    const origin = mkdtempSync(join(tmpdir(), "sitemap-origin-"));
    scratch.push(origin);
    const git = (dir: string, args: string[]) =>
      execFileSync("git", args, { cwd: dir, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
    git(origin, ["init", "-q", "-b", "main"]);
    git(origin, ["config", "user.email", "test@example.com"]);
    git(origin, ["config", "user.name", "test"]);
    for (const n of [1, 2]) {
      writeFileSync(join(origin, "tracked.txt"), `revision ${n}\n`);
      git(origin, ["add", "tracked.txt"]);
      git(origin, ["commit", "-q", "-m", `commit ${n}`]);
    }
    if (!shallow) return origin;

    const clone = mkdtempSync(join(tmpdir(), "sitemap-shallow-"));
    scratch.push(clone);
    execFileSync("git", ["clone", "-q", "--depth", "1", `file://${origin}`, clone], {
      stdio: ["ignore", "pipe", "ignore"],
    });
    return clone;
  }

  afterAll(() => {
    for (const d of scratch) rmSync(d, { recursive: true, force: true });
  });

  it("dates a tracked file in a repository with real history", () => {
    const date = createCommitDater(tempRepo(false))(["tracked.txt"]);
    expect(date).not.toBeNull();
    expect(Number.isNaN(Date.parse(date!))).toBe(false);
  });

  it("answers null for a path git does not know about", () => {
    expect(createCommitDater(tempRepo(false))(["never-committed.txt"])).toBeNull();
  });

  it("answers null for every path in a shallow clone", () => {
    // one commit means one date for every file; publishing it would mark all 28 URLs fresh on
    // every deploy, so the whole field is withheld instead
    const dater = createCommitDater(tempRepo(true));
    expect(dater(["tracked.txt"])).toBeNull();
  });

  it("answers null where there is no repository at all", () => {
    const bare = mkdtempSync(join(tmpdir(), "sitemap-nogit-"));
    scratch.push(bare);
    // Without this the test is vacuous: an untracked path answers null inside a repository too, so
    // if TMPDIR ever sat under one, this would pass while proving nothing about the no-git branch.
    expect(() =>
      execFileSync("git", ["rev-parse", "--show-toplevel"], { cwd: bare, stdio: ["ignore", "pipe", "ignore"] }),
    ).toThrow();

    // a tracked-looking name, so only the missing repository can be why the answer is null
    writeFileSync(join(bare, "tracked.txt"), "content\n");
    expect(createCommitDater(bare)(["tracked.txt"])).toBeNull();
  });
});

describe("the sitemap.xml endpoint", () => {
  it("serves parseable XML declaring exactly the site's URLs", async () => {
    const res = await GET({} as never);
    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Type")).toContain("application/xml");

    const urls = urlsOf(await res.text());
    expect(urls.map((u) => u.loc).sort()).toEqual(SITEMAP_ROUTES.map((r) => absolute(r.path)).sort());
  });

  it("dates every URL when the repository has real history, and none when it does not", async () => {
    const urls = urlsOf(await (await GET({} as never)).text());
    const dated = urls.filter((u) => u.lastmod !== undefined);

    if (!repoHasRealHistory()) {
      // a shallow checkout would give every file the same date; the endpoint must publish none
      expect(dated).toHaveLength(0);
      return;
    }

    expect(dated).toHaveLength(urls.length);
    for (const u of dated) {
      const t = Date.parse(u.lastmod!);
      expect(Number.isNaN(t)).toBe(false);
      expect(t).toBeLessThanOrEqual(Date.now());
    }
  });
});
