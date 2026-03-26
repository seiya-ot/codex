const state = {
  catalog: [],
  candidates: [],
};

const elements = {
  overviewCards: document.getElementById("overviewCards"),
  baseUrl: document.getElementById("baseUrl"),
  accessToken: document.getElementById("accessToken"),
  requestText: document.getElementById("requestText"),
  resolveButton: document.getElementById("resolveButton"),
  refreshButton: document.getElementById("refreshButton"),
  candidateMeta: document.getElementById("candidateMeta"),
  candidates: document.getElementById("candidates"),
  operationId: document.getElementById("operationId"),
  method: document.getElementById("method"),
  path: document.getElementById("path"),
  contentType: document.getElementById("contentType"),
  bodyFormat: document.getElementById("bodyFormat"),
  body: document.getElementById("body"),
  variables: document.getElementById("variables"),
  headers: document.getElementById("headers"),
  executeButton: document.getElementById("executeButton"),
  executeMeta: document.getElementById("executeMeta"),
  responseViewer: document.getElementById("responseViewer"),
  coverageButton: document.getElementById("coverageButton"),
  coverageMeta: document.getElementById("coverageMeta"),
  coverageSummary: document.getElementById("coverageSummary"),
  coverageItems: document.getElementById("coverageItems"),
};

bootstrap().catch((error) => {
  elements.responseViewer.textContent = formatError(error);
});

elements.resolveButton.addEventListener("click", resolveRequest);
elements.executeButton.addEventListener("click", executeRequest);
elements.coverageButton.addEventListener("click", buildCoveragePlan);
elements.refreshButton.addEventListener("click", refreshManuals);

async function bootstrap() {
  loadLocalSettings();
  await Promise.all([loadOverview(), loadCatalog()]);
}

async function loadOverview() {
  const overview = await fetchJson("/api/overview");
  const cards = [
    ["manual pages", overview.totalManualPages],
    ["api operations", overview.totalOperations],
    ["generated", new Date(overview.generatedAtUtc).toLocaleString("ja-JP")],
  ];

  Object.entries(overview.pageCounts || {}).forEach(([label, count]) => cards.push([label, count]));
  elements.overviewCards.innerHTML = cards.slice(0, 6).map(([label, value]) => `
    <article class="stat-card">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(String(value))}</strong>
    </article>
  `).join("");
}

async function loadCatalog() {
  state.catalog = await fetchJson("/api/catalog");
  elements.candidateMeta.textContent = `${state.catalog.length} 件の API を読み込み済み`;
}

async function resolveRequest() {
  persistLocalSettings();

  const payload = {
    requestText: elements.requestText.value,
    explicitMethod: elements.method.value,
    explicitPath: elements.path.value,
    operationId: elements.operationId.value,
    top: 8,
  };

  const response = await postJson("/api/resolve", payload);
  state.candidates = response.candidates || [];
  renderCandidates();
}

function renderCandidates() {
  if (state.candidates.length === 0) {
    elements.candidates.innerHTML = '<div class="candidate"><p>候補が見つかりませんでした。Method / Path を直接入力してください。</p></div>';
    return;
  }

  elements.candidateMeta.textContent = `${state.candidates.length} 件ヒット`;
  elements.candidates.innerHTML = state.candidates.map((candidate) => {
    const operation = candidate.operation;
    return `
      <article class="candidate">
        <header>
          <div>
            <div class="badge method">${escapeHtml(operation.method)}</div>
            <h3>${escapeHtml(operation.summary)}</h3>
          </div>
          <button data-operation-id="${escapeHtml(operation.id)}">使う</button>
        </header>
        <p>${escapeHtml(operation.path)}</p>
        <p>${escapeHtml(candidate.reasons.join(" / "))}</p>
        <div class="meta">
          <span class="badge">${escapeHtml(operation.manualName)}</span>
          <span class="badge">${escapeHtml(operation.category)}</span>
          <span class="badge">score ${escapeHtml(String(candidate.score))}</span>
          <a class="badge" href="${escapeAttribute(operation.sourcePageUrl)}" target="_blank" rel="noreferrer">manual</a>
        </div>
      </article>
    `;
  }).join("");

  elements.candidates.querySelectorAll("button[data-operation-id]").forEach((button) => {
    button.addEventListener("click", () => {
      const candidate = state.candidates.find((item) => item.operation.id === button.dataset.operationId);
      if (!candidate) {
        return;
      }

      const operation = candidate.operation;
      elements.operationId.value = operation.id;
      elements.method.value = operation.method;
      elements.path.value = operation.path;
      elements.contentType.value = operation.sampleContentType || "application/json";
      elements.body.value = operation.sampleBody || "";
      elements.responseViewer.textContent = `選択済み: ${operation.summary}\n${operation.method} ${operation.path}`;
    });
  });
}

async function executeRequest() {
  persistLocalSettings();

  const payload = {
    operationId: elements.operationId.value || null,
    requestText: elements.requestText.value,
    baseUrl: elements.baseUrl.value,
    accessToken: elements.accessToken.value,
    method: elements.method.value || null,
    path: elements.path.value || null,
    contentType: elements.contentType.value || null,
    bodyFormat: elements.bodyFormat.value,
    body: elements.body.value,
    variables: parseJsonField(elements.variables.value, "Variables JSON"),
    headers: parseJsonField(elements.headers.value, "Headers JSON"),
  };

  try {
    const response = await postJson("/api/execute", payload);
    const statusLabel = response.statusCode > 0 ? String(response.statusCode) : (response.errorType || "error");
    elements.executeMeta.textContent = `${statusLabel} ${response.isSuccessStatusCode ? "success" : "error"} / ${response.elapsedMilliseconds} ms`;

    const noteBlock = Array.isArray(response.notes) && response.notes.length > 0
      ? `Notes:\n- ${response.notes.join("\n- ")}\n\n`
      : "";
    const errorBlock = response.errorMessage
      ? `Error: ${response.errorMessage}\n\n`
      : "";
    const headersBlock = response.responseHeaders && Object.keys(response.responseHeaders).length > 0
      ? `${JSON.stringify(response.responseHeaders, null, 2)}\n\n`
      : "";
    const bodyBlock = response.responseBody || "(empty response body)";

    elements.responseViewer.textContent =
      `${response.method} ${response.finalUrl}\n\n` +
      `${errorBlock}` +
      `${noteBlock}` +
      `${headersBlock}` +
      `${bodyBlock}`;
  } catch (error) {
    elements.executeMeta.textContent = "error";
    elements.responseViewer.textContent = formatError(error);
  }
}

async function buildCoveragePlan() {
  const payload = {
    operationIds: state.candidates.length > 0 ? state.candidates.map((candidate) => candidate.operation.id) : null,
    variables: parseJsonField(elements.variables.value, "Variables JSON"),
  };

  const response = await postJson("/api/coverage-plan", payload);
  elements.coverageMeta.textContent = `${response.totalOperations} 件を評価`;
  elements.coverageSummary.innerHTML = `
    <span class="coverage-pill ready">ready ${response.readyCount}</span>
    <span class="coverage-pill needs_input">needs_input ${response.needsInputCount}</span>
    <span class="coverage-pill manual_fixture">manual_fixture ${response.manualFixtureCount}</span>
  `;

  elements.coverageItems.innerHTML = response.items.map((item) => `
    <article class="coverage-item">
      <header>
        <div>
          <div class="badge method">${escapeHtml(item.method)}</div>
          <h3>${escapeHtml(item.summary)}</h3>
        </div>
        <span class="coverage-pill ${escapeHtml(item.status)}">${escapeHtml(item.status)}</span>
      </header>
      <p>${escapeHtml(item.path)}</p>
      <p>${escapeHtml(item.reasons.join(" / ") || "必要条件は揃っています。")}</p>
    </article>
  `).join("");
}

async function refreshManuals() {
  await postJson("/api/admin/refresh-manuals", {});
  await Promise.all([loadOverview(), loadCatalog()]);
  elements.responseViewer.textContent = "マニュアルカタログを再構築しました。";
}

function parseJsonField(raw, label) {
  if (!raw.trim()) {
    return {};
  }

  try {
    return JSON.parse(raw);
  } catch (error) {
    throw new Error(`${label} の JSON が不正です: ${error.message}`);
  }
}

async function fetchJson(url) {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`${url} failed: ${response.status}`);
  }

  return response.json();
}

async function postJson(url, payload) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`${url} failed: ${response.status}\n${text}`);
  }

  return response.json();
}

function loadLocalSettings() {
  elements.baseUrl.value = localStorage.getItem("iij.baseUrl") || "";
  elements.accessToken.value = localStorage.getItem("iij.accessToken") || "";
  elements.variables.value = localStorage.getItem("iij.variables") || "{}";
  elements.headers.value = localStorage.getItem("iij.headers") || "{}";
}

function persistLocalSettings() {
  localStorage.setItem("iij.baseUrl", elements.baseUrl.value);
  localStorage.setItem("iij.accessToken", elements.accessToken.value);
  localStorage.setItem("iij.variables", elements.variables.value);
  localStorage.setItem("iij.headers", elements.headers.value);
}

function formatError(error) {
  return error instanceof Error ? error.stack || error.message : String(error);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replaceAll("'", "&#39;");
}
