## Development

When starting the dev server, use background mode:

```
astro dev --background
```

Manage the background server with `astro dev stop`, `astro dev status`, and `astro dev logs`.

## Verifying a change

Run `tman site` after editing anything under `site/`, before committing. It builds the site with
`npm --prefix site run build`, so a change that breaks the Astro compile fails here instead of
landing red. `npm test` in `site/` only runs vitest and never compiles a page, which is how a broken
`docs.astro` reached main. In a fresh worktree with no `site/node_modules`, run `npm ci --prefix site`
once first; `tman site` fails with npm's own message until then.

## Documentation

Full documentation: https://docs.astro.build

Consult these guides before working on related tasks:

- [Adding pages, dynamic routes, or middleware](https://docs.astro.build/en/guides/routing/)
- [Working with Astro components](https://docs.astro.build/en/basics/astro-components/)
- [Using React, Vue, Svelte, or other framework components](https://docs.astro.build/en/guides/framework-components/)
- [Adding or managing content](https://docs.astro.build/en/guides/content-collections/)
- [Adding styles or using Tailwind](https://docs.astro.build/en/guides/styling/)
- [Supporting multiple languages](https://docs.astro.build/en/guides/internationalization/)
