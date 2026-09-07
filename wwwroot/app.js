const state = {
  catalog: [],
  candidates: [],
  autoResolveTimer: 0,
  isRequestTextComposing: false,
  resolveSequence: 0,
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
  contentType: document.getElementById("contentType"),
  bodyFormat: document.getElementById("bodyFormat"),
  body: document.getElementById("body"),
  queryBuilder: document.getElementById("queryBuilder"),
  queryBaseDate: document.getElementById("queryBaseDate"),
  queryEntityType: document.getElementById("queryEntityType"),
  queryFrom: document.getElementById("queryFrom"),
  queryTo: document.getElementById("queryTo"),
  queryType: document.getElementById("queryType"),
  attributeQueryFields: document.getElementById("attributeQueryFields"),
  queryAttributeId: document.getElementById("queryAttributeId"),
  queryComparisonOperator: document.getElementById("queryComparisonOperator"),
  queryComparisonValue: document.getElementById("queryComparisonValue"),
  queryReferenceIds: document.getElementById("queryReferenceIds"),
  queryOnlyLatestData: document.getElementById("queryOnlyLatestData"),
  logicalQueryFields: document.getElementById("logicalQueryFields"),
  queryLogicalOperator: document.getElementById("queryLogicalOperator"),
  queryLogicalConditions: document.getElementById("queryLogicalConditions"),
  diffQueryFields: document.getElementById("diffQueryFields"),
  queryDiffAttributeId: document.getElementById("queryDiffAttributeId"),
  queryIntervalTarget: document.getElementById("queryIntervalTarget"),
  queryDiffType: document.getElementById("queryDiffType"),
  queryDiffFromOperator: document.getElementById("queryDiffFromOperator"),
  queryDiffFromValue: document.getElementById("queryDiffFromValue"),
  queryDiffToOperator: document.getElementById("queryDiffToOperator"),
  queryDiffToValue: document.getElementById("queryDiffToValue"),
  queryAttributeSelector: document.getElementById("queryAttributeSelector"),
  queryGroupAttributeSelector: document.getElementById("queryGroupAttributeSelector"),
  applyQueryBuilderButton: document.getElementById("applyQueryBuilderButton"),
  resetQueryBuilderButton: document.getElementById("resetQueryBuilderButton"),
  executeQueryButton: document.getElementById("executeQueryButton"),
  variables: document.getElementById("variables"),
  headers: document.getElementById("headers"),
  executeButton: document.getElementById("executeButton"),
  executeMeta: document.getElementById("executeMeta"),
  responseViewer: document.getElementById("responseViewer"),
  reloadErrorLogButton: document.getElementById("reloadErrorLogButton"),
  clearErrorLogButton: document.getElementById("clearErrorLogButton"),
  errorLogItems: document.getElementById("errorLogItems"),
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
elements.coverageButton.addEventListener("click", buildCoveragePlan);
elements.refreshButton.addEventListener("click", refreshManuals);
elements.reloadErrorLogButton.addEventListener("click", loadErrorLog);
elements.clearErrorLogButton.addEventListener("click", clearErrorLog);
elements.requestText.addEventListener("input", handleRequestTextInput);
elements.requestText.addEventListener("compositionstart", handleRequestTextCompositionStart);
elements.requestText.addEventListener("compositionend", handleRequestTextCompositionEnd);
elements.path.addEventListener("input", () => toggleQueryBuilder({ hydrate: false }));
elements.queryType.addEventListener("change", renderQueryTypeFields);
elements.applyQueryBuilderButton.addEventListener("click", () => applyQueryBuilder({ updateBody: true }));
elements.resetQueryBuilderButton.addEventListener("click", resetQueryBuilder);
elements.executeQueryButton.addEventListener("click", async () => {
  applyQueryBuilder({ updateBody: true });
  await executeRequest();
});

async function bootstrap() {
  loadLocalSettings();
  await Promise.all([loadOverview(), loadCatalog(), loadErrorLog()]);
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

  const plan = await planRequestBody({ overwriteBody: true });
  toggleQueryBuilder({ hydrate: true });
  elements.responseViewer.textContent =
    `選択済み: ${operation.summary}\n` +
    `${operation.method} ${operation.path}\n\n` +
    formatPlanSummary(plan);
}

async function executeRequest() {
  persistLocalSettings();

  if (isQueryEndpoint(elements.path.value)) {
    applyQueryBuilder({ updateBody: true });
  }

  const hadBody = Boolean(elements.body.value.trim());
  const plan = await planRequestBody({ overwriteBody: false });
  const payload = collectExecutePayload();

  if (!hadBody && plan.bodyGenerated) {
    payload.body = "";
    payload.contentType = plan.contentType || payload.contentType;
    payload.bodyFormat = plan.bodyFormat || payload.bodyFormat;
  }

  const response = await postJson("/api/execute", payload);
  renderExecutionResult(response);
  if (!response.isSuccessStatusCode) {
    await loadErrorLog();
  }

  if (!hadBody && plan.bodyGenerated) {
    elements.body.value = plan.body || "";
  }
}

async function loadErrorLog() {
  const entries = await fetchJson("/api/error-log");
  renderErrorLog(entries);
}

function renderErrorLog(entries) {
  if (!Array.isArray(entries) || entries.length === 0) {
    elements.errorLogItems.innerHTML = '<div class="error-log-item"><p>記録済みのエラーはありません。</p></div>';
    return;
  }

  elements.errorLogItems.innerHTML = entries.map((entry) => {
    const status = entry.statusCode > 0 ? `HTTP ${entry.statusCode}` : (entry.errorType || "error");
    const time = new Date(entry.occurredAtUtc).toLocaleString("ja-JP");
    return `
      <article class="error-log-item">
        <header>
          <div><div class="badge method">${escapeHtml(entry.method)}</div><h3>${escapeHtml(status)}: ${escapeHtml(entry.errorMessage || "実行に失敗しました")}</h3></div>
          <div class="actions"><button data-error-log-show="${escapeAttribute(entry.id)}">詳細</button><button data-error-log-delete="${escapeAttribute(entry.id)}">削除</button></div>
        </header>
        <p>${escapeHtml(entry.finalUrl || "URL を確定できませんでした")}</p>
        <p>${escapeHtml(time)} / ${escapeHtml(String(entry.elapsedMilliseconds || 0))} ms</p>
      </article>
    `;
  }).join("");

  elements.errorLogItems.querySelectorAll("button[data-error-log-show]").forEach((button) => {
    button.addEventListener("click", () => {
      const entry = entries.find((item) => item.id === button.dataset.errorLogShow);
      if (entry) {
        elements.responseViewer.textContent = formatErrorLogEntry(entry);
      }
    });
  });
  elements.errorLogItems.querySelectorAll("button[data-error-log-delete]").forEach((button) => {
    button.addEventListener("click", async () => deleteErrorLogEntry(button.dataset.errorLogDelete));
  });
}

function formatErrorLogEntry(entry) {
  const requestHeaders = Object.keys(entry.requestHeaders || {}).length > 0
    ? `Request headers:\n${JSON.stringify(entry.requestHeaders, null, 2)}\n\n` : "";
  const responseHeaders = Object.keys(entry.responseHeaders || {}).length > 0
    ? `Response headers:\n${JSON.stringify(entry.responseHeaders, null, 2)}\n\n` : "";
  const notes = Array.isArray(entry.notes) && entry.notes.length > 0
    ? `Notes:\n- ${entry.notes.join("\n- ")}\n\n` : "";
  return `Recorded: ${new Date(entry.occurredAtUtc).toLocaleString("ja-JP")}\n${entry.method} ${entry.finalUrl}\n` +
    `Status: ${entry.statusCode || entry.errorType || "error"}\nError: ${entry.errorMessage || "実行に失敗しました"}\n` +
    `Elapsed: ${entry.elapsedMilliseconds || 0} ms\n\n${requestHeaders}${responseHeaders}${notes}${entry.responseBody || "(empty response body)"}`;
}

async function deleteErrorLogEntry(id) {
  const response = await fetch(`/api/error-log/${encodeURIComponent(id)}`, { method: "DELETE" });
  if (!response.ok) {
    throw new Error(`エラー履歴の削除に失敗しました: ${response.status}`);
  }
  await loadErrorLog();
}

async function clearErrorLog() {
  const response = await fetch("/api/error-log", { method: "DELETE" });
  if (!response.ok) {
    throw new Error(`エラー履歴の削除に失敗しました: ${response.status}`);
  }
  await loadErrorLog();
}

function renderExecutionResult(response) {
  const statusLabel = response.statusCode > 0 ? String(response.statusCode) : (response.errorType || "error");
  const hasSuccessExample = Boolean(response.successExample && response.successExample.body && !response.isSuccessStatusCode);
  elements.executeMeta.textContent =
    `${statusLabel} ${response.isSuccessStatusCode ? "success" : "error"} / ${response.elapsedMilliseconds} ms` +
    (hasSuccessExample ? " / success example available" : "") +
    formatQueryResultMeta(response);

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
    `${errorBlock}` +
    `${successExampleBlock}` +
    `${noteBlock}` +
    `${headersBlock}` +
    `${bodyBlock}`;
}

function formatQueryResultMeta(response) {
  if (!isQueryEndpoint(response.finalUrl || elements.path.value) || !response.isSuccessStatusCode) {
    return "";
  }

  try {
    const result = JSON.parse(response.responseBody || "{}");
    return Array.isArray(result.results) ? ` / ${result.results.length} results` : "";
  } catch {
    return "";
  }
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
}

async function planRequestBody({ overwriteBody }) {
  const payload = collectBodyPlanPayload();
  const response = await postJson("/api/body-plan", payload);

  elements.contentType.value = response.contentType || elements.contentType.value;
  elements.bodyFormat.value = response.bodyFormat || elements.bodyFormat.value;

  if (overwriteBody || !elements.body.value.trim()) {
    elements.body.value = response.body || "";
  }

  return response;
}

function collectBodyPlanPayload() {
  return {
    operationId: elements.operationId.value || null,
    requestText: elements.requestText.value,
    baseUrl: elements.baseUrl.value,
    method: elements.method.value || null,
    path: elements.path.value || null,
    contentType: elements.contentType.value || null,
    bodyFormat: elements.bodyFormat.value,
    body: elements.body.value,
    variables: parseJsonField(elements.variables.value, "Variables JSON"),
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
    variables: parseJsonField(elements.variables.value, "Variables JSON"),
    headers: parseJsonField(elements.headers.value, "Headers JSON"),
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

function isQueryEndpoint(path) {
  return /^\/api\/v\d+\.\d+\/query(?:[/?#]|$)/i.test(String(path).trim()) ||
    /\/api\/v\d+\.\d+\/query(?:[/?#]|$)/i.test(String(path).trim());
}

function toggleQueryBuilder({ hydrate }) {
  const isQuery = isQueryEndpoint(elements.path.value);
  elements.queryBuilder.hidden = !isQuery;
  if (!isQuery) {
    return;
  }

  renderQueryTypeFields();
  if (hydrate) {
    hydrateQueryBuilder();
  }
}

function renderQueryTypeFields() {
  const queryType = elements.queryType.value;
  elements.attributeQueryFields.hidden = queryType !== "AttributeQuery";
  elements.logicalQueryFields.hidden = queryType !== "Logical";
  elements.diffQueryFields.hidden = queryType !== "DiffQuery";
}

function resetQueryBuilder() {
  const today = new Date().toISOString().slice(0, 10);
  elements.queryBaseDate.value = today;
  elements.queryEntityType.value = "member";
  elements.queryFrom.value = "";
  elements.queryTo.value = "";
  elements.queryType.value = "AttributeQuery";
  elements.queryAttributeId.value = "";
  elements.queryComparisonOperator.value = "ISNOTNULL";
  elements.queryComparisonValue.value = "";
  elements.queryReferenceIds.value = "";
  elements.queryOnlyLatestData.checked = false;
  elements.queryLogicalOperator.value = "AND";
  elements.queryLogicalConditions.value = "";
  elements.queryDiffAttributeId.value = "";
  elements.queryIntervalTarget.value = "TO";
  elements.queryDiffType.value = "IN";
  elements.queryDiffFromOperator.value = "ISNULL";
  elements.queryDiffFromValue.value = "";
  elements.queryDiffToOperator.value = "ISNOTNULL";
  elements.queryDiffToValue.value = "";
  elements.queryAttributeSelector.value = "";
  elements.queryGroupAttributeSelector.value = "";
  renderQueryTypeFields();
}

function hydrateQueryBuilder() {
  resetQueryBuilder();
  const [pathOnly, queryString = ""] = elements.path.value.split("?", 2);
  const parameters = new URLSearchParams(queryString);
  elements.path.value = pathOnly;
  elements.queryBaseDate.value = parameters.get("baseDate") || elements.queryBaseDate.value;
  elements.queryEntityType.value = parameters.get("entityType") || "member";
  elements.queryFrom.value = parameters.get("from") || "";
  elements.queryTo.value = parameters.get("to") || "";

  if (!elements.body.value.trim()) {
    return;
  }

  try {
    const body = JSON.parse(elements.body.value);
    elements.queryAttributeSelector.value = toLines(body.attributeSelector);
    elements.queryGroupAttributeSelector.value = toLines(body.groupAttributeSelector);
    hydrateQueryObject(body.query);
  } catch {
    // 手入力中の JSON は壊れている可能性があるため、フォーム初期値を維持する。
  }
}

function hydrateQueryObject(query) {
  if (!query || typeof query !== "object") {
    return;
  }

  elements.queryType.value = query.type || "AttributeQuery";
  if (query.type === "Logical") {
    elements.queryLogicalOperator.value = query.op || "AND";
    elements.queryLogicalConditions.value = JSON.stringify(query.conditions || [], null, 2);
  } else if (query.type === "DiffQuery") {
    elements.queryDiffAttributeId.value = query.toCondition?.attributeId || query.fromCondition?.attributeId || "";
    elements.queryIntervalTarget.value = query.intervalTarget || "TO";
    elements.queryDiffType.value = query.diffType || "IN";
    elements.queryDiffFromOperator.value = query.fromCondition?.comparisonOperator || "ISNULL";
    elements.queryDiffFromValue.value = query.fromCondition?.comparisonValue || "";
    elements.queryDiffToOperator.value = query.toCondition?.comparisonOperator || "ISNOTNULL";
    elements.queryDiffToValue.value = query.toCondition?.comparisonValue || "";
  } else {
    elements.queryAttributeId.value = query.condition?.attributeId || "";
    elements.queryComparisonOperator.value = query.condition?.comparisonOperator || "ISNOTNULL";
    elements.queryComparisonValue.value = query.condition?.comparisonValue || "";
    elements.queryReferenceIds.value = toLines(query.condition?.referenceIds);
    elements.queryOnlyLatestData.checked = query.onlyLatestData === true;
  }

  renderQueryTypeFields();
}

function applyQueryBuilder({ updateBody }) {
  if (!isQueryEndpoint(elements.path.value)) {
    return;
  }

  const pathOnly = elements.path.value.split("?", 1)[0];
  const parameters = new URLSearchParams();
  addQueryParameter(parameters, "baseDate", elements.queryBaseDate.value);
  addQueryParameter(parameters, "entityType", elements.queryEntityType.value);
  addQueryParameter(parameters, "from", elements.queryFrom.value);
  addQueryParameter(parameters, "to", elements.queryTo.value);
  const parameterText = parameters.toString();
  elements.path.value = parameterText ? `${pathOnly}?${parameterText}` : pathOnly;

  if (updateBody) {
    elements.contentType.value = "application/json";
    elements.bodyFormat.value = "json";
    elements.body.value = JSON.stringify({
      query: buildQueryObject(),
      attributeSelector: linesToValues(elements.queryAttributeSelector.value),
      groupAttributeSelector: linesToValues(elements.queryGroupAttributeSelector.value),
    }, null, 2);
  }
}

function buildQueryObject() {
  if (elements.queryType.value === "Logical") {
    const conditions = parseJsonField(elements.queryLogicalConditions.value || "[]", "複合条件 JSON");
    if (!Array.isArray(conditions)) {
      throw new Error("複合条件 JSON は配列で指定してください。");
    }
    return { type: "Logical", op: elements.queryLogicalOperator.value, conditions };
  }

  if (elements.queryType.value === "DiffQuery") {
    const attributeId = requireQueryValue(elements.queryDiffAttributeId.value, "差分対象項目 ID");
    return {
      type: "DiffQuery",
      fromCondition: buildComparisonCondition(attributeId, elements.queryDiffFromOperator.value, elements.queryDiffFromValue.value),
      toCondition: buildComparisonCondition(attributeId, elements.queryDiffToOperator.value, elements.queryDiffToValue.value),
      intervalTarget: elements.queryIntervalTarget.value,
      diffType: elements.queryDiffType.value,
    };
  }

  const attributeId = requireQueryValue(elements.queryAttributeId.value, "項目 ID");
  return {
    type: "AttributeQuery",
    condition: {
      ...buildComparisonCondition(attributeId, elements.queryComparisonOperator.value, elements.queryComparisonValue.value),
      referenceIds: linesToValues(elements.queryReferenceIds.value),
    },
    onlyLatestData: elements.queryOnlyLatestData.checked,
  };
}

function buildComparisonCondition(attributeId, comparisonOperator, comparisonValue) {
  const condition = { attributeId, comparisonOperator };
  if (!isNullComparisonOperator(comparisonOperator)) {
    condition.comparisonValue = requireQueryValue(comparisonValue, "比較値");
  }
  return condition;
}

function isNullComparisonOperator(comparisonOperator) {
  return comparisonOperator === "ISNULL" || comparisonOperator === "ISNOTNULL";
}

function requireQueryValue(value, label) {
  if (!value.trim()) {
    throw new Error(`${label} を入力してください。`);
  }
  return value.trim();
}

function linesToValues(value) {
  return value.split(/\r?\n/).map((item) => item.trim()).filter(Boolean);
}

function toLines(values) {
  return Array.isArray(values) ? values.join("\n") : "";
}

function addQueryParameter(parameters, name, value) {
  if (value && value.trim()) {
    parameters.set(name, value.trim());
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
  elements.proxyUrl.value = localStorage.getItem("iij.proxyUrl") || "";
  elements.timeoutSeconds.value = localStorage.getItem("iij.timeoutSeconds") || "30";
  elements.proxyMode.value = localStorage.getItem("iij.proxyMode") || "system";
  elements.variables.value = localStorage.getItem("iij.variables") || "{}";
  elements.headers.value = localStorage.getItem("iij.headers") || "{}";
}

function persistLocalSettings() {
  localStorage.setItem("iij.baseUrl", elements.baseUrl.value);
  localStorage.setItem("iij.accessToken", elements.accessToken.value);
  localStorage.setItem("iij.proxyUrl", elements.proxyUrl.value);
  localStorage.setItem("iij.timeoutSeconds", elements.timeoutSeconds.value);
  localStorage.setItem("iij.proxyMode", elements.proxyMode.value);
  localStorage.setItem("iij.variables", elements.variables.value);
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

function cancelAutoResolve() {
  if (state.autoResolveTimer) {
    window.clearTimeout(state.autoResolveTimer);
    state.autoResolveTimer = 0;
  }
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
