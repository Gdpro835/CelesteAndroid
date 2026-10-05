# CI (GitHub Actions)

`build-apk.yml` here is a **template**, not an active workflow: it sits in `docs/ci/` because
GitHub only runs files from `.github/workflows/`. Move it there to enable it.

## Why it isn't in place already

The file was written in a tooling session whose git credentials are not allowed to push workflow
files (`refusing to allow a Personal Access Token to create or update workflow`). Only you can
commit and push it. Everything else about the build is already in the repository.

## Enable it

```powershell
git pull
mkdir .github\workflows -Force
git mv docs/ci/build-apk.yml .github/workflows/build-apk.yml
git commit -m "CI: build the APK on GitHub Actions"
git push
```

If the push is rejected with a workflow-scope error, re-authorize the CLI with that scope first
(`gh auth refresh -h github.com -s workflow`) or create the file through the GitHub web UI
(**Add file → Create new file**, path `.github/workflows/build-apk.yml`, paste the contents).

## Why the "Run workflow" dialog has no field for the links

That dialog only renders `workflow_dispatch` inputs — here, the *Which APK to build* choice. Secrets
are never form fields: GitHub deliberately keeps them out of anything that becomes part of a run
(inputs are recorded in the run log and are visible to anyone who can read the repository, which
would expose the asset URLs). So the link secrets are configured once, before the first run:

1. Repository page → **Settings** (the tab strip; owner only. On a phone, scroll the tabs sideways).
2. **Secrets and variables → Actions → New repository secret**.
3. Name `CELESTE_GAME_URL`, value = the archive link → **Add secret**. Repeat for `FMOD_ANDROID_URL`.

Or from a PC with the GitHub CLI:

```powershell
gh secret set CELESTE_GAME_URL --body "https://..." --repo Gdpro835/CelesteAndroid
gh secret set FMOD_ANDROID_URL --body "https://..." --repo Gdpro835/CelesteAndroid
```

Only *links* go in (`gh secret set` accepts text up to 48 KB, not files), and the links must be
reachable when the job runs.

## Secrets it needs

The workflow downloads what the repository can't ship; the URLs live in repository secrets
(**Settings → Secrets and variables → Actions**). The same table is in the workflow header.

| Secret | Required | What it points to |
|---|---|---|
| `CELESTE_GAME_URL` | yes | Archive with `Celeste.exe` (FNA build) — the patch module compiles against it. Add `Content/` to use the personal variants. |
| `FMOD_ANDROID_URL` | yes | Archive with `libfmod.so`, `libfmodstudio.so`, `fmod.jar` (FMOD Engine 1.10.14, Android). |
| `NATIVES_ANDROID_URL` | no | Archive with the prebuilt `libSDL3.so`, `libFNA3D.so`, `libFAudio.so`. Without it the workflow installs NDK r27d and builds them (~10 min). |
| `ASSETS_TOKEN` | no | Bearer token for the downloads above when they need authentication. |
| `CELESTE_KEYSTORE_B64` | no | base64 of the release keystore. Without it (plus `CELESTE_KEYSTORE_ALIAS` / `CELESTE_KEYSTORE_PASS`) the APK is signed with the debug key. |
| `ARTIFACT_PASS` | no | Password used to upload a personal APK as an encrypted 7-Zip artifact. Without it personal APKs are not uploaded at all. |

Secrets accept text only (48 KB limit), so `CELESTE_*_URL` must be *links* to the files: a private
GitHub release asset (with `ASSETS_TOKEN`), a signed URL, or any file host. The download step
unpacks zip/gzip/xz/bzip2/tar automatically, so the URLs don't need nice file names.

There is a longer, step-by-step Russian guide (with the commands to pack the archives and set every
secret) here: `ci-secrets-setup.md`, next to the analysis/work report in the workspace where this
was written — `/home/user/ci-secrets-setup.md`. It is not part of the repository.

## Running it

- **Actions → Build APK → Run workflow** and pick a variant: `public` (no official art, safe to
  share), `personal` (icon/art from your own game files), `personal-embedded` (also bundles the
  game — ~860 MB, keep it local unless you need it in CI).
- Or push a tag: `git tag v1.0.2 && git push origin v1.0.2` builds the **public** APK and attaches
  it to a GitHub Release. A non-public variant is never published; the release step refuses.

The public APK is kept as a build artifact for 14 days; a personal APK is only uploaded encrypted
(and for 3 days): artifacts of a public repository are public, and those APKs contain official art.

See [docs/BUILDING.md](../BUILDING.md) for how to produce every input file locally.
