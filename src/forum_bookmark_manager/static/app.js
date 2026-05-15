const filters = {
  status: "",
  type: "",
  q: "",
  sort: "new",
};

const postsEl = document.querySelector("#posts");
const summaryEl = document.querySelector("#summary");
const progressEl = document.querySelector("#progress");
const selectedCountEl = document.querySelector("#selected-count");
const selectAllButton = document.querySelector("#select-all");
const clearSelectionButton = document.querySelector("#clear-selection");
const bulkBarEl = document.querySelector("#bulk-bar");
const headlessTabsEl = document.querySelector("#headless-tabs");
const headlessTabCountEl = document.querySelector("#headless-tab-count");
const headlessDialog = document.querySelector("#headless-dialog");
const headlessTabsDetailEl = document.querySelector("#headless-tabs-detail");
const headlessDialogSummaryEl = document.querySelector("#headless-dialog-summary");
const closeAllHeadlessTabsButton = document.querySelector("#close-all-headless-tabs");
const crawlDialog = document.querySelector("#crawl-dialog");
const crawlStartUrl = document.querySelector("#crawl-start-url");
const crawlMirrorUrl = document.querySelector("#crawl-mirror-url");
const crawlMode = document.querySelector("#crawl-mode");
const annotateDialog = document.querySelector("#annotate-dialog");
const annotateTargetUrl = document.querySelector("#annotate-target-url");
const clashStateEl = document.querySelector("#clash-state");
const clashPanelEl = document.querySelector("#clash-panel");
const clashSwitchButton = document.querySelector("#clash-switch");
const imageDialog = document.querySelector("#image-dialog");
const viewerImage = document.querySelector("#viewer-image");
const imageTitleEl = document.querySelector("#image-title");
const imageCounterEl = document.querySelector("#image-counter");
const imageFilmstripEl = document.querySelector("#image-filmstrip");
const imagePrevButton = document.querySelector("#image-prev");
const imageNextButton = document.querySelector("#image-next");
const imageCloseButton = document.querySelector("#image-close");
const selectedPostIds = new Set();
let currentPostIds = [];
let lastHeadlessTabs = [];
let lastPosts = [];
let imageViewerPost = null;
let imageViewerIndex = 0;

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function params() {
  const query = new URLSearchParams();
  if (filters.status) query.set("status", filters.status);
  if (filters.type) query.set("type", filters.type);
  if (filters.q) query.set("q", filters.q);
  query.set("sort", filters.sort);
  return query.toString();
}

function selectedIds() {
  return Array.from(selectedPostIds).map((id) => Number(id));
}

function pruneSelectionToCurrentPosts() {
  const visibleIds = new Set(currentPostIds);
  for (const id of Array.from(selectedPostIds)) {
    if (!visibleIds.has(id)) {
      selectedPostIds.delete(id);
    }
  }
}

function updateSelectionUi() {
  const selectedCount = selectedPostIds.size;
  selectedCountEl.textContent = `已选 ${selectedCount} 项`;
  bulkBarEl.classList.toggle("has-selection", selectedCount > 0);
  const hasVisiblePosts = currentPostIds.length > 0;
  const allVisibleSelected = hasVisiblePosts && currentPostIds.every((id) => selectedPostIds.has(id));
  selectAllButton.textContent = allVisibleSelected ? "取消全选当前筛选" : "一键全选当前筛选";
  selectAllButton.disabled = !hasVisiblePosts;
  clearSelectionButton.disabled = selectedCount === 0;
  document.querySelectorAll("[data-bulk-status], #bulk-delete").forEach((button) => {
    button.disabled = selectedCount === 0;
  });
  document.querySelectorAll(".post-select").forEach((checkbox) => {
    checkbox.checked = selectedPostIds.has(checkbox.dataset.id);
  });
}

function clearSelection() {
  selectedPostIds.clear();
  updateSelectionUi();
}

async function loadTypes() {
  const response = await fetch("/api/types");
  const data = await response.json();
  const container = document.querySelector("#type-filters");
  const existing = container.querySelector("[data-value='']");
  container.innerHTML = "";
  container.append(existing);
  for (const type of data.types) {
    const button = document.createElement("button");
    button.dataset.value = type;
    button.textContent = type;
    container.append(button);
  }
}

async function loadPosts() {
  const response = await fetch(`/api/posts?${params()}`);
  const data = await response.json();
  lastPosts = data.items;
  currentPostIds = data.items.map((post) => String(post.id));
  pruneSelectionToCurrentPosts();
  summaryEl.textContent = `当前筛选：${filters.type || "全部类型"} + ${filters.status || "全部状态"}，共 ${data.total} 项`;
  postsEl.innerHTML = data.items.length
    ? data.items.map(renderPost).join("")
    : `<div class="empty-state">暂无数据。请先运行登录和爬取，或点击“开始爬取”。</div>`;
  updateSelectionUi();
}

function renderPost(post) {
  const images = post.images.length
    ? post.images.slice(0, 2).map((image, index) => {
      const src = image.local_path || image.thumbnail_path || image.source_url;
      return `
        <button class="image-preview" data-post-id="${post.id}" data-image-index="${index}" type="button">
          <img class="thumb" src="${escapeHtml(src)}" alt="帖子图片 ${index + 1}">
        </button>
      `;
    }).join("")
    : `<div class="empty-thumb large-empty-thumb">无图</div>`;
  const imageMore = post.images.length > 2
    ? `<button class="image-more" data-post-id="${post.id}" data-image-index="2" type="button">+${post.images.length - 2} 张</button>`
    : "";

  const links = post.download_links.length
    ? post.download_links.map((link) => `
      <div>
        <a class="original-link" href="${escapeHtml(link.url)}" target="_blank" rel="noreferrer">${escapeHtml(link.label || link.url)}</a>
        <span>${escapeHtml(link.context_text)}</span>
      </div>
    `).join("")
    : "未提取到下载链接";

  return `
    <article class="post-card" data-id="${post.id}">
      <label class="select-cell" title="选择帖子">
        <input class="post-select" type="checkbox" data-id="${post.id}" aria-label="选择 ${escapeHtml(post.title)}">
      </label>
      <div class="thumbs">${images}${imageMore}</div>
      <div class="post-main">
        <h2 class="post-title">${escapeHtml(post.title)}</h2>
        <p class="meta">
          <span class="tag">${escapeHtml(post.project_type)}</span>
          <span class="tag">${escapeHtml(post.favorite_time || "无收藏时间")}</span>
          <span class="tag">下载 ${post.download_count ?? "-"}</span>
          <span class="tag">访问 ${post.visit_count ?? "-"}</span>
          <span class="tag">收藏 ${post.favorite_count ?? "-"}</span>
        </p>
        <p class="body-text">${escapeHtml((post.body_text || "").slice(0, 260))}</p>
        <a class="original-link" href="${escapeHtml(post.post_url)}" target="_blank" rel="noreferrer">打开原帖</a>
      </div>
      <div class="post-downloads">
        <p class="meta password-line">解压密码：${escapeHtml(post.extract_password || "未提取")}</p>
        <div class="links">${links}</div>
      </div>
      <div class="item-actions">
        <button class="state-button" data-id="${post.id}" data-status="${escapeHtml(post.status)}">${escapeHtml(post.status)}</button>
        <button class="delete-button" data-id="${post.id}" data-title="${escapeHtml(post.title)}">删除帖子</button>
      </div>
    </article>
  `;
}

function imageSource(image, preferFull = false) {
  if (!image) return "";
  if (preferFull) {
    return image.local_path || image.source_url || image.thumbnail_path || "";
  }
  return image.local_path || image.thumbnail_path || image.source_url || "";
}

function openImageViewer(postId, index = 0) {
  imageViewerPost = lastPosts.find((post) => String(post.id) === String(postId));
  if (!imageViewerPost || !imageViewerPost.images.length) {
    return;
  }
  imageViewerIndex = Math.max(0, Math.min(Number(index) || 0, imageViewerPost.images.length - 1));
  imageDialog.classList.remove("zoomed");
  renderImageViewer();
  imageDialog.showModal();
}

function renderImageViewer() {
  if (!imageViewerPost) {
    return;
  }
  const images = imageViewerPost.images;
  const image = images[imageViewerIndex];
  viewerImage.src = imageSource(image, true);
  imageTitleEl.textContent = imageViewerPost.title;
  imageCounterEl.textContent = `${imageViewerIndex + 1} / ${images.length}`;
  imagePrevButton.disabled = imageViewerIndex <= 0;
  imageNextButton.disabled = imageViewerIndex >= images.length - 1;
  imageFilmstripEl.innerHTML = images.map((item, index) => `
    <button class="${index === imageViewerIndex ? "active" : ""}" data-image-index="${index}" type="button">
      <img src="${escapeHtml(imageSource(item))}" alt="图片 ${index + 1}">
    </button>
  `).join("");
}

function moveImageViewer(delta) {
  if (!imageViewerPost) {
    return;
  }
  imageViewerIndex = Math.max(0, Math.min(imageViewerIndex + delta, imageViewerPost.images.length - 1));
  imageDialog.classList.remove("zoomed");
  renderImageViewer();
}

async function loadProgress() {
  const response = await fetch("/api/progress");
  const data = await response.json();
  const message = data.message ? `；信息：${data.message}` : "";
  progressEl.textContent = `状态：${data.status}；进度：${data.processed_posts}/${data.total_favorites}；成功：${data.successful_posts}；失败：${data.failed_posts}${message}`;
}

async function loadClashStatus() {
  try {
    const response = await fetch("/api/clash");
    const data = await response.json();
    renderClashStatus(data);
  } catch (_error) {
    clashStateEl.textContent = "不可用";
    clashPanelEl.textContent = "无法读取 ClashVerge 状态";
    clashSwitchButton.disabled = true;
  }
}

function renderClashStatus(data) {
  const reachable = data.reachable === true;
  clashStateEl.textContent = reachable ? "已连接" : (data.configured ? "未响应" : "未配置");
  clashSwitchButton.disabled = data.configured !== true;
  clashPanelEl.innerHTML = `
    <div>Controller：${escapeHtml(data.controller_url || "-")}</div>
    <div>代理组：${escapeHtml(data.proxy_group || "-")}</div>
    <div>当前线路：${escapeHtml(data.current || "-")}</div>
    <div>候选线路：${escapeHtml(data.candidate_count ?? 0)} 条</div>
    <div>状态：${escapeHtml(data.message || "-")}</div>
  `;
}

async function loadHeadlessTabs() {
  const response = await fetch("/api/headless-tabs");
  const data = await response.json();
  lastHeadlessTabs = data.tabs || [];
  headlessTabCountEl.textContent = `${data.total} 个`;
  headlessTabsEl.innerHTML = lastHeadlessTabs.length
    ? lastHeadlessTabs.map((tab) => renderHeadlessTab(tab, { compact: true })).join("")
    : "暂无无头标签页";
  renderHeadlessTabManager(data.total);
}

function renderHeadlessTab(tab, options = {}) {
  const compact = options.compact === true;
  return `
    <div class="headless-tab ${compact ? "compact-tab" : ""}">
      <div class="headless-tab-main">
        <div class="headless-tab-title">${escapeHtml(tab.role)} · ${escapeHtml(tab.label)}</div>
        <div class="headless-tab-status">${escapeHtml(tab.status)}</div>
        <div class="headless-tab-url">${escapeHtml(tab.url || "about:blank")}</div>
        ${compact ? "" : `
          <div class="headless-tab-times">
            <span>创建：${escapeHtml(formatDateTime(tab.created_at))}</span>
            <span>更新：${escapeHtml(formatDateTime(tab.updated_at))}</span>
          </div>
        `}
      </div>
      <div class="headless-tab-actions">
        <button class="tab-open-button" data-id="${tab.id}">打开可见</button>
        <button class="tab-copy-button" data-url="${escapeHtml(tab.url || "")}">复制链接</button>
        <button class="tab-close-button" data-id="${tab.id}">关闭</button>
      </div>
    </div>
  `;
}

function renderHeadlessTabManager(total = lastHeadlessTabs.length) {
  if (!headlessDialogSummaryEl || !headlessTabsDetailEl) {
    return;
  }
  headlessDialogSummaryEl.textContent = `当前 ${total} 个无头标签页`;
  closeAllHeadlessTabsButton.disabled = lastHeadlessTabs.length === 0;
  headlessTabsDetailEl.innerHTML = lastHeadlessTabs.length
    ? lastHeadlessTabs.map((tab) => renderHeadlessTab(tab)).join("")
    : `<div class="empty-state small-empty">暂无无头标签页</div>`;
}

function formatDateTime(value) {
  if (!value) return "-";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString("zh-CN", { hour12: false });
}

async function copyText(value) {
  if (!value) {
    return false;
  }
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(value);
    return true;
  }
  const textarea = document.createElement("textarea");
  textarea.value = value;
  textarea.style.position = "fixed";
  textarea.style.left = "-9999px";
  document.body.append(textarea);
  textarea.select();
  const copied = document.execCommand("copy");
  textarea.remove();
  return copied;
}

document.body.addEventListener("click", async (event) => {
  const imageButton = event.target.closest(".image-preview, .image-more");
  if (imageButton) {
    openImageViewer(imageButton.dataset.postId, imageButton.dataset.imageIndex);
    return;
  }

  const filmstripButton = event.target.closest("#image-filmstrip button");
  if (filmstripButton) {
    imageViewerIndex = Number(filmstripButton.dataset.imageIndex) || 0;
    imageDialog.classList.remove("zoomed");
    renderImageViewer();
    return;
  }

  const filterButton = event.target.closest("[data-group] button");
  if (filterButton) {
    const group = filterButton.closest("[data-group]");
    group.querySelectorAll("button").forEach((button) => button.classList.remove("active"));
    filterButton.classList.add("active");
    filters[group.dataset.group] = filterButton.dataset.value;
    await loadPosts();
    return;
  }

  const stateButton = event.target.closest(".state-button");
  if (stateButton) {
    const response = await fetch(`/api/posts/${stateButton.dataset.id}/cycle-status`, { method: "POST" });
    const data = await response.json();
    stateButton.dataset.status = data.status;
    stateButton.textContent = data.status;
    await loadPosts();
    return;
  }

  const deleteButton = event.target.closest(".delete-button");
  if (deleteButton) {
    const title = deleteButton.dataset.title || "该帖子";
    if (!confirm(`确定要删除“${title}”吗？此操作会从本地数据库移除该帖子。`)) {
      return;
    }
    const response = await fetch(`/api/posts/${deleteButton.dataset.id}`, { method: "DELETE" });
    if (!response.ok) {
      progressEl.textContent = "删除失败，请刷新后重试。";
      return;
    }
    await loadTypes();
    await loadPosts();
    return;
  }

  const tabOpenButton = event.target.closest(".tab-open-button");
  if (tabOpenButton) {
    const response = await fetch(`/api/headless-tabs/${tabOpenButton.dataset.id}/open`, { method: "POST" });
    if (!response.ok) {
      progressEl.textContent = "打开无头标签页当前地址失败，请刷新后重试。";
    }
    return;
  }

  const tabCopyButton = event.target.closest(".tab-copy-button");
  if (tabCopyButton) {
    const copied = await copyText(tabCopyButton.dataset.url || "");
    progressEl.textContent = copied ? "已复制无头标签页当前链接。" : "复制失败：该标签页还没有可复制的链接。";
    return;
  }

  const tabCloseButton = event.target.closest(".tab-close-button");
  if (tabCloseButton) {
    const response = await fetch(`/api/headless-tabs/${tabCloseButton.dataset.id}/close`, { method: "POST" });
    if (!response.ok) {
      progressEl.textContent = "关闭无头标签页失败，请刷新后重试。";
      return;
    }
    await loadHeadlessTabs();
    return;
  }
});

document.body.addEventListener("change", (event) => {
  const checkbox = event.target.closest(".post-select");
  if (!checkbox) {
    return;
  }
  if (checkbox.checked) {
    selectedPostIds.add(checkbox.dataset.id);
  } else {
    selectedPostIds.delete(checkbox.dataset.id);
  }
  updateSelectionUi();
});

selectAllButton.addEventListener("click", () => {
  const allVisibleSelected = currentPostIds.length > 0 && currentPostIds.every((id) => selectedPostIds.has(id));
  if (allVisibleSelected) {
    currentPostIds.forEach((id) => selectedPostIds.delete(id));
  } else {
    currentPostIds.forEach((id) => selectedPostIds.add(id));
  }
  updateSelectionUi();
});

clearSelectionButton.addEventListener("click", clearSelection);

document.querySelectorAll("[data-bulk-status]").forEach((button) => {
  button.addEventListener("click", async () => {
    const ids = selectedIds();
    if (!ids.length) {
      return;
    }
    const response = await fetch("/api/posts/bulk-status", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ ids, status: button.dataset.bulkStatus }),
    });
    if (!response.ok) {
      progressEl.textContent = "批量标记失败，请刷新后重试。";
      return;
    }
    const data = await response.json();
    progressEl.textContent = `已将 ${data.updated} 个帖子标为「${data.status}」。`;
    clearSelection();
    await loadPosts();
  });
});

document.querySelector("#bulk-delete").addEventListener("click", async () => {
  const ids = selectedIds();
  if (!ids.length) {
    return;
  }
  if (!confirm(`确定删除选中的 ${ids.length} 个帖子吗？此操作会从本地数据库移除这些帖子。`)) {
    return;
  }
  const response = await fetch("/api/posts/bulk-delete", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ids }),
  });
  if (!response.ok) {
    progressEl.textContent = "批量删除失败，请刷新后重试。";
    return;
  }
  const data = await response.json();
  progressEl.textContent = `已删除 ${data.deleted} 个帖子。`;
  clearSelection();
  await loadTypes();
  await loadPosts();
});

document.querySelector("#search").addEventListener("input", async (event) => {
  filters.q = event.target.value.trim();
  await loadPosts();
});

document.querySelector("#sort-new").addEventListener("click", async () => {
  filters.sort = "new";
  document.querySelector("#sort-new").classList.add("active");
  document.querySelector("#sort-old").classList.remove("active");
  await loadPosts();
});

document.querySelector("#sort-old").addEventListener("click", async () => {
  filters.sort = "old";
  document.querySelector("#sort-old").classList.add("active");
  document.querySelector("#sort-new").classList.remove("active");
  await loadPosts();
});

document.querySelector("#crawl").addEventListener("click", async () => {
  crawlDialog.showModal();
});

document.querySelector("#annotate").addEventListener("click", async () => {
  annotateDialog.showModal();
});

document.querySelector("#manage-headless-tabs").addEventListener("click", async () => {
  await loadHeadlessTabs();
  headlessDialog.showModal();
});

document.querySelector("#refresh-headless-tabs").addEventListener("click", loadHeadlessTabs);
document.querySelector("#refresh-headless-tabs-dialog").addEventListener("click", loadHeadlessTabs);

clashSwitchButton.addEventListener("click", async () => {
  clashSwitchButton.disabled = true;
  clashPanelEl.textContent = "正在切换可用线路...";
  const response = await fetch("/api/clash/switch", { method: "POST" });
  const data = await response.json();
  progressEl.textContent = data.messages?.length ? data.messages.join("；") : "ClashVerge 切换请求已完成";
  renderClashStatus(data.status || {});
});

imagePrevButton.addEventListener("click", () => moveImageViewer(-1));
imageNextButton.addEventListener("click", () => moveImageViewer(1));
imageCloseButton.addEventListener("click", () => imageDialog.close());
viewerImage.addEventListener("click", () => {
  imageDialog.classList.toggle("zoomed");
});

document.querySelector("#close-all-headless-tabs").addEventListener("click", async () => {
  if (!lastHeadlessTabs.length) {
    progressEl.textContent = "当前没有无头标签页需要关闭。";
    return;
  }
  if (!confirm(`确定关闭当前 ${lastHeadlessTabs.length} 个无头标签页吗？正在爬取的 worker 会停止当前页面。`)) {
    return;
  }
  const response = await fetch("/api/headless-tabs/close-all", { method: "POST" });
  if (!response.ok) {
    progressEl.textContent = "关闭全部无头标签页失败，请刷新后重试。";
    return;
  }
  const data = await response.json();
  progressEl.textContent = `已请求关闭 ${data.closed} 个无头标签页。`;
  await loadHeadlessTabs();
});

document.querySelector("#confirm-annotate").addEventListener("click", async (event) => {
  event.preventDefault();
  const targetUrl = annotateTargetUrl.value.trim();
  if (!targetUrl) {
    progressEl.textContent = "请输入用于标注的帖子链接。";
    return;
  }
  annotateDialog.close();
  const response = await fetch("/api/annotate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ target_url: targetUrl }),
  });
  const data = await response.json();
  progressEl.textContent = data.message || "标注窗口请求已提交。";
});

document.querySelector("#confirm-crawl").addEventListener("click", async (event) => {
  event.preventDefault();
  const startUrl = crawlStartUrl.value.trim();
  const mirrorUrl = crawlMirrorUrl.value.trim();
  if (!startUrl) {
    progressEl.textContent = "请输入论坛收藏页链接。";
    return;
  }
  crawlDialog.close();
  const response = await fetch("/api/crawl", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      mode: crawlMode.value,
      start_url: startUrl,
      mirror_url: mirrorUrl || null,
    }),
  });
  const data = await response.json();
  progressEl.textContent = data.message || "爬取请求已提交。";
});

loadTypes().then(loadPosts);
loadProgress();
loadHeadlessTabs();
loadClashStatus();
setInterval(loadProgress, 3000);
setInterval(loadHeadlessTabs, 1500);
setInterval(loadClashStatus, 5000);
