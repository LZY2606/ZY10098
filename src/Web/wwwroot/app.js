let state = null;
let saved = { leftId: null, rightId: null, rulesId: null };

const api = async (path, body, method = "POST") => {
  const response = await fetch("/api" + path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(data?.message || response.statusText);
  return data;
};

const esc = value => String(value ?? "").replace(/[&<>"']/g, c =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

function loadSamples() {
  document.querySelector("#rules").value = JSON.stringify({
    id: "demo-rules", name: "demo", version: "2026.09.21", searchTimeoutMs: 1000,
    powerNames: ["VDD"], groundNames: ["GND"],
    netAliases: [],
    portAliases: [{ moduleId: "*", aliases: { IN: "A", OUT: "B" } }],
    swappablePins: [{ deviceType: "resistor", pins: ["A", "B"] }],
    resistorArrays: [{ deviceType: "resistor_array", elementType: "resistor", countParameter: "count", resistanceParameter: "resistance", pinAFormat: "A{0}", pinBFormat: "B{0}", nameFormat: "{0}.R{1}" }],
    parameterUnits: [{ parameterName: "resistance", canonicalUnit: "ohm", units: { ohm: 1, "Ω": 1, kohm: 1000 } }]
  }, null, 2);
  document.querySelector("#leftNet").value = JSON.stringify({
    id: "left-demo", name: "array side", rootModuleId: "top",
    modules: [{
      id: "top", name: "top",
      ports: [{ id: "A" }, { id: "B" }],
      devices: [
        { id: "RA1", type: "resistor_array", pins: { A1: "A", B1: "N", A2: "N", B2: "B" }, parameters: { count: "2", resistance: "1 kohm" } }
      ],
      instances: [], nets: []
    }]
  }, null, 2);
  document.querySelector("#rightNet").value = JSON.stringify({
    id: "right-demo", name: "two resistors", rootModuleId: "top",
    modules: [{
      id: "top", name: "top",
      ports: [{ id: "IN" }, { id: "OUT" }],
      devices: [
        { id: "R1", type: "resistor", pins: { A: "IN", B: "N" }, parameters: { resistance: "1000 ohm" } },
        { id: "R2", type: "resistor", pins: { A: "N", B: "OUT" }, parameters: { resistance: "1000 ohm" } }
      ],
      instances: [], nets: []
    }]
  }, null, 2);
  document.querySelector("#batch").value = JSON.stringify({
    idempotencyKey: "demo-batch-1",
    rulesId: "demo-rules",
    pairs: [{ idempotencyKey: "pair-1", leftNetlistId: "left-demo", rightNetlistId: "right-demo" }]
  }, null, 2);
}

async function saveRules() {
  const payload = JSON.parse(document.querySelector("#rules").value);
  const result = await api("/rules", { id: payload.id, name: payload.name, rules: payload });
  saved.rulesId = result.rulesId;
  await refresh();
}

async function saveNetlists() {
  const left = JSON.parse(document.querySelector("#leftNet").value);
  const right = JSON.parse(document.querySelector("#rightNet").value);
  const leftRev = await api("/netlists", { id: left.id, name: left.name, rawText: document.querySelector("#leftNet").value });
  const rightRev = await api("/netlists", { id: right.id, name: right.name, rawText: document.querySelector("#rightNet").value });
  saved.leftId = leftRev.netlistId;
  saved.rightId = rightRev.netlistId;
  const session = await api("/sessions", {
    leftNetlistId: leftRev.netlistId,
    rightNetlistId: rightRev.netlistId,
    rulesId: saved.rulesId
  });
  setTimeout(refresh, 150);
}

async function submitBatch() {
  const payload = JSON.parse(document.querySelector("#batch").value);
  await api("/batches", payload);
  setTimeout(refresh, 150);
}

async function retry() {
  const id = document.querySelector("#sessions").value;
  if (id) { await api(`/sessions/${encodeURIComponent(id)}/retry`, {}); setTimeout(refresh, 150); }
}

async function refresh() {
  state = await api("/state", undefined, "GET");
  if (!saved.rulesId) saved.rulesId = Object.values(state.rules).at(-1)?.rulesId;
  renderSessions();
  renderContent();
}

function renderSessions() {
  const select = document.querySelector("#sessions");
  select.innerHTML = Object.values(state.sessions)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
    .map(s => `<option value="${esc(s.id)}">${esc(s.id)} · ${esc(s.state)}${s.result ? " · " + esc(s.result.status) : ""}</option>`)
    .join("");
}

function selectedSession() {
  const id = document.querySelector("#sessions").value;
  return state.sessions[id] || Object.values(state.sessions).sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];
}

function renderContent() {
  const session = selectedSession();
  document.querySelector("#content").innerHTML = renderInventory() + (session ? renderSession(session) : "<section><h2>暂无会话</h2></section>") + renderBatches();
}

function badge(status) {
  const cls = status === "Equivalent" || status === "Completed" || status === "Accepted" ? "ok"
    : status === "NotEquivalent" || status === "MappingConflict" ? "bad"
    : status === "DiagnosticsBlocked" ? "warn" : "info";
  return `<span class="badge ${cls}">${esc(status)}</span>`;
}

function renderInventory() {
  const netlists = Object.values(state.netlists);
  const rules = Object.values(state.rules);
  return `<section><h2>输入与规则版本</h2><table><thead><tr><th>类型</th><th>标识</th><th>版本</th><th>指纹</th></tr></thead><tbody>
    ${netlists.map(n => `<tr><td>网表</td><td>${esc(n.netlistId)} #${n.revision}</td><td>${esc(n.document.name)}</td><td><code>${esc(n.fingerprint.slice(0,18))}</code></td></tr>`).join("")}
    ${rules.map(r => `<tr><td>规则</td><td>${esc(r.rulesId)} #${r.revision}</td><td>${esc(r.version)}</td><td><code>${esc(r.fingerprint.slice(0,18))}</code></td></tr>`).join("")}
  </tbody></table></section>`;
}

function renderSession(session) {
  const result = session.result;
  return `<section>
    <h2>会话 ${esc(session.id)} ${badge(session.state)} ${result ? badge(result.status) : ""}</h2>
    <p class="muted">左指纹 <code>${esc(session.leftFingerprint.slice(0,18))}</code> · 右指纹 <code>${esc(session.rightFingerprint.slice(0,18))}</code> · 规则 ${esc(session.ruleVersion)} / <code>${esc(session.ruleFingerprint.slice(0,18))}</code></p>
    ${result ? renderResult(session, result) : "<p>后台分析进行中。</p>"}
    ${renderInputs(session)}
    ${renderSuggestions(session)}
    ${renderEvents(session)}
  </section>`;
}

function renderInputs(session) {
  const left = state.netlists[session.leftRevisionId];
  const right = state.netlists[session.rightRevisionId];
  const rules = state.rules[session.ruleRevisionId];
  return `<details><summary>支撑输入、规则版本和指纹</summary>
    <div class="grid2">
      <div><h3>左网表原文</h3><pre>${esc(left?.document.rawText || "")}</pre></div>
      <div><h3>右网表原文</h3><pre>${esc(right?.document.rawText || "")}</pre></div>
    </div>
    <h3>规则原文</h3><pre>${esc(JSON.stringify(rules?.rules || {}, null, 2))}</pre>
  </details>`;
}

function renderResult(session, result) {
  return `<div class="card">
    <h3>匹配组件</h3>
    <table><thead><tr><th>A</th><th>B</th><th>引脚</th></tr></thead><tbody>
      ${result.mappings.map(m => `<tr><td>${esc(m.leftId)}</td><td>${esc(m.rightId)}</td><td>${m.pins.map(p => `${esc(p.leftPin)}→${esc(p.rightPin)}`).join(", ")}</td></tr>`).join("")}
    </tbody></table>
    <h3>操作</h3>
    ${result.mappings.slice(0, 8).map(m => `<button onclick="lockPair('${esc(session.id)}','${esc(m.leftId)}','${esc(m.rightId)}')">锁定 ${esc(m.leftId)}</button><button class="danger" onclick="rejectPair('${esc(session.id)}','${esc(m.leftId)}','${esc(m.rightId)}')">否决</button>`).join(" ")}
    ${renderAmbiguous(result)}
    ${renderDiagnostics(result.diagnostics || [])}
    ${result.certificate ? renderCertificate(result.certificate) : ""}
    ${result.witness ? renderWitness(result.witness) : ""}
    ${result.conflictingLocks?.length ? `<h3>最小冲突锁定集合</h3><ul>${result.conflictingLocks.map(id => `<li><code>${esc(id)}</code></li>`).join("")}</ul>` : ""}
    ${renderActiveDecisions(session)}
    <p class="muted">${result.timedOut ? "搜索在截止时间前未完成；这是超时，不等同于无映射。" : `耗时 ${esc(result.elapsedMs)} ms`}</p>
  </div>`;
}

function renderActiveDecisions(session) {
  const decisions = session.decisions.filter(d => d.kind !== "Suggestion" && d.reviewState === "Accepted");
  if (!decisions.length) return "";
  return `<h3>当前锁定/否决</h3><ul>${decisions.map(d => `<li>${esc(d.kind)} <code>${esc(d.leftSourceId)}</code> → <code>${esc(d.rightSourceId)}</code>
    <button class="danger" onclick="clearDecision('${esc(session.id)}','${esc(d.id)}')">撤销</button></li>`).join("")}</ul>`;
}

function renderAmbiguous(result) {
  if (!result.ambiguousCandidates?.length) return "";
  return `<h3>候选映射歧义</h3>${result.ambiguousCandidates.map(c => `<p><code>${esc(c.leftId)}</code> 可映射到 <code>${esc(c.rightId)}</code> 或 ${c.alternativeRightIds.map(id => `<code>${esc(id)}</code>`).join("、")}</p>`).join("")}`;
}

function renderDiagnostics(diagnostics) {
  if (!diagnostics.length) return "";
  return `<h3>先诊断</h3><table><thead><tr><th>级别</th><th>侧</th><th>类型</th><th>位置</th><th>消息</th></tr></thead><tbody>
    ${diagnostics.map(d => `<tr><td>${esc(d.level)}</td><td>${esc(d.side)}</td><td>${esc(d.kind)}</td><td><code>${esc(d.path || "")}</code></td><td>${esc(d.message)}</td></tr>`).join("")}
  </tbody></table>`;
}

function renderCertificate(certificate) {
  return `<details open><summary>可验证映射证书</summary>
    <p>哈希 <code>${esc(certificate.certificateSha256)}</code></p>
    <pre>${esc(JSON.stringify({ ...certificate, issuedAt: "normalized for verification display" }, null, 2))}</pre>
    <button class="good" onclick="verifyCertificate()">验证证书哈希</button><span id="verifyResult"></span>
  </details>`;
}

function renderWitness(witness) {
  return `<details><summary>最小区分子图：${esc(witness.side)}</summary>
    <p>${esc(witness.reason)}</p>
    <p>组件：${witness.componentIds.map(id => `<code>${esc(id)}</code>`).join("、")}</p>
    <p>网络：${witness.netIds.map(id => `<code>${esc(id)}</code>`).join("、")}</p>
    <pre>${esc(JSON.stringify(witness.components, null, 2))}</pre>
  </details>`;
}

function renderSuggestions(session) {
  const suggestions = session.decisions.filter(d => d.kind === "Suggestion" || d.reviewState === "NeedsReview");
  if (!suggestions.length) return "";
  return `<div class="card"><h3>需复核的历史建议</h3>${suggestions.map(d => `<p><code>${esc(d.leftSourceId)}</code> → <code>${esc(d.rightSourceId)}</code> 来自 ${esc(d.originSessionId)}
    <button class="good" onclick="acceptSuggestion('${esc(session.id)}','${esc(d.id)}')">接受为锁定</button><button class="danger" onclick="declineSuggestion('${esc(session.id)}','${esc(d.id)}')">否决</button></p>`).join("")}</div>`;
}

function renderEvents(session) {
  return `<details><summary>事件顺序（${session.events.length}）</summary><ol>${session.events.map(e =>
    `<li>#${e.sequence} ${esc(e.at)} <b>${esc(e.type)}</b> ${esc(e.detail)}</li>`).join("")}</ol></details>`;
}

function renderBatches() {
  const batches = Object.values(state.batches);
  if (!batches.length) return "";
  return `<section><h2>批量结果集</h2>${batches.map(b => `<div class="card"><h3>${esc(b.id)} ${badge(b.state)}</h3>
    <table><thead><tr><th>幂等键</th><th>A</th><th>B</th><th>会话</th><th>结论</th></tr></thead><tbody>
    ${b.pairs.map(p => `<tr><td>${esc(p.idempotencyKey)}</td><td>${esc(p.leftNetlistId)}</td><td>${esc(p.rightNetlistId)}</td><td><code>${esc(p.sessionId)}</code></td><td>${p.status ? badge(p.status) : "等待"}</td></tr>`).join("")}
    </tbody></table><p class="muted">${b.state === "Completed" ? "所有对子完成后一次发布。" : "结果集尚未发布。"}</p></div>`).join("")}</section>`;
}

async function lockPair(sessionId, left, right) {
  await api(`/sessions/${encodeURIComponent(sessionId)}/decisions`, { leftSourceId: left, rightSourceId: right, kind: "Locked", pins: [] });
  setTimeout(refresh, 120);
}

async function rejectPair(sessionId, left, right) {
  await api(`/sessions/${encodeURIComponent(sessionId)}/decisions`, { leftSourceId: left, rightSourceId: right, kind: "Rejected", pins: [] });
  setTimeout(refresh, 120);
}

async function acceptSuggestion(sessionId, decisionId) {
  await api(`/sessions/${encodeURIComponent(sessionId)}/suggestions/${encodeURIComponent(decisionId)}/accept`, {});
  setTimeout(refresh, 120);
}

async function declineSuggestion(sessionId, decisionId) {
  await api(`/sessions/${encodeURIComponent(sessionId)}/suggestions/${encodeURIComponent(decisionId)}/reject`, {});
  setTimeout(refresh, 120);
}

async function clearDecision(sessionId, decisionId) {
  await fetch(`/api/sessions/${encodeURIComponent(sessionId)}/decisions/${encodeURIComponent(decisionId)}`, { method: "DELETE" });
  setTimeout(refresh, 120);
}

async function verifyCertificate() {
  const session = selectedSession();
  const result = await api("/certificate/verify", session.result.certificate);
  document.querySelector("#verifyResult").textContent = result.valid ? "有效：摘要与规范化映射一致。" : "无效。";
}

refresh().catch(error => alert(error.message));
