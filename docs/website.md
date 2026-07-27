# Project website

The public landing page is a dependency-free static site under `website/`. It is intentionally
separate from the WPF application and contains no analytics, cookies, account system, API keys, or
runtime provider calls.

## Build locally

```powershell
.\scripts\Build-Website.ps1 `
  -BaseUrl "https://YOUR-USER.github.io/StormATC" `
  -RepositoryUrl "https://github.com/YOUR-USER/StormATC"
```

The deployable result is written to `artifacts/site/`, which is ignored by Git. Preview that
folder with any local static-file server, for example:

```powershell
python -m http.server 4173 --directory artifacts/site
```

Then open `http://localhost:4173/`.

## GitHub Pages deployment

The `GitHub Pages` workflow:

1. runs when `website/` or its branding assets change on `main`;
2. builds a clean static artifact;
3. inserts the actual repository and Pages URLs;
4. uploads the artifact; and
5. deploys it through GitHub Pages.

For the first deployment, open **Settings → Pages → Build and deployment** and select
**GitHub Actions** as the source. After the workflow succeeds, add its URL to the repository's
Website field.

The download buttons always link to `/releases/latest`, so publishing a new GitHub Release updates
the destination without changing the website.

## Editing

- `index.html` contains the page structure and copy.
- `styles.css` contains the layout, responsive rules, and visual system.
- `script.js` contains the mobile navigation, sticky header, and reduced-motion-aware reveals.
- `assets/og.png` is the social preview shown when the site URL is shared.

Keep the site dependency-free unless a feature genuinely requires a build tool. Do not add
analytics, tracking pixels, external fonts, or third-party scripts without a public privacy
discussion and maintainer approval.

Check desktop and mobile layouts, keyboard navigation, reduced-motion behaviour, internal links,
and the latest-release button before merging visible changes.
