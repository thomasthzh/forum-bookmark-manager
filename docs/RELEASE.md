# Release Guide

This project is published without local crawl data, browser profiles, cookies, logs, or personal favorite-page IDs.

## Local Checklist

1. Run the test suite:

   ```powershell
   python -m pytest -q
   ```

2. Build the package:

   ```powershell
   python -m build
   ```

3. Confirm only public files are tracked:

   ```powershell
   git status --short
   git check-ignore data/bookmarks.sqlite3 data/edge-profile config/settings.toml
   ```

4. Search for accidental private values such as local absolute paths or account-specific IDs:

   ```powershell
   git grep -n -I -E "uid=[0-9]{4,}|C:\\\\Users\\\\" -- . ":(exclude)tests" ":(exclude)docs/RELEASE.md"
   ```

## Versioning

- Update `pyproject.toml` and `src/forum_bookmark_manager/__init__.py`.
- Add user-facing changes to `CHANGELOG.md`.
- Tag releases as `vX.Y.Z`.
