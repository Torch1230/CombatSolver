const $ = (id) => document.getElementById(id);
const requests = new Map();
const attempts = new Map();
let authenticated = false,
  activeTab = "live",
  automatic = true,
  overviewData,
  chart;
let livePage = 1,
  livePages = 1,
  liveQuery = "",
  liveSort = "online",
  liveOrder = "desc";
let runPage = 1,
  runPages = 1,
  runSort = "streak",
  runOrder = "desc",
  applied = new URLSearchParams();
let liveUpdated = 0,
  runsUpdated = 0,
  overviewUpdated = 0,
  copyIdentity = "";
const labels = {
  source: "来源",
  participation: "运行覆盖",
  character: "角色",
  runs_min: "至少完成局数",
  activity: "实际使用",
  version: "版本",
  ascension: "进阶",
  since: "结算起始",
  until: "结算截止",
  streak_min: "当前连续段 ≥",
  streak_max: "当前连续段 ≤",
  best_min: "最高连续段 ≥",
  best_max: "最高连续段 ≤",
  rate_min: "胜率 % ≥",
  rate_max: "胜率 % ≤",
  wins_min: "胜场 ≥",
  losses_min: "负场 ≥",
  abandoned_min: "放弃 ≥",
  sessionId: "安装标识",
  profileId: "档案标识",
};
const form = $("run-filters");
const fields = [
  [
    "跑局条件",
    true,
    [
      ["activity", "实际使用", "select"],
      ["version", "Mod 版本", "text"],
      ["ascension", "进阶", "number"],
      ["from", "结算起始", "datetime-local"],
      ["to", "结算截止", "datetime-local"],
    ],
  ],
  [
    "战绩范围",
    false,
    [
      ["streak_min", "当前连续段 ≥", "number"],
      ["streak_max", "当前连续段 ≤", "number"],
      ["best_min", "最高连续段 ≥", "number"],
      ["best_max", "最高连续段 ≤", "number"],
      ["rate_min", "胜率 % ≥", "number"],
      ["rate_max", "胜率 % ≤", "number"],
      ["wins_min", "胜场 ≥", "number"],
      ["losses_min", "负场 ≥", "number"],
      ["abandoned_min", "放弃 ≥", "number"],
    ],
  ],
  [
    "精确查找",
    false,
    [
      ["custom_character", "第三方角色 ID", "text"],
      ["sessionId", "安装标识", "text"],
      ["profileId", "档案标识", "text"],
    ],
  ],
];
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = String(text);
  if (className) node.className = className;
  return node;
}
function setText(node, value) {
  const text = String(value);
  if (node.textContent !== text) node.textContent = text;
}
for (const [title, solverOnly, definitions] of fields) {
  const group = el("fieldset");
  if (solverOnly) group.dataset.solverOnly = "";
  group.append(el("legend", title));
  const grid = el("div", undefined, "filter-grid");
  for (const [name, label, type] of definitions) {
    const wrapper = el("label", label),
      input = el(type === "select" ? "select" : "input");
    input.name = name;
    if (type === "select") {
      for (const [v, t] of [
        ["", "不限"],
        ["solve", "求解过"],
        ["execute", "执行过"],
        ["auto", "使用过全自动"],
      ]) {
        const option = el("option", t);
        option.value = v;
        input.append(option);
      }
    } else {
      input.type = type;
      input.placeholder = "不限";
      if (type === "number") {
        input.min = "0";
        if (name.startsWith("rate_")) {
          input.max = "100";
          input.step = "any";
        }
        if (name === "ascension") input.max = "100";
      } else if (type === "text")
        input.maxLength = name.endsWith("Id") ? 32 : 128;
    }
    if (name.endsWith("Id")) {
      input.pattern = "[a-f0-9]{32}";
      input.placeholder = "32 位匿名标识";
    }
    if (name === "custom_character") input.placeholder = "填写时优先于角色选项";
    if (name === "abandoned_min") wrapper.dataset.solverOnly = "";
    wrapper.append(input);
    grid.append(wrapper);
  }
  group.append(grid);
  $("filter-groups").append(group);
}
function updateHistoricalControls() {
  const historical = form.elements.source.value === "historical";
  for (const group of form.querySelectorAll("[data-solver-only]")) {
    group.hidden = historical;
    for (const input of group.querySelectorAll("input,select"))
      input.disabled = historical;
  }
  $("historical-note").hidden = !historical;
}
function draft() {
  const p = new URLSearchParams();
  for (const [k, v] of new FormData(form)) {
    if (!v || k === "custom_character") continue;
    if (k === "from" || k === "to") {
      const t = new Date(v).getTime();
      if (Number.isFinite(t))
        p.set(k === "from" ? "since" : "until", String(t));
    } else p.set(k, String(v).trim());
  }
  if (form.elements.custom_character.value.trim())
    p.set("character", form.elements.custom_character.value.trim());
  return p;
}
function canonical(p) {
  return [...p]
    .sort(([a], [b]) => a.localeCompare(b))
    .map((pair) => JSON.stringify(pair))
    .join("|");
}
function dirty() {
  return canonical(draft()) !== canonical(applied);
}
function markDirty() {
  $("dirty-note").hidden = !dirty();
  $("apply-filters").textContent = dirty() ? "应用修改" : "应用筛选";
  const count = [...draft()].filter(
    ([k]) => !["source", "participation", "character", "runs_min"].includes(k),
  ).length;
  setText($("advanced-count"), count ? `· 已填写 ${count} 项` : "");
}
function fillForm(p) {
  for (const input of form.elements) if (input.name) input.value = "";
  form.elements.source.value = p.get("source") || "solver";
  form.elements.participation.value = p.get("participation") || "full";
  for (const [k, v] of p) {
    if (k === "since" || k === "until") {
      const d = new Date(Number(v));
      if (Number.isFinite(d.getTime())) {
        const local = new Date(d.getTime() - d.getTimezoneOffset() * 60000)
          .toISOString()
          .slice(0, 16);
        form.elements[k === "since" ? "from" : "to"].value = local;
      }
    } else if (form.elements[k]) form.elements[k].value = v;
  }
  if (p.get("character") && !form.elements.character.value)
    form.elements.custom_character.value = p.get("character");
  updateHistoricalControls();
  markDirty();
}
function saveState() {
  const p = new URLSearchParams({
    view: activeTab,
    live_sort: liveSort,
    live_order: liveOrder,
    run_sort: runSort,
    run_order: runOrder,
    range: $("range").value,
  });
  if (liveQuery) p.set("q", liveQuery);
  for (const [k, v] of applied) p.set("run_" + k, v);
  history.replaceState(null, "", "?" + p);
}
function restoreState() {
  const p = new URLSearchParams(location.search);
  activeTab = p.get("view") === "runs" ? "runs" : "live";
  liveQuery = p.get("q") || "";
  $("search").value = liveQuery;
  if (["online", "floor", "hpLoss", "lastSeen"].includes(p.get("live_sort")))
    liveSort = p.get("live_sort");
  if (p.get("live_order") === "asc") liveOrder = "asc";
  if (["streak", "best", "rate", "wins", "losses"].includes(p.get("run_sort")))
    runSort = p.get("run_sort");
  if (p.get("run_order") === "asc") runOrder = "asc";
  if (["1", "24", "168", "720"].includes(p.get("range")))
    $("range").value = p.get("range");
  applied = new URLSearchParams();
  for (const [k, v] of p)
    if (k.startsWith("run_") && labels[k.slice(4)]) applied.set(k.slice(4), v);
  if (!applied.has("source"))
    applied = new URLSearchParams({
      source: "solver",
      participation: "full",
      runs_min: "5",
    });
  fillForm(applied);
  try {
    automatic = localStorage.getItem("cs-monitor-auto") !== "off";
    $("trend").open = localStorage.getItem("cs-monitor-trend") === "open";
  } catch {}
  renderChips();
  displayTab();
}
function loggedOut() {
  authenticated = false;
  for (const controller of requests.values()) controller.abort();
  requests.clear();
  $("session-loading").hidden = true;
  $("login").hidden = false;
  $("dashboard").hidden = true;
  $("logout").hidden = true;
  $("refresh-controls").hidden = true;
  setText($("connection"), "未登录");
}
async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    credentials: "same-origin",
  });
  if (response.status === 401) {
    loggedOut();
    throw Error("登录已失效");
  }
  if (!response.ok)
    throw Error(
      response.status === 400
        ? "筛选条件无效，请检查范围与标识"
        : response.status === 429
          ? "请求过于频繁，请稍后重试"
          : "服务暂时不可用",
    );
  return response.status === 204 ? null : response.json();
}
async function load(key, path, onSuccess, errorId) {
  requests.get(key)?.abort();
  const request = new AbortController();
  requests.set(key, request);
  attempts.set(key, Date.now());
  try {
    const data = await api(path, { signal: request.signal });
    if (request.signal.aborted || requests.get(key) !== request) return false;
    onSuccess(data);
    $(errorId).hidden = true;
    return true;
  } catch (error) {
    if (request.signal.aborted) return false;
    if (!$("login").hidden) return false;
    if (!authenticated) {
      $("session-loading").hidden = false;
      $("session-message").textContent = error.message + "，可重试恢复登录。";
      $("session-retry").hidden = false;
      setText($("connection"), "连接中断");
    } else {
      $(errorId).hidden = false;
      setText($(errorId), error.message + "。保留上次数据及其筛选条件。");
    }
    return false;
  } finally {
    if (requests.get(key) === request) requests.delete(key);
  }
}
function duration(seconds) {
  const days = Math.floor(seconds / 86400),
    hours = Math.floor(seconds / 3600) % 24,
    minutes = Math.floor(seconds / 60) % 60;
  return days
    ? `${days}天 ${hours}小时`
    : hours
      ? `${hours}小时 ${minutes}分`
      : `${minutes}分 ${seconds % 60}秒`;
}
function rate(value) {
  return value === null ? "暂无胜率" : `${(value * 100).toFixed(1)}%`;
}
function state(p) {
  return p.inCombat
    ? ["战斗中", "combat"]
    : p.inRun === true
      ? ["跑局中", "run"]
      : p.inRun === false
        ? ["主菜单", "idle"]
        : ["状态未知", "unknown"];
}
function tableRows(body, items, key, update) {
  const existing = new Map(
    [...body.children].map((row) => [row.dataset.key, row]),
  );
  items.forEach((item, index) => {
    const id = key(item);
    let row = existing.get(id);
    if (!row) {
      row = el("tr");
      row.dataset.key = id;
    }
    row.record = item;
    update(row, item);
    if (body.children[index] !== row)
      body.insertBefore(row, body.children[index] || null);
    existing.delete(id);
  });
  for (const row of existing.values()) row.remove();
}
function cell(row, index, primary, secondary, cls = "") {
  let td = row.children[index];
  if (!td) {
    td = el("td");
    td.append(
      el("span", undefined, "primary-text"),
      el("span", undefined, "secondary-text"),
    );
    row.append(td);
  }
  setText(td.children[0], primary);
  setText(td.children[1], secondary || "");
  td.children[1].hidden = !secondary;
  if (td.className !== cls) td.className = cls;
  return td;
}
function showDetails(title, pairs, identity) {
  copyIdentity = identity;
  setText($("details-title"), title);
  $("details-content").replaceChildren();
  for (const [name, value] of pairs)
    $("details-content").append(el("dt", name), el("dd", value ?? "未知"));
  setText($("copy-status"), "");
  $("details-dialog").showModal();
}
function liveDetails(p) {
  showDetails(
    p.name || "未命名玩家",
    [
      ["安装标识", p.sessionId],
      ["档案标识", p.runStatistics?.profileId],
      ["当前状态", state(p)[0]],
      ["角色", p.character || "未知"],
      ["最近已计算战斗", p.encounter || "尚无完整计算"],
      [
        "战斗采集时间",
        p.battleUpdatedAt
          ? new Date(p.battleUpdatedAt).toLocaleString()
          : "未知",
      ],
      [
        "预计战损",
        p.hpLoss === null ? "尚无完整预测" : p.hpLoss + " HP（预测值）",
      ],
      ["Mod 版本", p.version],
    ],
    p.runStatistics?.profileId || p.sessionId,
  );
}
function lookup(p) {
  const next = new URLSearchParams({
    source: "solver",
    participation: "all",
    sessionId: p.sessionId,
  });
  if (p.runStatistics?.profileId)
    next.set("profileId", p.runStatistics.profileId);
  fillForm(next);
  activeTab = "runs";
  displayTab();
  refreshRuns(next, 1);
}
function renderPlayers(data) {
  livePage = data.page;
  livePages = data.totalPages;
  liveUpdated = Date.now();
  tableRows(
    $("rows"),
    data.players,
    (p) => p.sessionId,
    (row, p) => {
      cell(row, 0, p.rank);
      const identity = cell(
        row,
        1,
        p.name || "未命名玩家",
        p.runStatistics?.profileId
          ? "档案 " + p.runStatistics.profileId.slice(0, 8)
          : "安装 " + p.sessionId.slice(0, 8),
      );
      identity.title = p.name || "";
      const status = cell(row, 2, state(p)[0]);
      status.children[0].className = "badge " + state(p)[1];
      cell(row, 3, duration(p.onlineSeconds));
      cell(row, 4, p.character || "未知");
      cell(row, 5, p.floor ?? "—");
      const battle = cell(
        row,
        6,
        p.encounter || "等待首次完整计算",
        p.inCombat ? "当前处于战斗" : "最近一次结果 · 非当前战斗",
      );
      battle.title = p.encounter || "";
      cell(
        row,
        7,
        p.hpLoss === null ? "未知" : p.hpLoss + " HP",
        "",
        p.hpLoss === 0 ? "zero-loss" : "loss-value",
      );
      cell(row, 8, p.version);
      cell(
        row,
        9,
        Math.max(0, Math.floor((data.now - p.lastSeen) / 1000)) + " 秒前",
      );
      if (!row.children[10]) {
        const td = el("td"),
          actions = el("div", undefined, "row-actions");
        const detail = el("button", "详情"),
          stats = el("button", "战绩");
        detail.addEventListener("click", () => liveDetails(row.record));
        stats.addEventListener("click", () => lookup(row.record));
        actions.append(detail, stats);
        td.append(actions);
        row.append(td);
      }
    },
  );
  $("empty").hidden = data.players.length > 0;
  setText($("empty"), liveQuery ? "没有匹配的在线玩家" : "当前没有在线玩家");
  setText(
    $("page-range"),
    `${data.total ? (data.page - 1) * data.pageSize + 1 : 0}–${data.total ? (data.page - 1) * data.pageSize + data.players.length : 0} / ${data.total} 位玩家`,
  );
  setText($("page-number"), `${data.page} / ${data.totalPages}`);
  $("previous-page").disabled = livePage <= 1;
  $("next-page").disabled = livePage >= livePages;
  sortHeaders("live", liveSort, liveOrder);
}
function sortHeaders(kind, sort, order) {
  for (const button of document.querySelectorAll(`[data-${kind}-sort]`)) {
    const selected = button.dataset[kind + "Sort"] === sort;
    button.parentElement.setAttribute(
      "aria-sort",
      selected ? (order === "asc" ? "ascending" : "descending") : "none",
    );
    setText(
      button.querySelector("span"),
      selected ? (order === "asc" ? "↑" : "↓") : "↕",
    );
  }
}
function refreshPlayers(page = livePage) {
  const query = new URLSearchParams({
    q: liveQuery,
    page: String(page),
    sort: liveSort,
    order: liveOrder,
  });
  return load("live", "/api/players?" + query, renderPlayers, "players-error");
}
function chipName(k, v) {
  if (k === "since" || k === "until")
    return new Date(Number(v)).toLocaleString();
  if (k === "sessionId" || k === "profileId") return v.slice(0, 8);
  const input = form.elements[k];
  if (input?.tagName === "SELECT") {
    const option = [...input.options].find((o) => o.value === v);
    if (option) return option.textContent;
  }
  return v;
}
function renderChips() {
  $("active-filters").replaceChildren();
  for (const [k, v] of applied) {
    const button = el("button", `${labels[k] || k}：${chipName(k, v)}`, "chip");
    button.type = "button";
    button.title = (labels[k] || k) + "：" + v;
    const removable = !["source", "participation"].includes(k);
    if (removable) {
      button.append(el("span", "×"));
      button.setAttribute("aria-label", "移除" + (labels[k] || k) + "条件");
      button.addEventListener("click", () => {
        const next = new URLSearchParams(applied);
        next.delete(k);
        fillForm(next);
        refreshRuns(next, 1);
      });
    } else button.disabled = true;
    $("active-filters").append(button);
  }
}
function runDetails(entry) {
  const s = entry.statistics;
  showDetails(
    entry.name,
    [
      ["安装标识", entry.sessionId],
      ["档案标识", entry.profileId],
      ["在线状态", entry.online ? "在线" : "离线"],
      [
        "数据来源",
        applied.get("source") === "historical"
          ? "游戏历史快照"
          : "求解器参与战绩",
      ],
      ["当前连续段", s.currentStreak ?? "跨角色未计"],
      ["最高连续段", s.bestStreak ?? "跨角色未计"],
      ["胜场", s.wins],
      ["负场", `${s.losses}（含 ${s.abandoned ?? "未知"} 次放弃）`],
      ["胜率 / 样本", `${rate(s.winRate)} / ${s.completedRuns} 局`],
      ["口径", "连续段受当前筛选约束，运行覆盖不等于 AI 独立通关。"],
    ],
    entry.profileId,
  );
}
function renderRuns(data) {
  runPage = data.page;
  runPages = data.totalPages;
  runsUpdated = Date.now();
  const historical = data.source === "historical";
  tableRows(
    $("run-rows"),
    data.entries,
    (p) => p.sessionId + ":" + p.profileId,
    (row, p) => {
      const s = p.statistics;
      cell(
        row,
        0,
        (p.online ? "● 在线 · " : "○ 离线 · ") + (p.name || "未命名玩家"),
        "档案 " + p.profileId.slice(0, 8),
      );
      cell(
        row,
        1,
        s.currentStreak ?? "—",
        s.currentStreak === null ? "跨角色未计" : "",
      );
      cell(
        row,
        2,
        s.bestStreak ?? "—",
        s.bestStreak === null ? "跨角色未计" : "",
      );
      cell(row, 3, s.wins);
      cell(
        row,
        4,
        `${s.losses} 负`,
        s.abandoned === null ? "放弃次数未知" : `含 ${s.abandoned} 次放弃`,
      );
      cell(row, 5, rate(s.winRate), `样本：${s.completedRuns} 局`);
      if (!row.children[6]) {
        const td = el("td"),
          button = el("button", "详情", "detail-button");
        button.addEventListener("click", () => runDetails(row.record));
        td.append(button);
        row.append(td);
      }
    },
  );
  setText(
    $("run-summary"),
    `${data.total} 个档案 · ${data.wins} 胜 / ${data.losses} 负 · 总胜率 ${rate(data.winRate)}`,
  );
  setText(
    $("statistics-semantics"),
    historical
      ? "原生存档首次快照，非求解器战绩。跨角色连胜未知；总胜率按当前筛选范围的胜负总和计算。"
      : "运行覆盖不等于 AI 独立通关。连续段受筛选条件约束；被排除的跑局会打断连续段。总胜率按当前筛选范围的胜负总和计算。",
  );
  setText($("runs-updated"), new Date(data.now).toLocaleTimeString());
  $("run-empty").hidden = data.entries.length > 0;
  setText(
    $("run-range"),
    `${data.total ? (data.page - 1) * data.pageSize + 1 : 0}–${data.total ? (data.page - 1) * data.pageSize + data.entries.length : 0} / ${data.total} 个档案`,
  );
  setText($("run-page"), `${runPage} / ${runPages}`);
  $("run-prev").disabled = runPage <= 1;
  $("run-next").disabled = runPage >= runPages;
  sortHeaders("run", runSort, runOrder);
}
function refreshRuns(
  candidate = applied,
  page = runPage,
  sort = runSort,
  order = runOrder,
) {
  const frozen = new URLSearchParams(candidate);
  const query = new URLSearchParams(frozen);
  query.set("page", page);
  query.set("sort", sort);
  query.set("order", order);
  return load(
    "runs",
    "/api/run-statistics?" + query,
    (data) => {
      applied = frozen;
      runSort = sort;
      runOrder = order;
      renderRuns(data);
      renderChips();
      markDirty();
      saveState();
    },
    "run-error",
  );
}
function renderChart() {
  if (!$("trend").open || !overviewData) return;
  const data = overviewData,
    points = [];
  for (const p of data.history) {
    points.push({
      x: p.time,
      y: p.count,
      start: p.start,
      end: p.end,
      samples: p.samples,
    });
  }
  $("history-empty").hidden = points.length > 0;
  if (!chart) {
    chart = new Chart($("chart"), {
      type: "line",
      data: {
        datasets: [
          {
            label: "在线人数",
            data: points,
            borderColor: "#237c62",
            backgroundColor: "#237c6210",
            fill: true,
            borderWidth: 2,
            pointRadius: points.length === 1 ? 3 : 0,
            spanGaps: true,
            cubicInterpolationMode: "monotone",
          },
        ],
      },
      options: {
        animation: false,
        maintainAspectRatio: false,
        parsing: false,
        interaction: { mode: "nearest", axis: "x", intersect: false },
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              title: (items) => new Date(items[0].raw.start).toLocaleString(),
              label: (item) =>
                `${item.raw.samples > 1 ? "平均在线" : "在线"}：${item.parsed.y.toLocaleString("zh-CN", { maximumFractionDigits: 1 })} 人`,
            },
          },
        },
        scales: {
          x: {
            type: "linear",
            ticks: {
              maxTicksLimit: 6,
              callback: (value) =>
                new Date(value).toLocaleString("zh-CN", {
                  month: "numeric",
                  day: "numeric",
                  hour: "2-digit",
                  minute: "2-digit",
                }),
            },
            grid: { display: false },
          },
          y: {
            beginAtZero: true,
            suggestedMax: 5,
            ticks: { precision: 0 },
            grid: { color: "#edf1ee" },
          },
        },
      },
    });
  } else {
    chart.data.datasets[0].data = points;
    chart.data.datasets[0].pointRadius = points.length === 1 ? 3 : 0;
  }
  chart.options.scales.x.min = data.now - Number($("range").value) * 3600000;
  chart.options.scales.x.max = data.now;
  chart.update();
}
function renderOverview(data) {
  renderComparisons(data);
  authenticated = true;
  overviewData = data;
  overviewUpdated = Date.now();
  $("session-loading").hidden = true;
  $("login").hidden = true;
  $("dashboard").hidden = false;
  $("logout").hidden = false;
  $("refresh-controls").hidden = false;
  setText($("connection"), "已连接");
  const recovering = data.samplingReady === false;
  setText($("online"), recovering ? "恢复中" : data.onlineCount);
  setText($("live-count"), recovering ? "恢复中" : data.onlineCount);
  setText($("in-combat"), recovering ? "恢复中" : data.fightingCount);
  setText($("in-run"), recovering ? "恢复中" : data.inRunCount);
  setText($("peak"), recovering ? (data.history.length ? data.historyPeak : "—") : Math.max(data.onlineCount, data.historyPeak));
  setText($("unknown-run"), recovering
    ? `心跳恢复中，约 ${Math.max(0, Math.ceil((data.samplingReadyAt - data.now) / 1000))} 秒后恢复采样`
    : `跑局状态未知：${data.runStatusUnknownCount} 人`);
  setText($("updated"), new Date(data.now).toLocaleTimeString());
  setText(
    $("trend-caption"),
    $("range").selectedOptions[0].textContent +
      ($("trend").open ? "" : " · 点击展开"),
  );
  renderChart();
}
function refreshOverview() {
  const width = $("chart").parentElement.clientWidth || 800;
  return load(
    "overview",
    `/api/overview?hours=${$("range").value}&maxPoints=${Math.max(32, Math.min(240, Math.floor(width / 6)))}`,
    renderOverview,
    "error",
  );
}
function renderRelease(data) {
  setText($("release-current"), data.latestVersion ? `当前发布：${data.latestVersion}` : "更新提醒已关闭");
}
function renderComparisons(data) {
  const number=value=>value.toLocaleString('zh-CN',{maximumFractionDigits:1});
  const signed=value=>(value>0?'+':'')+number(value);
  const comparisons=data.comparisons;
  setText($('comparison-period'),$('range').selectedOptions[0].textContent+'平均在线');
  setText($('period-average'),comparisons?.average==null?'暂无数据':number(comparisons.average)+' 人');
  setText($('period-coverage'),comparisons?`有效采样 ${comparisons.sampledMinutes}/${comparisons.expectedMinutes} 分钟`:'等待采样');
  for(const key of ['previousPeriod','previousDay','previousWeek']) {
    const c=comparisons?.[key],node=$(key+'-change');
    setText(node,c?.delta==null?'数据不足':`${signed(c.delta)} 人 · ${c.percent==null?'基期为 0':signed(c.percent)+'%'}`);
    node.className=c?.delta>0?'change-up':c?.delta<0?'change-down':'';
    setText($(key+'-detail'),c?.delta==null?'暂无共同采样分钟':`${number(c.previousAverage)} → ${number(c.currentAverage)} 人 · 配对覆盖 ${number(c.coverage*100)}%`);
    $(key+'-detail').title=c?`${new Date(c.start).toLocaleString()} 至 ${new Date(c.end).toLocaleString()}；配对 ${c.pairedMinutes}/${c.expectedMinutes} 分钟`:'';
  }
  const w=data.workshop;
  setText($('workshop-count'),w?.subscriptions==null?'暂无数据':w.subscriptions.toLocaleString('zh-CN'));
  setText($('workshop-updated'),w?.updatedAt?`${w.stale?'更新延迟 · ':''}${new Date(w.updatedAt).toLocaleString()}`:'等待 Steam 数据');
}
function refreshRelease() {
  if (requests.has("release-save")) return;
  return load("release", "/api/release", renderRelease, "release-error");
}
async function saveRelease(latestVersion) {
  requests.get("release")?.abort();
  requests.get("release-save")?.abort();
  const request = new AbortController();
  requests.set("release-save", request);
  $("release-save").disabled = $("release-clear").disabled = true;
  try {
    const data = await api("/api/release", {method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({latestVersion}), signal:request.signal});
    if (request.signal.aborted) return;
    renderRelease(data);
    $("release-error").hidden = true;
    setText($("release-message"), latestVersion ? "已保存，将随下次心跳提醒旧版客户端。" : "已关闭更新提醒。");
  } catch (error) {
    if (request.signal.aborted) return;
    $("release-error").hidden = false;
    setText($("release-error"), `保存失败：${error.message}`);
  } finally {
    if (requests.get("release-save") === request) requests.delete("release-save");
    $("release-save").disabled = $("release-clear").disabled = false;
  }
}
$("release-form").addEventListener("submit", event => {event.preventDefault();saveRelease($("release-version").value.trim());});
$("release-clear").addEventListener("click", () => saveRelease(null));

function displayTab() {
  for (const name of ["live", "runs"]) {
    const active = activeTab === name;
    $("panel-" + name).hidden = !active;
    $("tab-" + name).setAttribute("aria-selected", String(active));
    $("tab-" + name).tabIndex = active ? 0 : -1;
  }
}
async function switchTab(name) {
  activeTab = name;
  displayTab();
  saveState();
  if (authenticated) {
    if (name === "live") await refreshPlayers();
    else if (!dirty() || !runsUpdated) await refreshRuns();
  }
}
function reading() {
  return (
    $("details-dialog").open ||
    Boolean(window.getSelection()?.toString()) ||
    form.contains(document.activeElement) ||
    $("release-form").contains(document.activeElement) ||
    $("live-table").contains(document.activeElement) ||
    $("run-table").contains(document.activeElement) ||
    (activeTab === "runs" && dirty()) ||
    $("search").value.trim() !== liveQuery
  );
}
async function refreshNow() {
  if (!authenticated) {
    await bootstrap();
    return;
  }
  await Promise.all([
    refreshOverview(),
    refreshRelease(),
    activeTab === "live" ? refreshPlayers() : refreshRuns(),
  ]);
}
async function bootstrap() {
  const success = await refreshOverview();
  if (success && authenticated)
    await Promise.all([refreshRelease(), activeTab === "live" ? refreshPlayers() : refreshRuns()]);
}
function tick() {
  setText($("auto-refresh"), automatic ? "暂停刷新" : "继续刷新");
  $("auto-refresh").setAttribute("aria-pressed", String(automatic));
  if (!authenticated) return;
  const key = activeTab === "live" ? "live" : "runs",
    interval = activeTab === "live" ? 10000 : 60000,
    last = attempts.get(key) || 0;
  const held = reading() || document.hidden;
  setText(
    $("refresh-status"),
    !automatic
      ? "自动刷新已暂停"
      : held
        ? "正在阅读或编辑 · 暂缓刷新"
        : `下次更新 ${Math.max(0, Math.ceil((interval - (Date.now() - last)) / 1000))} 秒后`,
  );
  if (!automatic || held) return;
  if (
    Date.now() - (attempts.get("overview") || 0) >= 10000 &&
    !requests.has("overview")
  )
    refreshOverview();
  if (Date.now() - last >= interval && !requests.has(key)) {
    if (activeTab === "live") refreshPlayers();
    else refreshRuns();
  }
}
$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  button.disabled = true;
  setText($("login-error"), "");
  try {
    await api("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ password: $("password").value }),
    });
    $("password").value = "";
    $("login").hidden = true;
    $("session-loading").hidden = false;
    await bootstrap();
  } catch (error) {
    setText($("login-error"), error.message);
  } finally {
    button.disabled = false;
  }
});
$("logout").addEventListener("click", async () => {
  try {
    await api("/api/logout", { method: "POST" });
    loggedOut();
  } catch (error) {
    $("error").hidden = false;
    setText($("error"), error.message);
  }
});
$("session-retry").addEventListener("click", () => {
  $("session-retry").hidden = true;
  bootstrap();
});
$("auto-refresh").addEventListener("click", () => {
  automatic = !automatic;
  if (!automatic) {
    for (const request of requests.values()) request.abort();
    requests.clear();
  }
  try {
    localStorage.setItem("cs-monitor-auto", automatic ? "on" : "off");
  } catch {}
  tick();
});
$("refresh-now").addEventListener("click", refreshNow);
for (const name of ["live", "runs"]) {
  $("tab-" + name).addEventListener("click", () => switchTab(name));
  $("tab-" + name).addEventListener("keydown", (event) => {
    if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
      event.preventDefault();
      const target =
        event.key === "Home"
          ? "live"
          : event.key === "End"
            ? "runs"
            : name === "live"
              ? "runs"
              : "live";
      $("tab-" + target).focus();
      switchTab(target);
    }
  });
}
$("live-filters").addEventListener("submit", (event) => {
  event.preventDefault();
  liveQuery = $("search").value.trim();
  saveState();
  refreshPlayers(1);
});
$("live-reset").addEventListener("click", () => {
  $("search").value = "";
  liveQuery = "";
  saveState();
  refreshPlayers(1);
});
for (const button of document.querySelectorAll("[data-live-sort]"))
  button.addEventListener("click", () => {
    const value = button.dataset.liveSort;
    liveOrder = value === liveSort && liveOrder === "desc" ? "asc" : "desc";
    liveSort = value;
    saveState();
    refreshPlayers(1);
  });
for (const button of document.querySelectorAll("[data-run-sort]"))
  button.addEventListener("click", () => {
    const value = button.dataset.runSort;
    refreshRuns(
      applied,
      1,
      value,
      value === runSort && runOrder === "desc" ? "asc" : "desc",
    );
  });
form.addEventListener("input", () => {
  updateHistoricalControls();
  markDirty();
});
form.addEventListener("change", () => {
  updateHistoricalControls();
  markDirty();
});
form.addEventListener("submit", (event) => {
  event.preventDefault();
  refreshRuns(draft(), 1);
});
form.addEventListener("reset", () => {
  setTimeout(() => {
    updateHistoricalControls();
    markDirty();
    refreshRuns(draft(), 1);
  }, 0);
});
$("previous-page").addEventListener("click", () =>
  refreshPlayers(livePage - 1),
);
$("next-page").addEventListener("click", () => refreshPlayers(livePage + 1));
$("run-prev").addEventListener("click", () =>
  refreshRuns(applied, runPage - 1),
);
$("run-next").addEventListener("click", () =>
  refreshRuns(applied, runPage + 1),
);
$("range").addEventListener("change", () => {
  saveState();
  refreshOverview();
});
$("trend").addEventListener("toggle", () => {
  try {
    localStorage.setItem(
      "cs-monitor-trend",
      $("trend").open ? "open" : "closed",
    );
  } catch {}
  setText(
    $("trend-caption"),
    $("range").selectedOptions[0].textContent +
      ($("trend").open ? "" : " · 点击展开"),
  );
  renderChart();
});
$("close-details").addEventListener("click", () => $("details-dialog").close());
$("copy-identity").addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(copyIdentity);
    setText($("copy-status"), "已复制完整标识");
  } catch {
    setText($("copy-status"), "无法自动复制，请选中上方完整标识复制。");
  }
});
restoreState();
tick();
bootstrap();
setInterval(tick, 1000);
