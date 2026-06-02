const state = {
  catalog: [],
  candidates: [],
  autoResolveTimer: 0,
  operationSyncTimer: 0,
  isRequestTextComposing: false,
  resolveSequence: 0,
  isExecuting: false,
  latestResponseText: "",
};

const elements = {
  overviewCards: document.getElementById("overviewCards"),
  baseUrl: document.getElementById("baseUrl"),
  accessToken: document.getElementById("accessToken"),
  proxyUrl: document.getElementById("proxyUrl"),
  timeoutSeconds: document.getElementById("timeoutSeconds"),
  proxyMode: document.getElementById("proxyMode"),
  requestText: document.getElementById("requestText"),
  resolveButton: document.getElementById("resolveButton"),
  refreshButton: document.getElementById("refreshButton"),
  candidateMeta: document.getElementById("candidateMeta"),
  candidates: document.getElementById("candidates"),
  operationId: document.getElementById("operationId"),
  method: document.getElementById("method"),
  path: document.getElementById("path"),
  queryParameters: document.getElementById("queryParameters"),
  contentType: document.getElementById("contentType"),
  bodyFormat: document.getElementById("bodyFormat"),
  body: document.getElementById("body"),
  generateBodyButton: document.getElementById("generateBodyButton"),
  variables: document.getElementById("variables"),
  headers: document.getElementById("headers"),
  executeButton: document.getElementById("executeButton"),
  copyResponseQueryButton: document.getElementById("copyResponseQueryButton"),
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

elements.resolveButton.addEventListener("click", () => resolveRequest());
elements.executeButton.addEventListener("click", executeRequest);
elements.copyResponseQueryButton.addEventListener("click", copyExecutionResult);
elements.generateBodyButton.addEventListener("click", generateBodyTemplate);
elements.coverageButton.addEventListener("click", buildCoveragePlan);
elements.refreshButton.addEventListener("click", refreshManuals);
elements.requestText.addEventListener("input", handleRequestTextInput);
elements.requestText.addEventListener("compositionstart", handleRequestTextCompositionStart);
elements.requestText.addEventListener("compositionend", handleRequestTextCompositionEnd);
elements.operationId.addEventListener("input", handleOperationIdInput);
elements.operationId.addEventListener("change", handleOperationIdInput);
elements.responseViewer.addEventListener("keydown", handleResponseViewerKeyDown);
elements.copyResponseQueryButton.disabled = true;

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

  const hasSearchContext =
    Boolean(elements.requestText.value.trim()) ||
    Boolean(elements.method.value.trim()) ||
    Boolean(elements.path.value.trim()) ||
    Boolean(elements.operationId.value.trim());

  if (!hasSearchContext) {
    state.candidates = [];
    renderCandidates();
    return;
  }

  const currentSequence = ++state.resolveSequence;
  const payload = {
    requestText: elements.requestText.value,
    explicitMethod: elements.method.value,
    explicitPath: elements.path.value,
    operationId: elements.operationId.value,
    top: 8,
  };

  const response = await postJson("/api/resolve", payload);
  if (currentSequence !== state.resolveSequence) {
    return;
  }

  state.candidates = response.candidates || [];
  renderCandidates();
}

function renderCandidates() {
  if (state.candidates.length === 0) {
    elements.candidates.innerHTML = '<div class="candidate"><p>候補が見つかりませんでした。Method / Path を直接入力して実行できます。</p></div>';
    elements.candidateMeta.textContent = "候補なし";
    return;
  }

  elements.candidateMeta.textContent = `${state.candidates.length} 件ヒット`;
  elements.candidates.innerHTML = state.candidates.map((candidate) => {
    const operation = candidate.operation;
    const optionalQueryKeys = Object.keys(operation.optionalQueryParameters || {});
    const optionalQueryBlock = optionalQueryKeys.length > 0
      ? `<p>${escapeHtml(`optional query: ${optionalQueryKeys.join(", ")}`)}</p>`
      : "";
    return `
      <article class="candidate">
        <header>
          <div>
            <div class="badge method">${escapeHtml(operation.method)}</div>
            <h3>${escapeHtml(operation.summary)}</h3>
          </div>
          <button data-operation-id="${escapeHtml(operation.id)}">選択</button>
        </header>
        <p>${escapeHtml(operation.path)}</p>
        <p>${escapeHtml(candidate.reasons.join(" / "))}</p>
        ${optionalQueryBlock}
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
    button.addEventListener("click", async () => {
      const candidate = state.candidates.find((item) => item.operation.id === button.dataset.operationId);
      if (!candidate) {
        return;
      }

      await applySelectedOperation(candidate.operation);
    });
  });
}

async function applySelectedOperation(operation) {
  elements.operationId.value = operation.id;
  elements.method.value = operation.method;
  elements.path.value = operation.path;
  elements.contentType.value = operation.sampleContentType || "application/json";
  elements.bodyFormat.value = "json";
  elements.body.value = operation.sampleBody || "";
  elements.queryParameters.value = JSON.stringify(operation.optionalQueryParameters || {}, null, 2);

  const plan = await planRequestBody({ overwriteBody: true });
  elements.responseViewer.textContent =
    `選択済み: ${operation.summary}\n` +
    `${operation.method} ${operation.path}\n\n` +
    formatPlanSummary(plan);
}

async function executeRequest() {
  if (state.isExecuting) {
    return;
  }

  state.latestResponseText = "";
  elements.copyResponseQueryButton.disabled = true;
  setExecuteBusy(true);

  try {
    persistLocalSettings();

    const hadBody = Boolean(elements.body.value.trim());
    const plan = await planRequestBody({ overwriteBody: false, forceGenerate: !hadBody });
    const payload = collectExecutePayload();

    if (!hadBody && plan.bodyGenerated) {
      payload.body = plan.body || "";
      payload.contentType = plan.contentType || payload.contentType;
      payload.bodyFormat = plan.bodyFormat || payload.bodyFormat;
    }

    const response = await postJson("/api/execute", payload);
    renderExecutionResult(response);

    if (!hadBody && plan.bodyGenerated) {
      elements.body.value = plan.body || "";
    }
  } finally {
    setExecuteBusy(false);
  }
}

async function generateBodyTemplate() {
  persistLocalSettings();
  const plan = await planRequestBody({ overwriteBody: true, forceGenerate: true });
  elements.responseViewer.textContent =
    `Body generated for:\n${elements.method.value || "(method)"} ${elements.path.value || "(path)"}\n\n` +
    formatPlanSummary(plan);
  state.latestResponseText = "";
  elements.copyResponseQueryButton.disabled = true;
}

function renderExecutionResult(response) {

  const statusLabel = response.statusCode > 0 ? String(response.statusCode) : (response.errorType || "error");
  const hasSuccessExample = Boolean(response.successExample && response.successExample.body && !response.isSuccessStatusCode);
  elements.executeMeta.textContent =
    `${statusLabel} ${response.isSuccessStatusCode ? "success" : "error"} / ${response.elapsedMilliseconds} ms` +
    (hasSuccessExample ? " / success example available" : "");

  const requestMetaLines = [
    response.requestContentType ? `Content-Type: ${response.requestContentType}` : "",
    response.requestBodyFormat ? `Body format: ${response.requestBodyFormat}` : "",
    response.proxyMode ? `Proxy mode: ${response.proxyMode}` : "",
    response.proxyUrl ? `Proxy URL: ${response.proxyUrl}` : "",
    response.bodySource && response.bodySource !== "none" ? `Body source: ${response.bodySource}` : "",
  ].filter(Boolean);

  const requestBlock = response.requestDebugText
    ? `Request:\n${response.requestDebugText}\n\n`
    : "";
  const requestMetaBlock = requestMetaLines.length > 0
    ? `${requestMetaLines.join("\n")}\n\n`
    : "";
  const requestParametersBlock = formatRequestParametersBlock(response);
  const errorBlock = response.errorMessage
    ? `Error: ${response.errorMessage}\n\n`
    : "";
  const successExampleBlock = hasSuccessExample
    ? formatSuccessExample(response.successExample)
    : "";
  const noteBlock = Array.isArray(response.notes) && response.notes.length > 0
    ? `Notes:\n- ${response.notes.join("\n- ")}\n\n`
    : "";
  const headersBlock = response.responseHeaders && Object.keys(response.responseHeaders).length > 0
    ? `Response headers:\n${JSON.stringify(response.responseHeaders, null, 2)}\n\n`
    : "";
  const bodyBlock = response.responseBody || "(empty response body)";

  elements.responseViewer.textContent =
    `${requestBlock}` +
    `${requestMetaBlock}` +
    `${requestParametersBlock}` +
    `${errorBlock}` +
    `${successExampleBlock}` +
    `${noteBlock}` +
    `${headersBlock}` +
    `${bodyBlock}`;

  state.latestResponseText = elements.responseViewer.textContent || "";
  elements.copyResponseQueryButton.disabled = !state.latestResponseText.trim();
  elements.responseViewer.focus();
}

function formatSuccessExample(successExample) {
  const metaLines = [
    `Successful response example: ${successExample.statusCode || 200}`,
    successExample.contentType ? `Content-Type: ${successExample.contentType}` : "",
    successExample.source ? `Source: ${successExample.source}` : "",
  ].filter(Boolean);

  const notesBlock = Array.isArray(successExample.notes) && successExample.notes.length > 0
    ? `Notes:\n- ${successExample.notes.join("\n- ")}\n\n`
    : "";

  return `${metaLines.join("\n")}\n\n${notesBlock}${successExample.body}\n\n`;
}

function formatRequestParametersBlock(response) {
  const sections = [];

  if (response.requestQueryParameters && Object.keys(response.requestQueryParameters).length > 0) {
    sections.push(`Query parameters:\n${JSON.stringify(response.requestQueryParameters, null, 2)}`);
  }

  if (response.requestVariables && Object.keys(response.requestVariables).length > 0) {
    sections.push(`Variables:\n${JSON.stringify(response.requestVariables, null, 2)}`);
  }

  if (response.requestCustomHeaders && Object.keys(response.requestCustomHeaders).length > 0) {
    sections.push(`Custom headers:\n${JSON.stringify(response.requestCustomHeaders, null, 2)}`);
  }

  if (sections.length === 0) {
    return "";
  }

  return `Request parameters:\n${sections.join("\n\n")}\n\n`;
}

function setExecuteBusy(isBusy) {
  state.isExecuting = isBusy;
  elements.executeButton.disabled = isBusy;
}

async function copyExecutionResult() {
  if (!state.latestResponseText) {
    return;
  }

  try {
    await copyTextToClipboard(state.latestResponseText);
    if (!elements.executeMeta.textContent.includes("result copied")) {
      elements.executeMeta.textContent = `${elements.executeMeta.textContent} / result copied`;
    }
  } catch (error) {
    elements.responseViewer.textContent = formatError(error);
  }
}

function handleResponseViewerKeyDown(event) {
  const isSelectAll = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "a";
  if (!isSelectAll) {
    return;
  }

  event.preventDefault();
  selectElementText(elements.responseViewer);
}

function selectElementText(element) {
  const selection = window.getSelection();
  if (!selection) {
    return;
  }

  const range = document.createRange();
  range.selectNodeContents(element);
  selection.removeAllRanges();
  selection.addRange(range);
}

async function copyTextToClipboard(text) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.setAttribute("readonly", "");
  textarea.style.position = "fixed";
  textarea.style.top = "-9999px";
  textarea.style.left = "-9999px";
  document.body.appendChild(textarea);
  textarea.focus();
  textarea.select();

  try {
    const copied = document.execCommand("copy");
    if (!copied) {
      throw new Error("Clipboard copy command failed.");
    }
  } finally {
    document.body.removeChild(textarea);
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
      <p>${escapeHtml(item.reasons.join(" / ") || "理由なし")}</p>
    </article>
  `).join("");
}

async function refreshManuals() {
  await postJson("/api/admin/refresh-manuals", {});
  await Promise.all([loadOverview(), loadCatalog()]);
  elements.responseViewer.textContent = "マニュアルカタログを再取得しました。";
  state.latestResponseText = "";
  elements.copyResponseQueryButton.disabled = true;
}

async function planRequestBody({ overwriteBody, forceGenerate = false }) {
  const payload = collectBodyPlanPayload({ forceGenerate });
  const response = await postJson("/api/body-plan", payload);

  elements.contentType.value = response.contentType || elements.contentType.value;
  elements.bodyFormat.value = response.bodyFormat || elements.bodyFormat.value;

  if (overwriteBody || !elements.body.value.trim()) {
    elements.body.value = response.body || "";
  }

  return response;
}

function collectBodyPlanPayload({ forceGenerate = false } = {}) {
  return {
    operationId: elements.operationId.value || null,
    requestText: elements.requestText.value,
    baseUrl: elements.baseUrl.value,
    method: elements.method.value || null,
    path: elements.path.value || null,
    contentType: elements.contentType.value || null,
    bodyFormat: elements.bodyFormat.value,
    body: forceGenerate ? "" : elements.body.value,
    variables: parseStringMapField(elements.variables.value, "Variables JSON"),
  };
}

function collectExecutePayload() {
  const timeoutSeconds = Number.parseInt(elements.timeoutSeconds.value, 10);
  const proxyMode = elements.proxyMode.value || "system";
  const isProxyDisabled = proxyMode === "disabled";
  const explicitProxy = proxyMode === "explicit" ? elements.proxyUrl.value.trim() : "";

  return {
    operationId: elements.operationId.value || null,
    requestText: elements.requestText.value,
    baseUrl: elements.baseUrl.value,
    accessToken: elements.accessToken.value,
    method: elements.method.value || null,
    path: elements.path.value || null,
    contentType: elements.contentType.value || null,
    bodyFormat: elements.bodyFormat.value,
    body: elements.body.value,
    proxyUrl: explicitProxy || null,
    bypassSystemProxy: isProxyDisabled,
    useDefaultProxyCredentials: true,
    timeoutSeconds: Number.isFinite(timeoutSeconds) ? timeoutSeconds : 30,
    variables: parseStringMapField(elements.variables.value, "Variables JSON"),
    queryParameters: parseStringMapField(elements.queryParameters.value, "Query params JSON"),
    headers: parseStringMapField(elements.headers.value, "Headers JSON"),
  };
}

function formatPlanSummary(plan) {
  const lines = [
    `Content-Type: ${plan.contentType || "(none)"}`,
    `Body format: ${plan.bodyFormat || "(none)"}`,
    `Body required: ${plan.bodyRequired ? "yes" : "no"}`,
    `Body source: ${plan.bodySource || "none"}`,
  ];

  if (Array.isArray(plan.notes) && plan.notes.length > 0) {
    lines.push("", "Notes:");
    plan.notes.forEach((note) => lines.push(`- ${note}`));
  }

  if (plan.body) {
    lines.push("", "Body template:", plan.body);
  }

  return lines.join("\n");
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

function parseStringMapField(raw, label) {
  const parsed = parseJsonField(raw, label);
  if (parsed === null || Array.isArray(parsed) || typeof parsed !== "object") {
    throw new Error(`${label} は JSON オブジェクトで指定してください`);
  }

  const result = {};
  Object.entries(parsed).forEach(([key, value]) => {
    if (!key || value === undefined || value === null) {
      return;
    }

    result[key] = typeof value === "string" ? value : String(value);
  });
  return result;
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
  elements.proxyUrl.value = localStorage.getItem("iij.proxyUrl") || "";
  elements.timeoutSeconds.value = localStorage.getItem("iij.timeoutSeconds") || "30";
  elements.proxyMode.value = localStorage.getItem("iij.proxyMode") || "system";
  elements.variables.value = localStorage.getItem("iij.variables") || "{}";
  elements.queryParameters.value = localStorage.getItem("iij.queryParameters") || "{}";
  elements.headers.value = localStorage.getItem("iij.headers") || "{}";
}

function persistLocalSettings() {
  localStorage.setItem("iij.baseUrl", elements.baseUrl.value);
  localStorage.setItem("iij.accessToken", elements.accessToken.value);
  localStorage.setItem("iij.proxyUrl", elements.proxyUrl.value);
  localStorage.setItem("iij.timeoutSeconds", elements.timeoutSeconds.value);
  localStorage.setItem("iij.proxyMode", elements.proxyMode.value);
  localStorage.setItem("iij.variables", elements.variables.value);
  localStorage.setItem("iij.queryParameters", elements.queryParameters.value);
  localStorage.setItem("iij.headers", elements.headers.value);
}

function handleRequestTextInput(event) {
  if (event.isComposing || state.isRequestTextComposing) {
    return;
  }

  scheduleAutoResolve();
}

function handleRequestTextCompositionStart() {
  state.isRequestTextComposing = true;
  cancelAutoResolve();
}

function handleRequestTextCompositionEnd() {
  state.isRequestTextComposing = false;
  scheduleAutoResolve();
}

function handleOperationIdInput() {
  scheduleOperationSync();
}

function scheduleAutoResolve() {
  cancelAutoResolve();
  state.autoResolveTimer = window.setTimeout(async () => {
    try {
      await resolveRequest();
    } catch (error) {
      elements.candidateMeta.textContent = "候補検索エラー";
      elements.responseViewer.textContent = formatError(error);
    }
  }, 400);
}

function scheduleOperationSync() {
  cancelOperationSync();
  state.operationSyncTimer = window.setTimeout(async () => {
    try {
      await applyOperationFromOperationId();
    } catch (error) {
      elements.responseViewer.textContent = formatError(error);
    }
  }, 200);
}

function cancelAutoResolve() {
  if (state.autoResolveTimer) {
    window.clearTimeout(state.autoResolveTimer);
    state.autoResolveTimer = 0;
  }
}

function cancelOperationSync() {
  if (state.operationSyncTimer) {
    window.clearTimeout(state.operationSyncTimer);
    state.operationSyncTimer = 0;
  }
}

async function applyOperationFromOperationId() {
  const typedOperationId = elements.operationId.value.trim();
  if (!typedOperationId) {
    return;
  }

  const operation = state.catalog.find((item) => item.id.localeCompare(typedOperationId, undefined, { sensitivity: "accent" }) === 0);
  if (!operation) {
    return;
  }

  await applySelectedOperation(operation);
  await resolveRequest();
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
