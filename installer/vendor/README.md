# installer/vendor — build-time dependencies (not stored in git)

This directory is **gitignored**. It holds third-party binaries the Hotix
installer bundles, downloaded at build time by `installer/fetch-vendor.ps1`
instead of being committed to the repository (~200 MB of binaries used to be
tracked here, slowing every clone and CI run for no benefit).

## What the script fetches

| Artifact                    | Purpose                          | Pinned SHA256                                                   |
|-----------------------------|----------------------------------|-----------------------------------------------------------------|
| `python-3.12.6-amd64.exe`   | Bundled Python installer         | `5914748e…16045e0a`                                             |
| `Release-26.02.0-0.zip`     | Poppler Windows binaries (PDF)   | `993e4a94…eb85cda5`                                             |
| `poppler/` (extracted)      | Poppler `Library/` + `share/`    | (derived from the zip above)                                    |

## How to use

Run this before compiling the installer:

```powershell
powershell -ExecutionPolicy Bypass -File installer\fetch-vendor.ps1
iscc.exe installer\Hotix.iss
```

The script:
1. Downloads the Python installer from `python.org` and the Poppler build from
   the official `oschwartz10612/poppler-windows` GitHub release.
2. Verifies each download against a **pinned SHA256 checksum** — it refuses to
   proceed on mismatch, so a tampered or truncated binary can never make it
   into the installer.
3. Extracts Poppler into the exact layout `Hotix.iss` expects
   (`vendor\poppler\Library\*` + `vendor\poppler\share\*`).

Re-run it after any change to these dependencies (or pass `-Force` to
re-download regardless).

## Why not Git LFS?

These are static, versioned third-party downloads fetched by a pinned URL +
checksum. Git LFS adds infra/quota overhead without benefit here — the fetch
script reproduces byte-identical files on demand.
