(() => {
  if (globalThis.__rfbmBridgeInstalled) {
    return;
  }
  globalThis.__rfbmBridgeInstalled = true;

  const MAX_HTML_BYTES = 2_000_000;
  const SELECTOR_FIELDS = [
    ["favoritesList", "收藏列表"],
    ["favoriteItem", "单条收藏"],
    ["pagination", "分页区域"],
    ["threadTitle", "帖子标题"],
    ["postBody", "帖子正文"],
    ["images", "正文图片"],
    ["downloadLinks", "资源链接"],
    ["password", "提取码 / 密码"],
    ["counters", "浏览 / 收藏计数"],
    ["favoriteTime", "收藏时间"]
  ];
  const COLLECTION_FIELDS = new Set(["favoriteItem", "images", "downloadLinks"]);

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === "rfbm_collect_snapshot") {
      collectPageSnapshot(message)
        .then((snapshot) => sendResponse({ ok: true, snapshot }))
        .catch((error) => sendResponse({ ok: false, error: safeErrorMessage(error) }));
      return true;
    }

    if (message?.type === "rfbm_start_annotation") {
      try {
        startAnnotation(message.ticket);
        sendResponse({ ok: true });
      } catch (error) {
        sendResponse({ ok: false, error: safeErrorMessage(error) });
      }
      return false;
    }

    return false;
  });

  async function collectPageSnapshot(message) {
    const root = document.documentElement.cloneNode(true);
    root.querySelectorAll("[data-rfbm-ui], script").forEach((element) => element.remove());
    const bounded = boundUtf8(root.outerHTML || "", MAX_HTML_BYTES);
    const digest = await crypto.subtle.digest("SHA-256", bounded.bytes);

    return {
      type: "page_snapshot",
      nonce: requiredText(message.nonce, "接收器会话 nonce 缺失。"),
      pageKind: inferPageKind(window.location.href),
      runId: null,
      workItemId: null,
      ticket: message.ticket || null,
      requestedUrl: /^https?:\/\//i.test(message.requestedUrl || "")
        ? message.requestedUrl
        : window.location.href,
      finalUrl: window.location.href,
      title: (document.title || "(无标题)").trim(),
      html: bounded.text,
      capturedAtUtc: new Date().toISOString(),
      htmlByteLength: bounded.bytes.byteLength,
      htmlSha256: bytesToHex(new Uint8Array(digest))
    };
  }

  function startAnnotation(ticket) {
    const existing = document.querySelector("[data-rfbm-ui='panel']");
    if (existing) {
      existing.querySelector("select")?.focus();
      return;
    }

    const state = {
      field: SELECTOR_FIELDS[0][0],
      ticket: ticket || null,
      selectors: {},
      hovered: null
    };
    const panel = buildPanel(state);
    document.documentElement.append(panel);

    const onMouseOver = (event) => {
      const target = event.target;
      if (!(target instanceof Element) || target.closest("[data-rfbm-ui]")) {
        return;
      }
      state.hovered?.classList.remove("rfbm-selector-hover");
      state.hovered = target;
      target.classList.add("rfbm-selector-hover");
    };

    const onClick = (event) => {
      const target = event.target;
      if (!(target instanceof Element) || target.closest("[data-rfbm-ui]")) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
      state.selectors[state.field] = deriveCssSelector(target, COLLECTION_FIELDS.has(state.field));
      renderSelections(panel, state);
    };

    const stop = () => {
      state.hovered?.classList.remove("rfbm-selector-hover");
      document.removeEventListener("mouseover", onMouseOver, true);
      document.removeEventListener("click", onClick, true);
      panel.remove();
    };

    panel.querySelector("[data-action='cancel']").addEventListener("click", stop);
    panel.querySelector("[data-action='clear']").addEventListener("click", () => {
      delete state.selectors[state.field];
      renderSelections(panel, state);
    });
    panel.querySelector("[data-action='save']").addEventListener("click", async () => {
      const status = panel.querySelector("[data-role='status']");
      if (Object.keys(state.selectors).length === 0) {
        status.textContent = "请至少标注一个区域。";
        return;
      }

      status.textContent = "正在保存…";
      const response = await chrome.runtime.sendMessage({
        type: "rfbm_submit_selector_profile",
        profile: {
          type: "selector_profile",
          nonce: "filled-by-background",
          siteKey: window.location.host.toLowerCase(),
          sourceUrl: window.location.href,
          selectors: state.selectors,
          ticket: state.ticket,
          capturedAtUtc: new Date().toISOString()
        }
      });

      if (!response?.ok) {
        status.textContent = response?.error || "保存失败，请确认 R-fbm 已启动。";
        return;
      }
      stop();
    });

    document.addEventListener("mouseover", onMouseOver, true);
    document.addEventListener("click", onClick, true);
    renderSelections(panel, state);
  }

  function buildPanel(state) {
    const panel = document.createElement("aside");
    panel.dataset.rfbmUi = "panel";
    panel.className = "rfbm-selector-panel";

    const heading = document.createElement("strong");
    heading.textContent = "R-fbm 页面标注";
    const hint = document.createElement("p");
    hint.textContent = "选择字段，再点击页面中对应区域。";
    const select = document.createElement("select");
    select.setAttribute("aria-label", "当前标注字段");
    for (const [value, label] of SELECTOR_FIELDS) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = label;
      select.append(option);
    }
    select.addEventListener("change", () => {
      state.field = select.value;
      renderSelections(panel, state);
    });

    const current = document.createElement("code");
    current.dataset.role = "current";
    const status = document.createElement("p");
    status.dataset.role = "status";
    status.setAttribute("aria-live", "polite");
    const buttons = document.createElement("div");
    buttons.className = "rfbm-selector-actions";
    buttons.append(
      makeButton("清除此项", "clear", false),
      makeButton("取消", "cancel", false),
      makeButton("保存", "save", true)
    );
    panel.append(heading, hint, select, current, status, buttons);
    return panel;
  }

  function makeButton(label, action, primary) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = label;
    button.dataset.action = action;
    if (primary) {
      button.className = "rfbm-primary";
    }
    return button;
  }

  function renderSelections(panel, state) {
    const current = panel.querySelector("[data-role='current']");
    const selected = state.selectors[state.field];
    current.textContent = selected || "尚未标注";
    panel.querySelector("[data-role='status']").textContent =
      `已标注 ${Object.keys(state.selectors).length} / ${SELECTOR_FIELDS.length} 项`;
  }

  function deriveCssSelector(element, preferCollection) {
    if (element.id) {
      const idSelector = `#${CSS.escape(element.id)}`;
      if (isUnique(idSelector)) {
        return idSelector;
      }
    }

    const attributeSelector = stableAttributeSelector(element);
    if (attributeSelector && (preferCollection || isUnique(attributeSelector))) {
      return attributeSelector;
    }

    const classSelector = stableClassSelector(element);
    if (classSelector && (preferCollection || isUnique(classSelector))) {
      return classSelector;
    }

    const segments = [];
    let current = element;
    for (let depth = 0; current && depth < 6; depth += 1) {
      if (current.id) {
        segments.unshift(`#${CSS.escape(current.id)}`);
        break;
      }
      segments.unshift(pathSegment(current));
      const candidate = segments.join(" > ");
      if (isUnique(candidate)) {
        return candidate;
      }
      current = current.parentElement;
    }
    return segments.join(" > ");
  }

  function stableAttributeSelector(element) {
    const names = ["data-testid", "data-role", "data-type", "itemprop", "role", "name"];
    for (const name of names) {
      const value = element.getAttribute(name);
      if (value && value.length <= 80 && !/token|nonce|auth|session/i.test(name + value)) {
        return `${element.localName}[${name}="${escapeAttribute(value)}"]`;
      }
    }
    return null;
  }

  function stableClassSelector(element) {
    const classes = [...element.classList]
      .filter((name) => /^[A-Za-z_][\w-]*$/.test(name) && !/^rfbm-/.test(name))
      .slice(0, 3);
    return classes.length
      ? `${element.localName}.${classes.map((name) => CSS.escape(name)).join(".")}`
      : null;
  }

  function pathSegment(element) {
    const classSelector = stableClassSelector(element);
    if (classSelector) {
      return classSelector;
    }
    const siblings = element.parentElement
      ? [...element.parentElement.children].filter((sibling) => sibling.localName === element.localName)
      : [];
    const position = siblings.indexOf(element) + 1;
    return siblings.length > 1 ? `${element.localName}:nth-of-type(${position})` : element.localName;
  }

  function isUnique(selector) {
    try {
      return document.querySelectorAll(selector).length === 1;
    } catch {
      return false;
    }
  }

  function inferPageKind(url) {
    if (/\bdo=favorite\b|\/favorites?\b/i.test(url)) {
      return "favoritesList";
    }
    if (/\b(?:tid|thread)=?\d+|thread-\d+/i.test(url)) {
      return "thread";
    }
    return "unknown";
  }

  function boundUtf8(value, maxBytes) {
    const encoder = new TextEncoder();
    const encoded = encoder.encode(value);
    if (encoded.byteLength <= maxBytes) {
      return { text: value, bytes: encoded };
    }
    const text = new TextDecoder().decode(encoded.slice(0, maxBytes));
    return { text, bytes: encoder.encode(text) };
  }

  function bytesToHex(bytes) {
    return [...bytes].map((value) => value.toString(16).padStart(2, "0")).join("");
  }

  function escapeAttribute(value) {
    return value.replace(/["\\]/g, "\\$&");
  }

  function requiredText(value, message) {
    if (typeof value !== "string" || !value.trim()) {
      throw new Error(message);
    }
    return value.trim();
  }

  function safeErrorMessage(error) {
    return error instanceof Error ? error.message : "未知错误";
  }
})();
