let selectedSessionId = null;
let lastBatchId = null;

const $ = (id) => document.getElementById(id);
const api = async (path, options = {}) => {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
    body: options.body ? JSON.stringify(options.body) : undefined
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.message || `HTTP ${response.status}`);
  return payload;
};

const sampleNetlist = (name, resistorName = "R1") => ({
  name,
  ports: [
    { id: "in", name: "IN", direction: "Input", net: "n1" },
    { id: "out", name: "OUT", direction: "Output", net: "n2" }
  ],
  nets: [
    { id: "n1", name: "input" },
    { id: "n2", name: "output" }
  ],
  devices: [
    { id: "r1", name: resistorName, type: "R", pins: [{ pin: "1", net: "n1" }, { pin: "2", net: "n2" }],
      parameters: [{ key: "R", numericValue: 1000, unit: "ohm" }] }
  ]
});

window.addEventListener("DOMContentLoaded", async () => {
  $("netlistText").value = JSON.stringify(sampleNetlist("filter-a"), null, 2);
  bind();
  await refreshInputs();
  await refreshSessions();
});

function bind() {
  $("health").textContent = "服务在线";
  $("importNetlist").onclick = async () => {
    const record = await api("/api/netlists", { method: "POST", body: {
      name: $("netlistName").value, rawText: $("netlistText").value
    }});
    $("importResult").textContent = `已保存网表：${record.name} / ${record.fingerprint.slice(0, 12)}`;
    await refreshInputs();
  };
  $("importRules").onclick = async () => {
    const record = await api("/api/rules", { method: "POST", body: {
      version: $("ruleVersion").value, rawText: $("rulesText").value
    }});
    $("importResult").textContent = `已保存规则：${record.version} / ${record.fingerprint.slice(0, 12)}`;
    await refreshInputs();
  };
  $("createSession").onclick = async () => {
    const session = await api("/api/sessions", { method: "POST", body: {
      name: $("sessionName").value,
      leftNetlistId: $("leftNetlist").value,
      rightNetlistId: $("rightNetlist").value,
      rulesId: $("rulesSelect").value || null,
      timeoutMs: Number($("timeoutMs").value || 500)
    }});
    selectedSessionId = session.session.id;
    renderSession(session);
  };
  $("refreshSession").onclick = async () => {
    if (selectedSessionId) renderSession(await api(`/api/sessions/${selectedSessionId}/search`, {
      method: "POST", body: { timeoutMs: Number($("timeoutMs").value || 500) }
    }));
  };
  $("lockPair").onclick = () => decide("/locks");
  $("vetoPair").onclick = () => decide("/vetos");
  $("loadCertificate").onclick = async () => {
    if (!selectedSessionId) return;
    const certificate = await api(`/api/sessions/${selectedSessionId}/certificate`);
    $("certificate").textContent = JSON.stringify(certificate, null, 2);
  };
  $("submitBatch").onclick = async () => {
    const batch = await api("/api/batches", { method: "POST", body: {
      name: "batch",
      idempotencyKey: $("batchKey").value || null,
      pairs: JSON.parse($("batchPairs").value || "[]")
    }});
    lastBatchId = batch.id;
    await refreshBatch();
  };
  $("refreshBatch").onclick = refreshBatch;
}

async function decide(path) {
  if (!selectedSessionId) return;
  const session = await api(`/api/sessions/${selectedSessionId}${path}`, { method: "POST", body: {
    leftComponentId: $("manualLeft").value,
    rightComponentId: $("manualRight").value
  }});
  renderSession(session);
}

async function refreshInputs() {
  const inputs = await api("/api/inputs");
  fillSelect($("leftNetlist"), inputs.netlists);
  fillSelect($("rightNetlist"), inputs.netlists);
  fillSelect($("rulesSelect"), inputs.rules, true);
}

async function refreshSessions() {
  const sessions = await api("/api/sessions");
  if (sessions.length) {
    selectedSessionId = sessions[sessions.length - 1].id;
    renderSession(await api(`/api/sessions/${selectedSessionId}`));
  }
}

function fillSelect(select, records, allowEmpty = false) {
  const current = select.value;
  select.innerHTML = allowEmpty ? "<option value=''>默认规则</option>" : "";
  for (const record of records) {
    const option = document.createElement("option");
    option.value = record.id;
    option.textContent = `${record.name || record.version || record.id} (${record.id.slice(0, 12)})`;
    select.appendChild(option);
  }
  if (current) select.value = current;
}

function renderSession(snapshot) {
  selectedSessionId = snapshot.session.id;
  const status = snapshot.session.status;
  const className = status === "Equivalent" ? "equivalent" :
    status === "SearchTimeout" ? "timeout" :
    status === "LockConflict" ? "conflict" :
    status === "DiagnosticFailure" ? "diagnostic" : "non";
  $("sessionHeadline").className = `headline ${className}`;
  $("sessionHeadline").textContent = `${status}：${snapshot.session.name}`;
  renderDiagnostics(snapshot);
  renderMatches(snapshot);
  renderAmbiguities(snapshot);
  renderDecisions(snapshot);
  renderWitness(snapshot);
  renderEvidence(snapshot);
}

function renderDiagnostics(snapshot) {
  const diagnostics = snapshot.session.result?.diagnostics || [];
  $("diagnostics").innerHTML = diagnostics.map(d =>
    `<div class="row"><strong>${d.code} / ${d.severity}</strong>${d.message}</div>`).join("") ||
    '<p class="muted">无前置诊断。</p>';
}

function renderMatches(snapshot) {
  const matches = snapshot.session.result?.components || [];
  $("matches").innerHTML = matches.map(m =>
    `<div class="row"><strong>${m.leftName} → ${m.rightName}</strong>
      <span class="tag">${m.leftComponentId}</span><span class="tag">${m.rightComponentId}</span>
      <div class="muted">${Object.entries(m.pins).map(([l, r]) => `${l}→${r}`).join("，")}</div></div>`).join("") ||
    '<p class="muted">当前没有完整匹配。</p>';
}

function renderAmbiguities(snapshot) {
  const items = [
    ...(snapshot.session.result?.ambiguities || []).map(a => ({ ...a, kind: "拓扑歧义" })),
    ...snapshot.decisions.filter(d => d.status === "NeedsReview").map(d => ({
      leftComponentId: d.leftComponentId, rightComponentId: d.rightComponentId,
      leftName: d.leftName, rightName: d.rightName, reason: "旧会话决定，需要人工复核。", kind: "旧决定", decisionId: d.id
    }))
  ];
  $("ambiguities").innerHTML = items.map(item =>
    `<div class="row"><strong>${item.kind}：${item.leftName} → ${item.rightName}</strong>${item.reason}
      <div class="muted">${item.leftComponentId} / ${item.rightComponentId}</div>
      ${item.decisionId ? `<button onclick="review('${item.decisionId}', true)">采纳</button><button class='secondary' onclick="review('${item.decisionId}', false)">拒绝</button>` : ""}
    </div>`).join("") || '<p class="muted">无歧义候选。</p>';
}

window.review = async (decisionId, accept) => {
  renderSession(await api(`/api/sessions/${selectedSessionId}/suggestions/${decisionId}`, {
    method: "POST", body: { accept }
  }));
};

function renderDecisions(snapshot) {
  const decisions = snapshot.decisions.filter(d => d.status !== "NeedsReview");
  $("decisions").innerHTML = decisions.map(d =>
    `<div class="row"><span class="tag">${d.kind}</span><strong>${d.leftName} → ${d.rightName}</strong>${d.status}</div>`).join("");
}

function renderWitness(snapshot) {
  const witness = snapshot.session.result?.distinguishingSubgraph;
  if (!witness) {
    $("witness").innerHTML = '<p class="muted">等价结论无区分图；请查看证书。</p>';
    return;
  }
  const lockIds = witness.responsibleLockIds || [];
  $("witness").innerHTML =
    `<div class="row"><strong>${witness.reason}</strong>${lockIds.map(id => `<span class="tag">${id}</span>`).join("")}</div>
     <div class="muted">组件 ${witness.components.length}，网络 ${witness.nets.length}，连接 ${witness.connections.length}</div>
     <pre>${JSON.stringify(witness, null, 2)}</pre>`;
}

function renderEvidence(snapshot) {
  $("leftRaw").textContent = JSON.stringify({
    fingerprint: snapshot.leftNetlist.fingerprint, rawText: JSON.parse(snapshot.leftNetlist.rawText)
  }, null, 2);
  $("rightRaw").textContent = JSON.stringify({
    fingerprint: snapshot.rightNetlist.fingerprint, rawText: JSON.parse(snapshot.rightNetlist.rawText)
  }, null, 2);
  $("rulesRaw").textContent = JSON.stringify({
    version: snapshot.rules.version, fingerprint: snapshot.rules.fingerprint
  }, null, 2);
  $("events").innerHTML = snapshot.events.map(ev =>
    `<div class="row"><strong>#${ev.sequence} ${ev.type}</strong><span class="muted">${ev.at}</span><pre>${ev.payload}</pre></div>`).join("");
}

async function refreshBatch() {
  if (!lastBatchId) return;
  const batch = await api(`/api/batches/${lastBatchId}`);
  $("batchResult").innerHTML =
    `<div class="row"><strong>${batch.status}</strong>
      幂等键：${batch.idempotencyKey}<br>
      作业：${batch.jobIds.length}，发布时间：${batch.publishedAt || "尚未发布；所有对子完成后才会发布结果集。"}
     </div><pre>${JSON.stringify(batch, null, 2)}</pre>`;
}
