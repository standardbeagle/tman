import { getViteConfig } from "astro/config";
import astroConfig from "./astro.config.mjs";

/**
 * The deploy base comes from `astro.config.mjs`, not from a literal here.
 *
 * `src/lib/site.ts` builds every canonical, og:url, and sitemap `<loc>` from
 * `import.meta.env.BASE_URL`, which Vite derives from `base`. `getViteConfig` does not carry
 * Astro's `base` across on its own, so without this the tests ran against `/` and asserted URLs the
 * site never ships — which is exactly what they caught on their first run. Reading it from the same
 * file the build reads means the two cannot drift apart.
 */
export default getViteConfig({
  base: astroConfig.base,
  test: {
    include: ["tests/**/*.test.ts"],
    // vitest assembles its own import.meta.env and defaults BASE_URL to "/", ignoring both `base`
    // above and a `define`. `env` is the hook it does read — the value still comes from the Astro
    // config, so there is no second place to update.
    env: { BASE_URL: astroConfig.base },
  },
});
