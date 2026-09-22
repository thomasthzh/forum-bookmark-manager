const RFBM_BASE_URL = "http://127.0.0.1:53112";
const RFBM_SESSION_URL = `${RFBM_BASE_URL}/api/extension/session`;
const RFBM_SNAPSHOT_URL = `${RFBM_BASE_URL}/api/extension/page-snapshot`;
const RFBM_SELECTOR_URL = `${RFBM_BASE_URL}/api/extension/selector-profile`;
const RFBM_SEND_MENU_ID = "rfbm-send-page-snapshot";
const RFBM_ANNOTATE_MENU_ID = "rfbm-annotate-selectors";

chrome.runtime.onInstalled.addListener(async () => {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({
    id: RFBM_SEND_MENU_ID,
    title: "发送当前页到 R-fbm",
    contexts: ["page"]
  });
  chrome.contextMenus.create({
    id: RFBM_ANNOTATE_MENU_ID,
    title: "为 R-fbm 标注页面区域",
    contexts: ["page"]
  });
});

chrome.action.onClicked.addListener(async (tab) => {
  await runWithFeedback(tab, () => sendCurrentPage(tab));
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === RFBM_SEND_MENU_ID) {
    await runWithFeedback(tab, () => sendCurrentPage(tab));
  } else if (info.menuItemId === RFBM_ANNOTATE_MENU_ID) {
    await runWithFeedback(tab, () => startAnnotation(tab), "标");
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type !== "rfbm_submit_selector_profile" || !sender.tab?.id) {
    return false;
  }

  submitSelectorProfile(message.profile)
    .then((reply) => sendResponse({ ok: true, reply }))
    .catch((error) => sendResponse({ ok: false, error: safeErrorMessage(error) }));
  return true;
});

async function sendCurrentPage(tab) {
  requireWebTab(tab);
  const session = await getBridgeSession();
  await ensureContentScript(tab.id);
  const response = await chrome.tabs.sendMessage(tab.id, {
    type: "rfbm_collect_snapshot",
    nonce: session.nonce,
    ticket: crypto.randomUUID(),
    requestedUrl: tab.url
  });

  if (!response?.ok || !response.snapshot) {
    throw new Error(response?.error || "页面快照采集失败。");
  }

  return postBridgeMessage(RFBM_SNAPSHOT_URL, response.snapshot);
}

async function startAnnotation(tab) {
  requireWebTab(tab);
  await ensureContentScript(tab.id);
  const response = await chrome.tabs.sendMessage(tab.id, {
    type: "rfbm_start_annotation",
    ticket: crypto.randomUUID()
  });

  if (!response?.ok) {
    throw new Error(response?.error || "页面标注器启动失败。");
  }
}

async function submitSelectorProfile(profile) {
  const session = await getBridgeSession();
  return postBridgeMessage(RFBM_SELECTOR_URL, {
    ...profile,
    nonce: session.nonce
  });
}

async function getBridgeSession() {
  const response = await fetch(RFBM_SESSION_URL, {
    method: "GET",
    cache: "no-store",
    credentials: "omit",
    referrerPolicy: "no-referrer"
  });
  return readBridgeReply(response);
}

async function postBridgeMessage(url, message) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(message),
    cache: "no-store",
    credentials: "omit",
    referrerPolicy: "no-referrer"
  });
  return readBridgeReply(response);
}

async function readBridgeReply(response) {
  const payload = await response.json().catch(() => null);
  if (!response.ok || !payload?.ok) {
    throw new Error(payload?.message || `R-fbm 本地接收器返回 ${response.status}。`);
  }
  return payload;
}

async function ensureContentScript(tabId) {
  await chrome.scripting.insertCSS({ target: { tabId }, files: ["overlay.css"] });
  await chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"] });
}

async function runWithFeedback(tab, operation, successText = "✓") {
  if (!tab?.id) {
    return;
  }

  try {
    await operation();
    await setBadge(tab.id, successText, "#16734d");
  } catch (error) {
    console.warn("R-fbm bridge action failed:", safeErrorMessage(error));
    await setBadge(tab.id, "!", "#b42318");
  }
}

async function setBadge(tabId, text, color) {
  await chrome.action.setBadgeBackgroundColor({ tabId, color });
  await chrome.action.setBadgeText({ tabId, text });
  setTimeout(() => chrome.action.setBadgeText({ tabId, text: "" }), 2500);
}

function requireWebTab(tab) {
  if (!tab?.id || !/^https?:\/\//i.test(tab.url || "")) {
    throw new Error("只能从 HTTP(S) 页面采集快照。");
  }
}

function safeErrorMessage(error) {
  return error instanceof Error ? error.message : "未知错误";
}
