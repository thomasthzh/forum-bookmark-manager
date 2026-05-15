from forum_bookmark_manager.selector_profile import (
    SelectorProfile,
    load_selector_profile,
    save_selector_profile,
)


def test_selector_profile_round_trips_json(tmp_path):
    path = tmp_path / "selector-profile.json"
    profile = SelectorProfile(
        sample_url="https://example.test/thread-1.html",
        selectors={
            "title": "#thread_subject",
            "body": "td.t_f",
            "images": ".post-images",
            "password": ".secret",
            "download_links": ".downloads",
            "project_type": ".breadcrumb",
        },
    )

    save_selector_profile(path, profile)

    loaded = load_selector_profile(path)
    assert loaded == profile


def test_missing_selector_profile_loads_empty_profile(tmp_path):
    profile = load_selector_profile(tmp_path / "missing.json")

    assert profile.sample_url is None
    assert profile.selectors == {}
