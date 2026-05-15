from __future__ import annotations

import json

from playwright.async_api import async_playwright

from .crawler import launch_edge_context, new_clean_visible_page, wait_for_manual_close
from .selector_profile import SELECTOR_FIELDS, SelectorProfile, save_selector_profile
from .settings import Settings


async def open_annotation_browser(settings: Settings, target_url: str) -> None:
    async with async_playwright() as playwright:
        context = await launch_edge_context(playwright, settings, headless=False)

        def save_profile(payload):
            profile = SelectorProfile.from_payload(payload)
            save_selector_profile(settings.selector_profile_path, profile)
            return profile.to_payload()

        page = await prepare_annotation_page(context, target_url, save_profile)
        print("已打开标注窗口。选择字段后点击页面区域，保存后关闭窗口。")
        try:
            await wait_for_manual_close(page)
        finally:
            if not context.pages:
                return
            await context.close()


async def prepare_annotation_page(context, target_url: str, save_profile_callback):
    page = await new_clean_visible_page(context)
    script = _annotation_script()
    await page.expose_function("forumBookmarkSaveSelectorProfile", save_profile_callback)
    await page.add_init_script(script)
    try:
        await page.goto(target_url, wait_until="commit", timeout=20_000)
    except Exception as exc:
        print(f"标注窗口导航未完整完成，继续注入标注面板：{exc}")
    await _inject_annotation_panel(page, script)
    return page


async def _inject_annotation_panel(page, script: str) -> None:
    try:
        await page.add_script_tag(content=script)
    except Exception as exc:
        print(f"标注面板直接注入失败；页面完成加载后会通过初始化脚本自动注入：{exc}")


def _annotation_script() -> str:
    fields_json = json.dumps(SELECTOR_FIELDS, ensure_ascii=False)
    return f"""
(() => {{
  const fields = {fields_json};

  function boot() {{
    if (document.getElementById("fbm-panel")) return;
    if (!document.documentElement) {{
      document.addEventListener("DOMContentLoaded", boot, {{ once: true }});
      window.setTimeout(boot, 50);
      return;
    }}

    const selectors = window.__forumBookmarkSelectors || {{}};
    window.__forumBookmarkSelectors = selectors;
    let activeKey = Object.keys(fields)[0];

    const style = document.createElement("style");
    style.textContent = `
    #fbm-panel {{
      position: fixed;
      top: 16px;
      right: 16px;
      z-index: 2147483647;
      width: 320px;
      max-height: calc(100vh - 32px);
      overflow: auto;
      padding: 12px;
      border: 1px solid #b7c3d0;
      border-radius: 8px;
      background: #ffffff;
      color: #102033;
      font: 14px/1.45 Arial, "Microsoft YaHei", sans-serif;
      box-shadow: 0 14px 40px rgb(15 23 42 / 24%);
    }}
    #fbm-panel h2 {{ margin: 0 0 10px; font-size: 16px; }}
    #fbm-panel button {{
      min-height: 32px;
      margin: 0 6px 6px 0;
      padding: 0 8px;
      border: 1px solid #b7c3d0;
      border-radius: 6px;
      background: #fff;
      color: #102033;
      cursor: pointer;
    }}
    #fbm-panel button.active {{ background: #194b7d; border-color: #194b7d; color: #fff; }}
    #fbm-panel pre {{
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      padding: 8px;
      background: #f3f6fa;
      border-radius: 6px;
      max-height: 260px;
      overflow: auto;
    }}
    #fbm-highlight {{
      position: fixed;
      z-index: 2147483646;
      pointer-events: none;
      border: 2px solid #e11d48;
      background: rgb(225 29 72 / 10%);
      border-radius: 4px;
    }}
  `;
    document.documentElement.appendChild(style);

    const panel = document.createElement("div");
    panel.id = "fbm-panel";
    panel.innerHTML = `
    <h2>论坛抓取标注模式</h2>
    <div id="fbm-fields"></div>
    <div>当前字段：<strong id="fbm-active"></strong></div>
    <pre id="fbm-output"></pre>
    <button id="fbm-save" class="active">保存选择器</button>
  `;
    document.documentElement.appendChild(panel);
    const highlight = document.createElement("div");
    highlight.id = "fbm-highlight";
    document.documentElement.appendChild(highlight);

    const fieldsEl = panel.querySelector("#fbm-fields");
    const activeEl = panel.querySelector("#fbm-active");
    const outputEl = panel.querySelector("#fbm-output");

    for (const [key, label] of Object.entries(fields)) {{
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.key = key;
      button.textContent = label;
      button.addEventListener("click", () => {{
        activeKey = key;
        updatePanel();
      }});
      fieldsEl.appendChild(button);
    }}

    panel.querySelector("#fbm-save").addEventListener("click", async () => {{
      const payload = {{ sample_url: location.href, selectors }};
      try {{
        const saved = await window.forumBookmarkSaveSelectorProfile(payload);
        outputEl.textContent = "已保存：\\n" + JSON.stringify(saved.selectors, null, 2);
      }} catch (error) {{
        outputEl.textContent = "保存失败：" + error;
      }}
    }});

    document.addEventListener("mousemove", (event) => {{
      if (panel.contains(event.target)) return;
      const rect = event.target.getBoundingClientRect();
      highlight.style.left = `${{rect.left}}px`;
      highlight.style.top = `${{rect.top}}px`;
      highlight.style.width = `${{rect.width}}px`;
      highlight.style.height = `${{rect.height}}px`;
    }}, true);

    document.addEventListener("click", (event) => {{
      if (panel.contains(event.target)) return;
      event.preventDefault();
      event.stopPropagation();
      selectors[activeKey] = cssPath(event.target);
      updatePanel();
    }}, true);

    function updatePanel() {{
      activeEl.textContent = fields[activeKey];
      for (const button of fieldsEl.querySelectorAll("button")) {{
        button.classList.toggle("active", button.dataset.key === activeKey);
      }}
      outputEl.textContent = JSON.stringify(selectors, null, 2);
    }}

    function cssPath(element) {{
      if (element.id) return `#${{CSS.escape(element.id)}}`;
      const parts = [];
      let current = element;
      while (current && current.nodeType === Node.ELEMENT_NODE && current !== document.documentElement) {{
        let part = current.localName.toLowerCase();
        const usefulClasses = Array.from(current.classList).filter(Boolean).slice(0, 3);
        if (usefulClasses.length) part += "." + usefulClasses.map((item) => CSS.escape(item)).join(".");
        const parent = current.parentElement;
        if (parent) {{
          const sameTag = Array.from(parent.children).filter((child) => child.localName === current.localName);
          if (sameTag.length > 1) part += `:nth-of-type(${{sameTag.indexOf(current) + 1}})`;
        }}
        parts.unshift(part);
        if (parent && parent.id) {{
          parts.unshift(`#${{CSS.escape(parent.id)}}`);
          break;
        }}
        current = parent;
      }}
      return parts.join(" > ");
    }}

    updatePanel();
    window.__forumBookmarkAnnotator = true;
  }}

  boot();
}})();
"""
