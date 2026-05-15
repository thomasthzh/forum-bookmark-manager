# Changelog

## 0.2.1 - 2026-05-16

- Removed real forum domain examples from public documentation, defaults, tests, and release assets.
- Reworked README to describe the project as a generic forum favorite crawler.
- Changed public example config to use placeholder domains and blank local proxy/controller values.
- Made Windows start/stop scripts work from a fresh checkout by setting `PYTHONPATH` and installing missing dependencies when needed.

## 0.2.0 - 2026-05-15

- Added mirror-site failover between a primary site and a mirror site.
- Added optional proxy-controller integration for region-based switching.
- Split post-content crawling from image downloading with separate worker queues.
- Increased default worker counts by three for favorite pages, post details, and images.
- Added a crawl dialog mirror URL field.
- Added a proxy status and switching panel to the web UI.
- Reworked post image previews to show the first two images larger, with a lightbox for zooming and browsing all images.
- Bumped package version to `0.2.0` for release preparation.
