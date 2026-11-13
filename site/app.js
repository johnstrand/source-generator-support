const DATA_URL = "./data/sdk-roslyn-versions.json";

const state = {
  catalog: null,
  view: "bands", // bands | channels | sdks
  showEol: true,
  showPreview: false,
  search: "",
  expandedBands: new Set(),
  expandedChannels: new Set(),
  sortKey: "sdkVersion",
  sortDir: "desc",
  helperTab: "single",
  selectedMinBand: "",
  selectedMultiTiers: new Set(),
};

const els = {
  statsBar: document.getElementById("stats-bar"),
  themeToggle: document.getElementById("theme-toggle"),
  minBandSelect: document.getElementById("min-band-select"),
  singleResult: document.getElementById("single-result"),
  singleSnippet: document.getElementById("single-snippet"),
  multiTiersList: document.getElementById("multi-tiers-list"),
  multiSnippet: document.getElementById("multi-snippet"),
  helperSingle: document.getElementById("helper-single"),
  helperMulti: document.getElementById("helper-multi"),
  searchInput: document.getElementById("search-input"),
  toggleEol: document.getElementById("toggle-eol"),
  togglePreview: document.getElementById("toggle-preview"),
  expandAllBtn: document.getElementById("expand-all-btn"),
  tableStatus: document.getElementById("table-status"),
  tableHead: document.getElementById("table-head"),
  tableBody: document.getElementById("table-body"),
  toast: document.getElementById("toast"),
};

initTheme();
readUrlParams();
wireEvents();
loadData();

function initTheme() {
  const saved = localStorage.getItem("theme-pref") || "auto";
  applyTheme(saved);
}

function applyTheme(mode) {
  if (mode === "dark" || mode === "light") {
    document.documentElement.setAttribute("data-theme", mode);
  } else {
    document.documentElement.removeAttribute("data-theme");
  }
  localStorage.setItem("theme-pref", mode);
  els.themeToggle.textContent = `Theme: ${mode[0].toUpperCase()}${mode.slice(1)}`;
}

function cycleTheme() {
  const cur = localStorage.getItem("theme-pref") || "auto";
  const next = cur === "auto" ? "dark" : cur === "dark" ? "light" : "auto";
  applyTheme(next);
}

function readUrlParams() {
  const params = new URLSearchParams(window.location.search);
  const view = params.get("view");
  if (view === "bands" || view === "channels" || view === "sdks") {
    state.view = view;
  }
  if (params.has("eol")) {
    state.showEol = params.get("eol") !== "0";
  }
  if (params.has("preview")) {
    state.showPreview = params.get("preview") === "1";
  }
  if (params.has("q")) {
    state.search = params.get("q") || "";
  }
  syncControlsFromState();
}

function writeUrlParams() {
  const params = new URLSearchParams();
  if (state.view !== "bands") params.set("view", state.view);
  if (!state.showEol) params.set("eol", "0");
  if (state.showPreview) params.set("preview", "1");
  if (state.search.trim()) params.set("q", state.search.trim());
  const qs = params.toString();
  const nextUrl = qs ? `${window.location.pathname}?${qs}` : window.location.pathname;
  window.history.replaceState(null, "", nextUrl);
}

function syncControlsFromState() {
  els.toggleEol.checked = state.showEol;
  els.togglePreview.checked = state.showPreview;
  els.searchInput.value = state.search;
  document.querySelectorAll("[data-view]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.view === state.view);
  });
}

function wireEvents() {
  els.themeToggle.addEventListener("click", cycleTheme);

  document.querySelectorAll("[data-view]").forEach((btn) => {
    btn.addEventListener("click", () => {
      state.view = btn.dataset.view;
      syncControlsFromState();
      writeUrlParams();
      renderTable();
    });
  });

  document.querySelectorAll("[data-helper-tab]").forEach((btn) => {
    btn.addEventListener("click", () => {
      state.helperTab = btn.dataset.helperTab;
      document.querySelectorAll("[data-helper-tab]").forEach((b) => {
        const active = b.dataset.helperTab === state.helperTab;
        b.classList.toggle("active", active);
        b.setAttribute("aria-selected", String(active));
      });
      els.helperSingle.classList.toggle("hidden", state.helperTab !== "single");
      els.helperMulti.classList.toggle("hidden", state.helperTab !== "multi");
    });
  });

  els.toggleEol.addEventListener("change", () => {
    state.showEol = els.toggleEol.checked;
    writeUrlParams();
    renderTable();
  });

  els.togglePreview.addEventListener("change", () => {
    state.showPreview = els.togglePreview.checked;
    writeUrlParams();
    renderTable();
  });

  els.searchInput.addEventListener("input", () => {
    state.search = els.searchInput.value;
    writeUrlParams();
    renderTable();
  });

  els.expandAllBtn.addEventListener("click", () => {
    if (state.view === "bands") {
      const visible = getFilteredBandRows();
      const allExpanded = visible.length > 0 && visible.every((r) => state.expandedBands.has(r.band.band));
      if (allExpanded) {
        state.expandedBands.clear();
      } else {
        visible.forEach((r) => state.expandedBands.add(r.band.band));
      }
      renderTable();
    } else if (state.view === "channels") {
      const visible = getFilteredChannels();
      const allExpanded = visible.length > 0 && visible.every((c) => state.expandedChannels.has(c.channelVersion));
      if (allExpanded) {
        state.expandedChannels.clear();
      } else {
        visible.forEach((c) => state.expandedChannels.add(c.channelVersion));
      }
      renderTable();
    }
  });

  els.minBandSelect.addEventListener("change", () => {
    state.selectedMinBand = els.minBandSelect.value;
    renderSingleHelper();
  });

  document.querySelectorAll(".copy-btn").forEach((btn) => {
    btn.addEventListener("click", () => {
      const target = document.getElementById(btn.dataset.copyTarget);
      if (target) {
        copyText(target.textContent || "", "Copied snippet to clipboard");
      }
    });
  });
}

async function loadData() {
  try {
    const resp = await fetch(DATA_URL, { cache: "no-store" });
    if (!resp.ok) {
      throw new Error(`HTTP ${resp.status} loading ${DATA_URL}`);
    }
    state.catalog = await resp.json();
    renderStats();
    initHelpers();
    renderTable();
  } catch (err) {
    els.tableStatus.classList.remove("hidden");
    els.tableStatus.textContent = `Unable to load dataset: ${err.message}`;
  }
}

function renderStats() {
  const c = state.catalog;
  const channels = c.channels || [];
  const totalBands = channels.reduce((acc, ch) => acc + ch.featureBands.length, 0);
  const totalSdks = channels.reduce((acc, ch) => acc + ch.featureBands.reduce((a, b) => a + b.sdks.length, 0), 0);
  const activeCount = channels.filter((ch) => ch.supportPhase !== "eol").length;
  const updated = c.lastUpdatedUtc ? new Date(c.lastUpdatedUtc).toISOString().replace("T", " ").slice(0, 16) + " UTC" : "Not yet generated";

  els.statsBar.innerHTML = "";
  const pills = [
    `Updated: ${updated}`,
    `${channels.length} .NET Channels (${activeCount} active)`,
    `${totalBands} Feature Bands`,
    `${totalSdks} SDK Releases`,
  ];
  for (const label of pills) {
    const span = document.createElement("span");
    span.className = "stat-pill";
    span.textContent = label;
    els.statsBar.appendChild(span);
  }
}

function getAllBandRows(includeEol = true, includePreview = true) {
  const rows = [];
  for (const channel of state.catalog?.channels || []) {
    const isEol = channel.supportPhase === "eol";
    if (!includeEol && isEol) continue;
    for (const band of channel.featureBands || []) {
      if (!includePreview && band.isPrereleaseOnly) continue;
      rows.push({ channel, band });
    }
  }
  return rows;
}

function initHelpers() {
  const allBands = getAllBandRows(true, false);
  if (allBands.length === 0) return;

  els.minBandSelect.innerHTML = "";
  for (const { channel, band } of allBands) {
    const opt = document.createElement("option");
    opt.value = band.band;
    const vs = band.vsSupport || (band.vsVersion ? `VS/MSBuild ${band.vsVersion}` : "");
    const eolTag = channel.supportPhase === "eol" ? " [EOL]" : "";
    const nugetTag = band.codeAnalysisCSharpNuGetVersion || band.codeAnalysisCSharpPrereleaseNuGetVersion || "?";
    opt.textContent = `.NET ${band.band}${eolTag} — NuGet ${nugetTag}${vs ? ` (${vs})` : ""}`;
    els.minBandSelect.appendChild(opt);
  }

  // Default to oldest active or .NET 8.0.1xx if present so users see a realistic baseline.
  const preferred = allBands.find((r) => r.band.band.startsWith("8.0.1")) || allBands[allBands.length - 1];
  state.selectedMinBand = preferred.band.band;
  els.minBandSelect.value = state.selectedMinBand;
  renderSingleHelper();

  // Multi-Roslyn tier chips: walk oldest -> newest so each Roslyn Major.Minor maps to its minimum introducing SDK band.
  const tiersByMinor = new Map();
  for (const { band } of [...allBands].reverse()) {
    const nugetVer = band.codeAnalysisCSharpNuGetVersion;
    if (!nugetVer) continue;
    const minorKey = toMajorMinor(nugetVer);
    const existing = tiersByMinor.get(minorKey);
    if (!existing) {
      tiersByMinor.set(minorKey, {
        minorKey,
        nugetVer,
        band: band.band,
        vsSupport: band.vsSupport || band.vsVersion || "",
      });
    } else if (compareVersions(nugetVer, existing.nugetVer) > 0) {
      existing.nugetVer = nugetVer;
    }
  }
  const tiers = [...tiersByMinor.values()].reverse();

  // Pre-select up to 3 representative tiers (latest, ~4.8/4.12/4.0 style spacing).
  if (state.selectedMultiTiers.size === 0 && tiers.length > 0) {
    state.selectedMultiTiers.add(tiers[0].minorKey);
    const mid = tiers.find((t) => t.minorKey === "4.8" || t.minorKey === "4.12");
    if (mid) state.selectedMultiTiers.add(mid.minorKey);
    state.selectedMultiTiers.add(tiers[tiers.length - 1].minorKey);
  }

  els.multiTiersList.innerHTML = "";
  for (const tier of tiers) {
    const label = document.createElement("label");
    label.className = "tier-chip";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = state.selectedMultiTiers.has(tier.minorKey);
    cb.addEventListener("change", () => {
      if (cb.checked) state.selectedMultiTiers.add(tier.minorKey);
      else state.selectedMultiTiers.delete(tier.minorKey);
      renderMultiHelper(tiers);
    });
    const text = document.createElement("span");
    text.textContent = `roslyn${tier.minorKey} (NuGet ${tier.nugetVer} · ${tier.band})`;
    label.append(cb, text);
    els.multiTiersList.appendChild(label);
  }

  renderMultiHelper(tiers);
}

function renderSingleHelper() {
  const allBands = getAllBandRows(true, true);
  const selectedIdx = allBands.findIndex((r) => r.band.band === state.selectedMinBand);
  if (selectedIdx < 0) return;

  const { channel, band } = allBands[selectedIdx];
  const coveredBands = allBands.slice(0, selectedIdx + 1);
  let constraining = band;
  for (const item of coveredBands) {
    if (
      item.band.roslynAssemblyVersion &&
      compareVersions(item.band.roslynAssemblyVersion, constraining.roslynAssemblyVersion) < 0
    ) {
      constraining = item.band;
    }
  }

  const nugetVer =
    constraining.codeAnalysisCSharpNuGetVersion ||
    constraining.codeAnalysisCSharpPrereleaseNuGetVersion ||
    "N/A";
  const lagNote = band.nugetLag
    ? `<span class="badge badge-lag">SDK ships ${escapeHtml(band.roslynAssemblyVersion)}; max NuGet today is ${escapeHtml(nugetVer)}</span>`
    : "";

  els.singleResult.innerHTML = `
    <span><strong>Max Safe NuGet:</strong> <code>${escapeHtml(nugetVer)}</code></span>
    <span><strong>Constraining AssemblyVersion:</strong> <code>${escapeHtml(constraining.roslynAssemblyVersion)}</code></span>
    <span><strong>Channel:</strong> .NET ${escapeHtml(channel.channelVersion)} (${escapeHtml(channel.supportPhase.toUpperCase())})</span>
    ${lagNote}
  `;

  els.singleSnippet.textContent = [
    `<Project Sdk="Microsoft.NET.Sdk">`,
    `  <PropertyGroup>`,
    `    <TargetFramework>netstandard2.0</TargetFramework>`,
    `    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`,
    `  </PropertyGroup>`,
    ``,
    `  <ItemGroup>`,
    `    <!-- Safe for .NET SDK ${band.band} (${band.vsSupport || band.vsVersion || "MSBuild " + (band.msbuildVersion || "")}) and newer -->`,
    `    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="${nugetVer}" PrivateAssets="all" />`,
    `  </ItemGroup>`,
    `</Project>`,
  ].join("\n");
}

function renderMultiHelper(tiers) {
  const chosen = tiers.filter((t) => state.selectedMultiTiers.has(t.minorKey));
  if (chosen.length === 0) {
    els.multiSnippet.textContent = "<!-- Select at least one Roslyn tier above to generate a multi-targeting snippet. -->";
    return;
  }

  const sorted = [...chosen].sort((a, b) => compareVersions(a.minorKey, b.minorKey));
  const lines = [
    `<!-- Build separate analyzer assemblies per Roslyn tier (pack into analyzers/dotnet/roslyn<ver>/cs) -->`,
    `<PropertyGroup>`,
    `  <RoslynTargetVersion Condition="'$(RoslynTargetVersion)' == ''">${sorted[0].minorKey}</RoslynTargetVersion>`,
    `  <DefineConstants>$(DefineConstants);ROSLYN_$(RoslynTargetVersion.Replace('.', '_'))</DefineConstants>`,
    `</PropertyGroup>`,
    ``,
    `<ItemGroup>`,
  ];

  for (const t of sorted) {
    lines.push(
      `  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="${t.nugetVer}" PrivateAssets="all" Condition="'$(RoslynTargetVersion)' == '${t.minorKey}'" />`
    );
  }

  lines.push(`</ItemGroup>`);
  lines.push(``);
  lines.push(`<!-- Pack each built DLL into its Roslyn-versioned analyzer folder:`);
  for (const t of sorted) {
    lines.push(`  analyzers/dotnet/roslyn${t.minorKey}/cs/YourAnalyzer.dll  (built with Roslyn ${t.nugetVer}, supports ${t.band}+)`);
  }
  lines.push(`-->`);

  els.multiSnippet.textContent = lines.join("\n");
}

function getFilteredBandRows() {
  const q = state.search.trim().toLowerCase();
  const rows = [];
  for (const { channel, band } of getAllBandRows(state.showEol, state.showPreview)) {
    const matchingSdks = (band.sdks || []).filter((sdk) => {
      if (!state.showPreview && sdk.isPrerelease && !band.isPrereleaseOnly) return false;
      return !q || matchesQuery(channel, band, sdk, q);
    });

    if (!q || matchesBandQuery(channel, band, q) || matchingSdks.length > 0) {
      rows.push({
        channel,
        band,
        sdks: matchingSdks.length > 0 ? matchingSdks : band.sdks,
      });
    }
  }
  return rows;
}

function getFilteredChannels() {
  const q = state.search.trim().toLowerCase();
  return (state.catalog?.channels || []).filter((ch) => {
    if (!state.showEol && ch.supportPhase === "eol") return false;
    const bands = (ch.featureBands || []).filter((b) => state.showPreview || !b.isPrereleaseOnly);
    if (bands.length === 0) return false;
    if (!q) return true;
    return (
      ch.channelVersion.toLowerCase().includes(q) ||
      ch.supportPhase.toLowerCase().includes(q) ||
      bands.some((b) => matchesBandQuery(ch, b, q))
    );
  });
}

function getFilteredFlatSdks() {
  const q = state.search.trim().toLowerCase();
  const rows = [];
  for (const channel of state.catalog?.channels || []) {
    if (!state.showEol && channel.supportPhase === "eol") continue;
    for (const band of channel.featureBands || []) {
      if (!state.showPreview && band.isPrereleaseOnly) continue;
      for (const sdk of band.sdks || []) {
        if (!state.showPreview && sdk.isPrerelease) continue;
        if (q && !matchesQuery(channel, band, sdk, q)) continue;
        rows.push({ channel, band, sdk });
      }
    }
  }

  rows.sort((a, b) => {
    const cmp = compareFlatRows(a, b, state.sortKey);
    return state.sortDir === "asc" ? cmp : -cmp;
  });
  return rows;
}

function matchesBandQuery(channel, band, q) {
  const haystack = [
    channel.channelVersion,
    channel.releaseType,
    channel.supportPhase,
    band.band,
    band.latestSdkVersion,
    band.roslynAssemblyVersion,
    band.codeAnalysisCSharpNuGetVersion,
    band.codeAnalysisCSharpPrereleaseNuGetVersion,
    band.vsSupport,
    band.vsVersion,
    band.msbuildVersion,
    band.csharpVersion,
    band.fsharpVersion,
    band.vbVersion,
  ]
    .filter(Boolean)
    .join(" ")
    .toLowerCase();
  return haystack.includes(q);
}

function matchesQuery(channel, band, sdk, q) {
  if (matchesBandQuery(channel, band, q)) return true;
  const haystack = [
    sdk.sdkVersion,
    sdk.runtimeVersion,
    sdk.releaseDate,
    sdk.codeAnalysisCSharp?.assemblyVersion,
    sdk.codeAnalysisCSharp?.fileVersion,
    sdk.codeAnalysisCSharp?.informationalVersion,
    sdk.maxCodeAnalysisCSharpNuGetVersion,
    sdk.resolvedVsSupport,
    sdk.resolvedVsVersion,
    sdk.msbuild?.fileVersion,
  ]
    .filter(Boolean)
    .join(" ")
    .toLowerCase();
  return haystack.includes(q);
}

function renderTable() {
  if (!state.catalog) return;

  els.expandAllBtn.classList.toggle("hidden", state.view === "sdks");

  if (state.view === "bands") {
    renderBandsTable();
  } else if (state.view === "channels") {
    renderChannelsTable();
  } else {
    renderFlatSdksTable();
  }
}

function renderBandsTable() {
  const rows = getFilteredBandRows();
  els.tableHead.innerHTML = `
    <tr>
      <th>.NET SDK Band</th>
      <th>Max NuGet (<code>Microsoft.CodeAnalysis.CSharp</code>)</th>
      <th>SDK <code>AssemblyVersion</code></th>
      <th>Visual Studio / MSBuild</th>
      <th>Languages</th>
      <th>Latest Patch &amp; Date</th>
    </tr>
  `;

  if (rows.length === 0) {
    showEmptyState("No feature bands match the current filters.");
    return;
  }
  hideEmptyState();

  const frag = document.createDocumentFragment();
  for (const row of rows) {
    const { channel, band, sdks } = row;
    const isExpanded = state.expandedBands.has(band.band);

    const tr = document.createElement("tr");
    tr.className = `band-row${isExpanded ? " expanded" : ""}`;
    tr.addEventListener("click", (e) => {
      if (e.target.closest("a, button")) return;
      if (state.expandedBands.has(band.band)) state.expandedBands.delete(band.band);
      else state.expandedBands.add(band.band);
      renderTable();
    });

    tr.innerHTML = `
      <td>
        <div class="cell-main">
          <span class="expand-caret">&#9654;</span>
          <strong><code>${escapeHtml(band.band)}</code></strong>
          ${renderStatusBadges(channel, band)}
        </div>
        <div class="cell-sub">${sdks.length} SDK release${sdks.length === 1 ? "" : "s"} &middot; .NET ${escapeHtml(channel.channelVersion)}</div>
      </td>
      <td>${renderNuGetCell(band.codeAnalysisCSharpNuGetVersion, band.codeAnalysisCSharpPrereleaseNuGetVersion, band.nugetLag, band.roslynAssemblyVersion)}</td>
      <td>
        <code>${escapeHtml(band.roslynAssemblyVersion)}</code>
        <div class="cell-sub">Latest file: ${escapeHtml(sdks[0]?.codeAnalysisCSharp?.fileVersion || "—")}</div>
      </td>
      <td>
        <div>${escapeHtml(band.vsSupport || "—")}</div>
        <div class="cell-sub">MSBuild ${escapeHtml(band.msbuildVersion || band.vsVersion || "—")}</div>
      </td>
      <td>
        <div>C# ${escapeHtml(band.csharpVersion || "—")}</div>
        <div class="cell-sub">F# ${escapeHtml(band.fsharpVersion || "—")} &middot; VB ${escapeHtml(band.vbVersion || "—")}</div>
      </td>
      <td>
        <div><code>${escapeHtml(band.latestSdkVersion)}</code></div>
        <div class="cell-sub">${escapeHtml(band.latestReleaseDate || "—")}</div>
      </td>
    `;
    frag.appendChild(tr);

    if (isExpanded) {
      for (const sdk of sdks) {
        frag.appendChild(buildSdkSubRow(sdk));
      }
    }
  }

  els.tableBody.innerHTML = "";
  els.tableBody.appendChild(frag);
}

function buildSdkSubRow(sdk) {
  const tr = document.createElement("tr");
  tr.className = "sub-row";
  const caDiff =
    sdk.codeAnalysis?.assemblyVersion &&
    sdk.codeAnalysis.assemblyVersion !== sdk.codeAnalysisCSharp?.assemblyVersion
      ? `Core ${sdk.codeAnalysis.assemblyVersion} / C# ${sdk.codeAnalysisCSharp?.assemblyVersion}`
      : sdk.codeAnalysisCSharp?.assemblyVersion || "—";

  const commitHtml = sdk.codeAnalysisCSharp?.commitSha
    ? ` &middot; <a href="https://github.com/dotnet/roslyn/commit/${encodeURIComponent(sdk.codeAnalysisCSharp.commitSha)}" target="_blank" rel="noopener">${escapeHtml(sdk.codeAnalysisCSharp.commitSha.slice(0, 7))}</a>`
    : "";

  const notesHtml = sdk.releaseNotes
    ? `<a href="${escapeHtml(sdk.releaseNotes)}" target="_blank" rel="noopener">Release notes</a>`
    : "";

  const secBadge = sdk.security ? `<span class="badge badge-sec">Security</span>` : "";
  const preBadge = sdk.isPrerelease ? `<span class="badge badge-preview">Preview/RC</span>` : "";

  tr.innerHTML = `
    <td class="sub-indent">
      <div class="cell-main">
        <code>${escapeHtml(sdk.sdkVersion)}</code>
        ${preBadge}
        ${secBadge}
      </div>
      <div class="cell-sub">Runtime ${escapeHtml(sdk.runtimeVersion || "—")}${notesHtml ? " &middot; " + notesHtml : ""}</div>
    </td>
    <td>${renderNuGetCell(sdk.maxCodeAnalysisCSharpNuGetVersion, sdk.maxCodeAnalysisCSharpPrereleaseNuGetVersion, sdk.nugetLag, sdk.codeAnalysisCSharp?.assemblyVersion)}</td>
    <td>
      <div><code>${escapeHtml(caDiff)}</code></div>
      <div class="cell-sub">File ${escapeHtml(sdk.codeAnalysisCSharp?.fileVersion || "—")}${commitHtml}</div>
    </td>
    <td>
      <div>${escapeHtml(sdk.resolvedVsSupport || sdk.vsSupport || "—")}</div>
      <div class="cell-sub">MSBuild ${escapeHtml(sdk.msbuild?.fileVersion || sdk.resolvedVsVersion || "—")}</div>
    </td>
    <td>
      <div>C# ${escapeHtml(sdk.csharpVersion || "—")}</div>
      <div class="cell-sub">F# ${escapeHtml(sdk.fsharpVersion || "—")} &middot; VB ${escapeHtml(sdk.vbVersion || "—")}</div>
    </td>
    <td>
      <div>${escapeHtml(sdk.releaseDate || "—")}</div>
      <div class="cell-sub">Release ${escapeHtml(sdk.releaseVersion || "—")}</div>
    </td>
  `;
  return tr;
}

function renderChannelsTable() {
  const channels = getFilteredChannels();
  els.tableHead.innerHTML = `
    <tr>
      <th>.NET Channel</th>
      <th>Support &amp; EOL</th>
      <th>Feature Bands</th>
      <th>Roslyn Range (NuGet / Assembly)</th>
      <th>Latest SDK</th>
    </tr>
  `;

  if (channels.length === 0) {
    showEmptyState("No .NET channels match the current filters.");
    return;
  }
  hideEmptyState();

  const frag = document.createDocumentFragment();
  for (const ch of channels) {
    const bands = (ch.featureBands || []).filter((b) => state.showPreview || !b.isPrereleaseOnly);
    const oldest = bands[bands.length - 1];
    const newest = bands[0];
    const isExpanded = state.expandedChannels.has(ch.channelVersion);

    const tr = document.createElement("tr");
    tr.className = `band-row${isExpanded ? " expanded" : ""}`;
    tr.addEventListener("click", (e) => {
      if (e.target.closest("a, button")) return;
      if (state.expandedChannels.has(ch.channelVersion)) state.expandedChannels.delete(ch.channelVersion);
      else state.expandedChannels.add(ch.channelVersion);
      renderTable();
    });

    const minNuget = oldest?.codeAnalysisCSharpNuGetVersion || oldest?.roslynAssemblyVersion || "—";
    const maxNuget = newest?.codeAnalysisCSharpNuGetVersion || newest?.roslynAssemblyVersion || "—";
    const rangeLabel = minNuget === maxNuget ? minNuget : `${minNuget} &rarr; ${maxNuget}`;

    tr.innerHTML = `
      <td>
        <div class="cell-main">
          <span class="expand-caret">&#9654;</span>
          <strong>.NET ${escapeHtml(ch.channelVersion)}</strong>
          ${renderStatusBadges(ch, null)}
        </div>
      </td>
      <td>
        <div>${escapeHtml(ch.supportPhase.toUpperCase())}</div>
        <div class="cell-sub">EOL: ${escapeHtml(ch.eolDate || "TBD")}</div>
      </td>
      <td>${bands.length} band${bands.length === 1 ? "" : "s"} (${bands.reduce((a, b) => a + b.sdks.length, 0)} SDKs)</td>
      <td>
        <div><code>${rangeLabel}</code></div>
        <div class="cell-sub">AssemblyVersion ${escapeHtml(oldest?.roslynAssemblyVersion || "—")} &rarr; ${escapeHtml(newest?.roslynAssemblyVersion || "—")}</div>
      </td>
      <td><code>${escapeHtml(ch.latestSdk)}</code></td>
    `;
    frag.appendChild(tr);

    if (isExpanded) {
      for (const b of bands) {
        const sub = document.createElement("tr");
        sub.className = "sub-row";
        sub.innerHTML = `
          <td class="sub-indent"><code>${escapeHtml(b.band)}</code></td>
          <td>${escapeHtml(b.latestReleaseDate || "—")}</td>
          <td>${b.sdks.length} SDKs &middot; ${escapeHtml(b.vsSupport || b.vsVersion || "—")}</td>
          <td>${renderNuGetCell(b.codeAnalysisCSharpNuGetVersion, b.codeAnalysisCSharpPrereleaseNuGetVersion, b.nugetLag, b.roslynAssemblyVersion)}</td>
          <td><code>${escapeHtml(b.latestSdkVersion)}</code></td>
        `;
        frag.appendChild(sub);
      }
    }
  }

  els.tableBody.innerHTML = "";
  els.tableBody.appendChild(frag);
}

function renderFlatSdksTable() {
  const rows = getFilteredFlatSdks();
  const sortArrow = (key) => (state.sortKey === key ? (state.sortDir === "asc" ? " ▲" : " ▼") : "");

  els.tableHead.innerHTML = `
    <tr>
      <th class="sortable" data-sort="sdkVersion">SDK Version${sortArrow("sdkVersion")}</th>
      <th class="sortable" data-sort="nuget">Max NuGet${sortArrow("nuget")}</th>
      <th class="sortable" data-sort="assemblyVersion">SDK AssemblyVersion${sortArrow("assemblyVersion")}</th>
      <th class="sortable" data-sort="vs">Visual Studio / MSBuild${sortArrow("vs")}</th>
      <th class="sortable" data-sort="csharp">C#${sortArrow("csharp")}</th>
      <th class="sortable" data-sort="releaseDate">Release Date${sortArrow("releaseDate")}</th>
    </tr>
  `;

  els.tableHead.querySelectorAll("th.sortable").forEach((th) => {
    th.addEventListener("click", () => {
      const key = th.dataset.sort;
      if (state.sortKey === key) {
        state.sortDir = state.sortDir === "asc" ? "desc" : "asc";
      } else {
        state.sortKey = key;
        state.sortDir = "desc";
      }
      renderTable();
    });
  });

  if (rows.length === 0) {
    showEmptyState("No SDK releases match the current filters.");
    return;
  }
  hideEmptyState();

  const frag = document.createDocumentFragment();
  for (const { channel, band, sdk } of rows) {
    const tr = document.createElement("tr");
    const commitHtml = sdk.codeAnalysisCSharp?.commitSha
      ? ` &middot; <a href="https://github.com/dotnet/roslyn/commit/${encodeURIComponent(sdk.codeAnalysisCSharp.commitSha)}" target="_blank" rel="noopener">${escapeHtml(sdk.codeAnalysisCSharp.commitSha.slice(0, 7))}</a>`
      : "";
    tr.innerHTML = `
      <td>
        <div class="cell-main">
          <strong><code>${escapeHtml(sdk.sdkVersion)}</code></strong>
          ${renderStatusBadges(channel, band, sdk)}
        </div>
        <div class="cell-sub">Band ${escapeHtml(band.band)} &middot; Runtime ${escapeHtml(sdk.runtimeVersion || "—")}</div>
      </td>
      <td>${renderNuGetCell(sdk.maxCodeAnalysisCSharpNuGetVersion, sdk.maxCodeAnalysisCSharpPrereleaseNuGetVersion, sdk.nugetLag, sdk.codeAnalysisCSharp?.assemblyVersion)}</td>
      <td>
        <code>${escapeHtml(sdk.codeAnalysisCSharp?.assemblyVersion || "—")}</code>
        <div class="cell-sub">${escapeHtml(sdk.codeAnalysisCSharp?.fileVersion || "—")}${commitHtml}</div>
      </td>
      <td>
        <div>${escapeHtml(sdk.resolvedVsSupport || sdk.vsSupport || "—")}</div>
        <div class="cell-sub">MSBuild ${escapeHtml(sdk.msbuild?.fileVersion || sdk.resolvedVsVersion || "—")}</div>
      </td>
      <td>${escapeHtml(sdk.csharpVersion || "—")}</td>
      <td>
        <div>${escapeHtml(sdk.releaseDate || "—")}</div>
        <div class="cell-sub">${sdk.releaseNotes ? `<a href="${escapeHtml(sdk.releaseNotes)}" target="_blank" rel="noopener">Notes</a>` : ""}</div>
      </td>
    `;
    frag.appendChild(tr);
  }

  els.tableBody.innerHTML = "";
  els.tableBody.appendChild(frag);
}

function renderStatusBadges(channel, band, sdk = null) {
  const badges = [];
  if (channel.releaseType) {
    const cls = channel.releaseType.toLowerCase() === "lts" ? "badge-lts" : "badge-sts";
    badges.push(`<span class="badge ${cls}">${escapeHtml(channel.releaseType.toUpperCase())}</span>`);
  }
  if (channel.supportPhase === "eol") {
    badges.push(`<span class="badge badge-eol" title="EOL ${escapeHtml(channel.eolDate || "")}">EOL</span>`);
  }
  if ((sdk && sdk.isPrerelease) || (band && band.isPrereleaseOnly)) {
    badges.push(`<span class="badge badge-preview">Preview</span>`);
  }
  if (sdk && sdk.security) {
    badges.push(`<span class="badge badge-sec">Security</span>`);
  }
  return badges.join(" ");
}

function renderNuGetCell(stableVer, prereleaseVer, nugetLag, sdkAsmVersion) {
  const ver = stableVer || prereleaseVer;
  if (!ver) {
    return `<span class="muted">Not on NuGet</span>`;
  }
  const url = `https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/${encodeURIComponent(ver)}`;
  const copyVal = `<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="${ver}" PrivateAssets="all" />`;
  const copyBtn = `<button type="button" class="btn btn-sm btn-ghost" data-copy-inline="${escapeHtml(copyVal)}" title="Copy PackageReference">Copy</button>`;

  let sub = "";
  if (nugetLag) {
    const preNote = prereleaseVer ? ` (pre ${escapeHtml(prereleaseVer)} available)` : "";
    sub = `<div class="cell-sub"><span class="badge badge-lag" title="SDK ships ${escapeHtml(sdkAsmVersion || "")}, awaiting matching package on NuGet.org">Awaiting ${escapeHtml(toMajorMinor(sdkAsmVersion || ""))}.x on NuGet</span>${preNote}</div>`;
  } else if (prereleaseVer && stableVer) {
    sub = `<div class="cell-sub">Prerelease: <code>${escapeHtml(prereleaseVer)}</code></div>`;
  }

  // Delegate click handler for inline copy buttons attached on tableBody.
  return `
    <div class="cell-main">
      <a class="nuget-link" href="${url}" target="_blank" rel="noopener">${escapeHtml(ver)}</a>
      ${copyBtn}
    </div>
    ${sub}
  `;
}

els.tableBody.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-copy-inline]");
  if (!btn) return;
  e.stopPropagation();
  copyText(btn.dataset.copyInline || "", "Copied PackageReference");
});

function showEmptyState(msg) {
  els.tableBody.innerHTML = "";
  els.tableStatus.classList.remove("hidden");
  els.tableStatus.textContent = msg;
}

function hideEmptyState() {
  els.tableStatus.classList.add("hidden");
}

function compareFlatRows(a, b, key) {
  switch (key) {
    case "sdkVersion":
      return compareVersions(a.sdk.sdkVersion, b.sdk.sdkVersion);
    case "nuget":
      return compareVersions(a.sdk.maxCodeAnalysisCSharpNuGetVersion || "0", b.sdk.maxCodeAnalysisCSharpNuGetVersion || "0");
    case "assemblyVersion":
      return compareVersions(a.sdk.codeAnalysisCSharp?.assemblyVersion || "0", b.sdk.codeAnalysisCSharp?.assemblyVersion || "0");
    case "vs":
      return compareVersions(a.sdk.resolvedVsVersion || "0", b.sdk.resolvedVsVersion || "0");
    case "csharp":
      return compareVersions(a.sdk.csharpVersion || "0", b.sdk.csharpVersion || "0");
    case "releaseDate":
      return (a.sdk.releaseDate || "").localeCompare(b.sdk.releaseDate || "");
    default:
      return 0;
  }
}

function compareVersions(a, b) {
  const [aMain, aPre] = String(a).split("-", 2);
  const [bMain, bPre] = String(b).split("-", 2);
  const aParts = aMain.split(".").map((n) => parseInt(n, 10) || 0);
  const bParts = bMain.split(".").map((n) => parseInt(n, 10) || 0);
  const len = Math.max(aParts.length, bParts.length);
  for (let i = 0; i < len; i++) {
    const diff = (aParts[i] || 0) - (bParts[i] || 0);
    if (diff !== 0) return diff;
  }
  if (!aPre && bPre) return 1;
  if (aPre && !bPre) return -1;
  return (aPre || "").localeCompare(bPre || "");
}

function toMajorMinor(ver) {
  const parts = String(ver).split(".");
  return parts.length >= 2 ? `${parts[0]}.${parts[1]}` : ver;
}

async function copyText(text, message) {
  try {
    await navigator.clipboard.writeText(text);
    showToast(message);
  } catch {
    showToast("Copy failed (clipboard blocked)");
  }
}

let toastTimer = null;
function showToast(msg) {
  els.toast.textContent = msg;
  els.toast.classList.remove("hidden");
  if (toastTimer) clearTimeout(toastTimer);
  toastTimer = setTimeout(() => els.toast.classList.add("hidden"), 2200);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
