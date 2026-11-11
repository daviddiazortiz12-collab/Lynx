(() => {
  const $ = (id) => document.getElementById(id);

  let modalTargetInput = null;
  let modalDirsOnly = false;
  let modalSaveCiaMode = false;
  let modalParentDir = "";
  let activeWorkflowMode = "cia-randomize"; // "cia-randomize" | "cia-merge" | "classic"

  function setWorkflowMode(mode) {
    activeWorkflowMode = mode;
    document.querySelectorAll(".workflow-tab").forEach((b) => {
      b.classList.toggle("active", b.getAttribute("data-wmode") === mode);
    });

    const isCia = mode === "cia-randomize" || mode === "cia-merge";
    const isMerge = mode === "cia-merge";
    const isClassic = mode === "classic";

    $("ciaPathsSection").classList.toggle("hidden", !isCia);
    $("mergeRomFsRow").classList.toggle("hidden", !isMerge);
    $("classicPathsSection").classList.toggle("hidden", !isClassic);
    $("classicOutputSection").classList.toggle("hidden", !isClassic);
    $("classicExportCard").classList.toggle("hidden", !isClassic);
    $("titleIdOverrideCol").classList.toggle("hidden", !isClassic);
    $("editionTitleRow").classList.toggle("hidden", isMerge);
    $("seedBlindCard").classList.toggle("hidden", isMerge);
    $("allSubsystemsWrapper").classList.toggle("hidden", isMerge);
    $("categoriesHeader").classList.toggle("hidden", isMerge);
    $("mergeExplainBox").classList.toggle("hidden", !isMerge);

    if (mode === "cia-randomize") {
      $("btnGenerate").textContent = "⚡ Randomizar .cia y Generar Nuevo .cia Verificado";
    } else if (mode === "cia-merge") {
      $("btnGenerate").textContent = "🧩 Fusionar Carpeta RomFS + .cia y Verificar Nuevo .cia";
    } else {
      $("btnGenerate").textContent = "⚡ Generar Run Randomizada (Modo Clásico v1.0.2)";
    }

    suggestOutputCiaPath(true);
  }

  document.querySelectorAll(".workflow-tab").forEach((btn) => {
    btn.addEventListener("click", () => setWorkflowMode(btn.getAttribute("data-wmode")));
  });

  function suggestOutputCiaPath(allowSuffixUpdate = false) {
    const src = ($("sourceCiaPath").value || "").trim();
    if (!src) return;
    const cur = ($("outputCiaPath").value || "").trim();
    const suffix = activeWorkflowMode === "cia-merge" ? "_RomFsMerged.cia" : "_Randomizado.cia";
    const baseNoExt = src.replace(/\.cia$/i, "");
    if (!cur || (allowSuffixUpdate && (cur.endsWith("_Randomizado.cia") || cur.endsWith("_RomFsMerged.cia")))) {
      $("outputCiaPath").value = baseNoExt + suffix;
    }
  }

  // Slider live labels
  $("plStatVar").addEventListener("input", (e) => {
    $("lblStatVar").textContent = `${e.target.value}%`;
  });
  $("plAvChance").addEventListener("input", (e) => {
    $("lblAvChance").textContent = `${e.target.value}%`;
  });

  // Random seed generator (strictly relative URL for local & proxy preview compatibility)
  async function requestRandomSeed() {
    try {
      const res = await fetch("api/seed/random");
      const data = await res.json();
      if (data && data.seed) {
        $("seedInput").value = data.seed;
      }
    } catch {
      $("seedInput").value = "PEGASO-" + Math.floor(1000 + Math.random() * 9000);
    }
  }

  $("btnRandomSeed").addEventListener("click", requestRandomSeed);
  requestRandomSeed();

  // Discover local Citra/Azahar/Lime3DS mods folder on startup
  (async function initCitraPaths() {
    try {
      const res = await fetch("api/citra/paths");
      if (!res.ok) return;
      const data = await res.json();
      if (data && data.defaultModsRoot && !$("citraModsRootInput").value) {
        $("citraModsRootInput").value = data.defaultModsRoot;
      }
      if (data && Array.isArray(data.discoveredRoots) && data.discoveredRoots.length > 0) {
        $("expDirectCitra").checked = true;
        $("directCitraPathRow").classList.remove("hidden");
      }
    } catch {
      // Non-fatal
    }
  })();

  $("expDirectCitra").addEventListener("change", () => {
    $("directCitraPathRow").classList.toggle("hidden", !$("expDirectCitra").checked);
  });

  $("btnCopySeed").addEventListener("click", async () => {
    const val = $("seedInput").value.trim();
    if (!val) return;
    try {
      await navigator.clipboard.writeText(val);
      $("btnCopySeed").textContent = "¡Copiada!";
      setTimeout(() => ($("btnCopySeed").textContent = "Copiar"), 1400);
    } catch {
      $("seedInput").select();
    }
  });

  $("btnShutdownApp").addEventListener("click", async () => {
    try {
      await fetch("api/app/shutdown", { method: "POST" });
      document.body.innerHTML = "<div style='display:grid;place-items:center;height:100vh;color:#e8eefc;font-family:sans-serif;'><h2>Programa cerrado correctamente. Ya puedes cerrar esta pestaña/ventana.</h2></div>";
      window.close();
    } catch {
      window.close();
    }
  });

  // Quick presets
  document.querySelectorAll("[data-preset]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const preset = btn.getAttribute("data-preset");
      if (preset === "balanced") {
        $("playersEnabled").checked = true;
        $("skillsEnabled").checked = true;
        $("teamsEnabled").checked = true;
        $("rewardsEnabled").checked = true;
        $("uniformsEnabled").checked = true;
        $("plVisualSwap").checked = false;
        $("skParams").checked = false;
        $("tmLevels").checked = false;
        $("rwShops").checked = false;
      } else if (preset === "full") {
        $("playersEnabled").checked = true;
        $("skillsEnabled").checked = true;
        $("teamsEnabled").checked = true;
        $("rewardsEnabled").checked = true;
        $("uniformsEnabled").checked = true;
        $("plVisualSwap").checked = true;
        $("skParams").checked = true;
        $("tmLevels").checked = true;
        $("rwShops").checked = true;
      } else if (preset === "players") {
        $("playersEnabled").checked = true;
        $("skillsEnabled").checked = false;
        $("teamsEnabled").checked = false;
        $("rewardsEnabled").checked = false;
        $("uniformsEnabled").checked = false;
      }
    });
  });

  // Inspect input path non-destructively
  let inspectTimer = null;
  async function inspectInputPath(pathValue) {
    const badge = $("inputInspectBadge");
    const trimmed = (pathValue || "").trim();
    if (!trimmed) {
      badge.className = "inspect-status neutral";
      badge.textContent = "Selecciona una entrada para verificar formato, edición y protección de lectura.";
      return;
    }

    try {
      const res = await fetch("api/inspect", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ inputPath: trimmed })
      });
      const data = await res.json();
      if (!res.ok) {
        badge.className = "inspect-status error";
        badge.textContent = `⚠️ ${data.error || "Ruta no válida."}`;
        return;
      }

      badge.className = "inspect-status ok";
      badge.textContent = `✅ ${data.summary}`;
      if (data.detectedEdition && data.detectedEdition !== "Auto") {
        $("editionSelect").value = data.detectedEdition;
      }
    } catch (err) {
      badge.className = "inspect-status error";
      badge.textContent = `⚠️ Error al inspeccionar entrada: ${err.message}`;
    }
  }

  $("inputPath").addEventListener("input", (e) => {
    clearTimeout(inspectTimer);
    inspectTimer = setTimeout(() => inspectInputPath(e.target.value), 350);
  });

  // File / Folder picker (attempts native Windows dialog first, falls back to built-in browser modal)
  async function openPicker(targetInputId, mode, title, suggestedName = "") {
    try {
      const url = `api/fs/pick-native?mode=${encodeURIComponent(mode)}&suggestedName=${encodeURIComponent(suggestedName || "")}`;
      const nativeRes = await fetch(url, { method: "POST" });
      if (nativeRes.ok) {
        const nativeData = await nativeRes.json();
        if (nativeData.supported && nativeData.selectedPath) {
          $(targetInputId).value = nativeData.selectedPath;
          if (targetInputId === "inputPath") inspectInputPath(nativeData.selectedPath);
          if (targetInputId === "sourceCiaPath") {
            suggestOutputCiaPath(true);
            inspectLocalCia(false);
          }
          return;
        }
        if (nativeData.supported) {
          return; // User cancelled native Windows dialog
        }
      }
    } catch {
      // Fallback to built-in modal
    }

    modalTargetInput = targetInputId;
    modalSaveCiaMode = mode === "save-cia";
    modalDirsOnly = mode === "folder" || modalSaveCiaMode;
    $("modalTitle").textContent = title;
    $("modalNewSubfolder").placeholder = modalSaveCiaMode
      ? "Nombre del nuevo archivo .cia (ej. InazumaGO_Luz_Randomizado.cia)"
      : "Opcional: subcarpeta nueva (ej. Run_Luz_01)";
    if (modalSaveCiaMode && suggestedName) {
      $("modalNewSubfolder").value = suggestedName;
    }
    $("modalNewSubfolder").style.display = modalDirsOnly ? "block" : "none";
    $("btnModalSelectCurrent").textContent = modalSaveCiaMode ? "Guardar aquí" : "Seleccionar esta carpeta";
    $("btnModalSelectCurrent").style.display = modalDirsOnly ? "inline-block" : "none";
    $("browserModal").classList.remove("hidden");
    await loadModalDirectory($(targetInputId).value.trim());
  }

  async function loadModalDirectory(dirPath) {
    const query = `api/fs/list?path=${encodeURIComponent(dirPath || "")}&dirsOnly=${modalDirsOnly}`;
    const res = await fetch(query);
    const data = await res.json();
    $("modalCurrentPath").value = data.currentDirectory || "";
    modalParentDir = data.parentDirectory || "";

    const drivesEl = $("modalDrives");
    drivesEl.innerHTML = "";
    (data.drives || []).forEach((drv) => {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "btn btn-chip";
      b.textContent = drv;
      b.addEventListener("click", () => loadModalDirectory(drv));
      drivesEl.appendChild(b);
    });

    const entriesEl = $("modalEntries");
    entriesEl.innerHTML = "";
    if (!data.entries || data.entries.length === 0) {
      const empty = document.createElement("div");
      empty.className = "entry-item";
      empty.textContent = modalDirsOnly
        ? "Sin subcarpetas visibles (puedes seleccionar esta carpeta actual)."
        : "No hay subcarpetas ni archivos .3ds / ie_a.fa en este directorio.";
      entriesEl.appendChild(empty);
      return;
    }

    data.entries.forEach((item) => {
      const row = document.createElement("div");
      row.className = "entry-item";
      const icon = item.isDirectory ? "📁" : "📦";
      const meta = item.isDirectory
        ? "Carpeta"
        : `${(item.sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
      row.innerHTML = `<span>${icon} ${escapeHtml(item.name)}</span><small style="color:#97a8cc">${meta}</small>`;
      row.addEventListener("click", () => {
        if (item.isDirectory) {
          loadModalDirectory(item.fullPath);
        } else {
          $(modalTargetInput).value = item.fullPath;
          closeModal();
          if (modalTargetInput === "inputPath") inspectInputPath(item.fullPath);
          if (modalTargetInput === "sourceCiaPath") {
            suggestOutputCiaPath(true);
            inspectLocalCia(false);
          }
        }
      });
      entriesEl.appendChild(row);
    });
  }

  function closeModal() {
    $("browserModal").classList.add("hidden");
  }

  $("btnPickInputFile").addEventListener("click", () =>
    openPicker("inputPath", "file", "Seleccionar archivo .cia, .3ds o ie_a.fa"));
  $("btnPickInputFolder").addEventListener("click", () =>
    openPicker("inputPath", "folder", "Seleccionar carpeta RomFS extraída"));
  $("btnPickOutputFolder").addEventListener("click", () =>
    openPicker("outputDir", "folder", "Seleccionar carpeta de salida separada"));
  $("btnPickCitraModsFolder").addEventListener("click", () =>
    openPicker("citraModsRootInput", "folder", "Seleccionar carpeta load/mods de Citra o Azahar"));

  $("btnPickSourceCia").addEventListener("click", () =>
    openPicker("sourceCiaPath", "cia", "Seleccionar archivo .cia original (solo lectura)"));
  $("btnPickModifiedRomFs").addEventListener("click", () =>
    openPicker("modifiedRomFsDir", "folder", "Seleccionar carpeta RomFS modificada"));
  $("btnPickOutputCia").addEventListener("click", () => {
    const src = ($("sourceCiaPath").value || "").trim();
    const base = src
      ? src.split(/[\\/]/).pop().replace(/\.cia$/i, "") + (activeWorkflowMode === "cia-merge" ? "_RomFsMerged.cia" : "_Randomizado.cia")
      : "InazumaElevenGO_Randomizado.cia";
    openPicker("outputCiaPath", "save-cia", "Elegir destino del nuevo archivo .cia", base);
  });
  $("btnPickAesKeys").addEventListener("click", () =>
    openPicker("aesKeysPath", "keys", "Seleccionar archivo local aes_keys.txt"));

  let ciaInspectTimer = null;
  async function inspectLocalCia(saveDiagJson = false) {
    const ciaPath = ($("sourceCiaPath").value || "").trim();
    const box = $("ciaInspectBox");
    if (!ciaPath) {
      box.classList.add("hidden");
      return;
    }

    suggestOutputCiaPath(false);
    box.classList.remove("hidden");
    box.innerHTML = `<span style="color:#93c5fd;">🔎 Inspeccionando localmente cabeceras CIA / TMD / NCCH / RomFS (100% solo lectura)...</span>`;

    const exportDiagnosticJsonPath = saveDiagJson
      ? ciaPath.replace(/\.cia$/i, "") + ".cia-diagnostico-sin-rom.json"
      : "";

    try {
      const res = await fetch("api/cia/inspect", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          ciaPath,
          aesKeysPath: ($("aesKeysPath").value || "").trim(),
          computeSha256: false,
          exportDiagnosticJsonPath
        })
      });
      const data = await res.json();
      if (!res.ok || !data.report) {
        box.innerHTML = `<strong style="color:#fca5a5;">⚠️ ${escapeHtml(data.error || "No se pudo inspeccionar el archivo .cia.")}</strong>`;
        return;
      }

      const r = data.report;
      if (r.detectedEdition && r.detectedEdition !== "Auto") {
        $("editionSelect").value = r.detectedEdition;
      }

      const okChip = r.canProcessNow
        ? `<span class="cia-chip ok">✅ Listo para procesar</span>`
        : `<span class="cia-chip warn">⚠️ Requiere atención</span>`;
      const verChip = `<span class="cia-chip">${escapeHtml(r.tmdTitleVersionFormatted || "V.?")} ${r.isBaseRevisionV000 ? "(Base retail V.0.0.0)" : ""}</span>`;
      const encChip = `<span class="cia-chip">${escapeHtml(r.encryptionKind)}</span>`;
      const tidChip = `<span class="cia-chip">${escapeHtml(r.detectedEdition)} · ${escapeHtml(r.tmdTitleIdHex)}</span>`;

      let html = `<div class="cia-badge-row">${okChip}${verChip}${encChip}${tidChip}</div>`;
      html += `<div><strong>Tamaño:</strong> ${(r.fileSizeBytes / (1024 * 1024)).toFixed(1)} MB · <strong>Contenidos TMD:</strong> ${r.contentCount} · <strong>SaveDataSize:</strong> ${Math.round(r.saveDataSizeBytes / 1024)} KB${r.smdhBannerTitle ? ` · <strong>Banner:</strong> ${escapeHtml(r.smdhBannerTitle)}` : ""}</div>`;
      if (r.romFsReadable) {
        html += `<div><strong>RomFS completa legible:</strong> ${r.romFsFileCount} archivos en ${r.romFsDirectoryCount} carpetas (<code>ie_a.fa</code>=${r.hasIeAFa ? "SÍ" : "NO"}, <code>fix.xr</code>=${r.hasFixXr ? "SÍ" : "NO"}, sonidos=${r.romFsSoundFilesCount}, vídeos=${r.romFsMovieFilesCount})</div>`;
      }
      if (Array.isArray(r.limitationsAndWarnings) && r.limitationsAndWarnings.length > 0) {
        html += `<ul style="margin:6px 0 4px;padding-left:18px;color:#cbd5e1;">${r.limitationsAndWarnings.map((w) => `<li>${escapeHtml(w)}</li>`).join("")}</ul>`;
      }
      if (data.exportedDiagnosticJsonPath) {
        html += `<div style="margin-top:6px;color:#6ee7b7;">💾 Informe JSON técnico local (sin datos de ROM) guardado en: <code>${escapeHtml(data.exportedDiagnosticJsonPath)}</code></div>`;
      } else {
        html += `<div style="margin-top:6px;"><button type="button" class="btn btn-chip" id="btnExportRomFreeDiag">📄 Guardar informe JSON técnico local (sin bytes de ROM)</button></div>`;
      }

      box.innerHTML = html;
      const expBtn = $("btnExportRomFreeDiag");
      if (expBtn) expBtn.addEventListener("click", () => inspectLocalCia(true));
    } catch (err) {
      box.innerHTML = `<strong style="color:#fca5a5;">⚠️ Error al inspeccionar .cia: ${escapeHtml(err.message)}</strong>`;
    }
  }

  $("btnInspectLocalCia").addEventListener("click", () => inspectLocalCia(false));
  $("sourceCiaPath").addEventListener("input", () => {
    clearTimeout(ciaInspectTimer);
    ciaInspectTimer = setTimeout(() => inspectLocalCia(false), 450);
  });
  $("aesKeysPath").addEventListener("change", () => {
    if (($("sourceCiaPath").value || "").trim()) inspectLocalCia(false);
  });

  // Local Diagnostic Mode (zero spoilers)
  $("btnDiagnoseLocal").addEventListener("click", async () => {
    const diagBox = $("diagnosticResultBox");
    const inputPath = activeWorkflowMode === "classic"
      ? $("inputPath").value.trim()
      : $("sourceCiaPath").value.trim();
    if (!inputPath) {
      diagBox.className = "result-box failure";
      diagBox.innerHTML = "<strong>Falta entrada:</strong> Selecciona primero tu archivo <code>.cia</code>, <code>ie_a.fa</code>, carpeta <code>RomFS</code> o <code>.3ds</code>.";
      diagBox.classList.remove("hidden");
      return;
    }

    $("btnDiagnoseLocal").disabled = true;
    diagBox.className = "result-box";
    diagBox.innerHTML = "⏳ Analizando archivo ARC0, tablas internas y carpetas de mods de Citra...";
    diagBox.classList.remove("hidden");

    try {
      const res = await fetch("api/diagnose", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          inputPath,
          edition: $("editionSelect").value,
          targetOutputOrCitraDir: activeWorkflowMode === "classic"
            ? ($("outputDir").value.trim() || $("citraModsRootInput").value.trim())
            : ($("outputCiaPath").value.trim() || $("citraModsRootInput").value.trim())
        })
      });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error || "No se pudo ejecutar el diagnóstico.");

      const warningsHtml = (data.actionableWarnings || [])
        .map((w) => `<li style="color:#fcd34d;margin-bottom:4px;">⚠️ ${escapeHtml(w)}</li>`)
        .join("");
      const findingsHtml = (data.findings || [])
        .map((f) => `<li style="margin-bottom:3px;">✓ ${escapeHtml(f)}</li>`)
        .join("");

      diagBox.className = data.hasValidIeAFa ? "result-box success" : "result-box failure";
      diagBox.innerHTML = `
        <div><strong>🔍 Diagnóstico Forense Local (${escapeHtml(data.inputKind || "Entrada")})</strong></div>
        ${data.bannerTitleSummary ? `<div style="font-size:0.84rem;color:#93c5fd;margin-top:3px;">Banner 3DS: ${escapeHtml(data.bannerTitleSummary)}</div>` : ""}
        ${warningsHtml ? `<ul style="margin:8px 0;padding-left:18px;">${warningsHtml}</ul>` : ""}
        <ul style="margin:8px 0 0;padding-left:18px;font-size:0.85rem;">${findingsHtml}</ul>
      `;
    } catch (err) {
      diagBox.className = "result-box failure";
      diagBox.innerHTML = `<strong>Error en diagnóstico:</strong> ${escapeHtml(err.message)}`;
    } finally {
      $("btnDiagnoseLocal").disabled = false;
    }
  });

  $("btnCloseModal").addEventListener("click", closeModal);
  $("btnModalCancel").addEventListener("click", closeModal);
  $("btnModalUp").addEventListener("click", () => {
    if (modalParentDir) loadModalDirectory(modalParentDir);
  });
  $("btnModalGo").addEventListener("click", () => loadModalDirectory($("modalCurrentPath").value));
  $("btnModalSelectCurrent").addEventListener("click", () => {
    let chosen = $("modalCurrentPath").value.trim();
    const sub = $("modalNewSubfolder").value.trim();
    if (modalSaveCiaMode) {
      const sep = chosen.includes("\\") ? "\\" : "/";
      const fname = sub || "InazumaElevenGO_Randomizado.cia";
      chosen = chosen.replace(/[\\/]+$/, "") + sep + (fname.toLowerCase().endsWith(".cia") ? fname : fname + ".cia");
    } else if (sub && modalTargetInput === "outputDir") {
      const sep = chosen.includes("\\") ? "\\" : "/";
      chosen = chosen.replace(/[\\/]+$/, "") + sep + sub;
    }
    if (chosen && modalTargetInput) {
      $(modalTargetInput).value = chosen;
      if (modalTargetInput === "inputPath") inspectInputPath(chosen);
    }
    closeModal();
  });

  // Collect options & start job
  function buildOptionsPayload() {
    return {
      seed: $("seedInput").value.trim(),
      edition: $("editionSelect").value,
      preferredLanguageCode: "es",
      aesKeysPath: ($("aesKeysPath").value || "").trim(),
      blindPlayMode: $("optBlindPlay").checked,
      players: {
        enabled: $("playersEnabled").checked,
        randomizeTeamAndRivalSlots: $("plRosters").checked,
        randomizePachangaPlayers: $("plPachangas").checked,
        randomizePalpackRecruits: $("plPalpack").checked,
        swapStoryPlayerVisualIdentities: $("plVisualSwap").checked,
        enforceStrictDerangement: true,
        preserveGoalkeeperRolesInRosters: $("plKeepGk").checked,
        randomizePositions: $("plPositions").checked,
        randomizeElements: $("plElements").checked,
        randomizeBaseStats: $("plStats").checked,
        statVariancePercent: parseInt($("plStatVar").value, 10) || 25,
        randomizeFightingSpirits: $("plAvatars").checked,
        fightingSpiritChancePercent: parseInt($("plAvChance").value, 10) || 25
      },
      skills: {
        enabled: $("skillsEnabled").checked,
        randomizeInitialSkills: $("skInitial").checked,
        randomizeLevelUpLearnsets: $("skLearnset").checked,
        randomizeTalents: $("skTalents").checked,
        randomizeFightingSpiritSkills: $("skAvatar").checked,
        randomizeSkillParameters: $("skParams").checked,
        skillParamVariancePercent: 20,
        enforcePositionCompatibility: $("skCompat").checked,
        ensureUsableInitialTp: $("skUsableTp").checked
      },
      teams: {
        enabled: $("teamsEnabled").checked,
        randomizeStoryAndRouteTeams: $("tmStory").checked,
        randomizePachangaTeams: $("tmPachanga").checked,
        randomizeKitNumbers: $("tmNumbers").checked,
        randomizeEmblems: $("tmEmblems").checked,
        randomizeFormations: $("tmFormations").checked,
        randomizeCoaches: $("tmCoaches").checked,
        randomizeTactics: $("tmTactics").checked,
        randomizeLevels: $("tmLevels").checked,
        maxLevelDelta: 4,
        randomizeEquipment: $("tmEquip").checked
      },
      rewards: {
        enabled: $("rewardsEnabled").checked,
        randomizeOverworldChests: $("rwMapChests").checked,
        randomizeCompetitionRouteChests: $("rwRouteChests").checked,
        randomizeMatchDrops: $("rwMatchDrops").checked,
        randomizePachangaDrops: $("rwPachangaDrops").checked,
        randomizeShopItems: $("rwShops").checked
      },
      uniforms: {
        enabled: $("uniformsEnabled").checked,
        randomizeRivalUniforms: $("unRivals").checked,
        randomizePachangaUniforms: $("unPachangas").checked,
        randomizeObtainableUniformRewards: $("unObtainable").checked
      },
      protection: {
        enableStorySoftlockProtection: $("prStory").checked,
        auditNutbEventScripts: $("prNutb").checked,
        protectKeyItemsAndFuncpointChests: $("prKeyItems").checked,
        enforceVisualAssetCoherence: true
      },
      export: {
        exportStandaloneIeAFa: $("expStandalone").checked,
        exportCitraModStructure: $("expCitra").checked,
        exportLuma3dsLayeredFsStructure: $("expLayeredFs").checked,
        customTitleIdOverride: $("titleIdOverride").value,
        directCitraModsRootPath: $("expDirectCitra").checked ? $("citraModsRootInput").value.trim() : ""
      }
    };
  }

  $("btnGenerate").addEventListener("click", async () => {
    const resultBox = $("resultBox");
    const progressArea = $("progressArea");
    resultBox.classList.add("hidden");

    let endpoint = "api/jobs";
    let bodyPayload = null;

    if (activeWorkflowMode === "cia-randomize") {
      const sourceCiaPath = $("sourceCiaPath").value.trim();
      const outputCiaPath = $("outputCiaPath").value.trim();
      if (!sourceCiaPath || !outputCiaPath) {
        resultBox.className = "result-box failure";
        resultBox.innerHTML = "<strong>Faltan rutas .cia:</strong> Selecciona tu archivo <code>.cia</code> original y la ruta del nuevo <code>.cia</code> de salida.";
        resultBox.classList.remove("hidden");
        return;
      }
      endpoint = "api/jobs/cia-randomize";
      bodyPayload = { sourceCiaPath, outputCiaPath, options: buildOptionsPayload() };
    } else if (activeWorkflowMode === "cia-merge") {
      const sourceCiaPath = $("sourceCiaPath").value.trim();
      const modifiedRomFsDirectory = $("modifiedRomFsDir").value.trim();
      const outputCiaPath = $("outputCiaPath").value.trim();
      if (!sourceCiaPath || !modifiedRomFsDirectory || !outputCiaPath) {
        resultBox.className = "result-box failure";
        resultBox.innerHTML = "<strong>Faltan rutas para la fusión:</strong> Indica el <code>.cia</code> original, la carpeta <code>RomFS</code> modificada y la ruta del nuevo <code>.cia</code>.";
        resultBox.classList.remove("hidden");
        return;
      }
      endpoint = "api/jobs/cia-merge";
      bodyPayload = {
        sourceCiaPath,
        modifiedRomFsDirectory,
        outputCiaPath,
        aesKeysPath: ($("aesKeysPath").value || "").trim()
      };
    } else {
      const inputPath = $("inputPath").value.trim();
      const outputDirectory = $("outputDir").value.trim();
      if (!inputPath || !outputDirectory) {
        resultBox.className = "result-box failure";
        resultBox.innerHTML = "<strong>Faltan rutas:</strong> Selecciona el archivo/carpeta de entrada y una carpeta de salida separada.";
        resultBox.classList.remove("hidden");
        return;
      }
      endpoint = "api/jobs";
      bodyPayload = { inputPath, outputDirectory, options: buildOptionsPayload() };
    }

    $("btnGenerate").disabled = true;
    progressArea.classList.remove("hidden");
    $("progressBarFill").style.width = "2%";
    $("progressLabel").textContent = "Enviando configuración al motor...";

    try {
      const res = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(bodyPayload)
      });

      const job = await res.json();
      if (!res.ok) {
        throw new Error(job.error || "No se pudo iniciar la randomización.");
      }

      await pollJobUntilDone(job.jobId);
    } catch (err) {
      resultBox.className = "result-box failure";
      resultBox.innerHTML = `<strong>Error de ejecución:</strong><br/>${escapeHtml(err.message)}`;
      resultBox.classList.remove("hidden");
    } finally {
      $("btnGenerate").disabled = false;
    }
  });

  async function pollJobUntilDone(jobId) {
    const resultBox = $("resultBox");
    while (true) {
      await new Promise((r) => setTimeout(r, 300));
      const res = await fetch(`api/jobs/${encodeURIComponent(jobId)}`);
      if (!res.ok) throw new Error("No se pudo consultar el progreso del proceso.");
      const state = await res.json();

      $("progressBarFill").style.width = `${state.percent || 0}%`;
      $("progressLabel").textContent = `[${state.percent || 0}%] ${state.currentStage || ""}`;

      if (state.status === "failed") {
        throw new Error(state.errorMessage || "Falló la validación del archivo.");
      }

      if (state.status === "completed" && state.jobMode === "cia-merge" && state.mergeReport) {
        const mr = state.mergeReport;
        const t = mr.rebuildTelemetry;
        const v = mr.verification;
        const outParentDir = t.outputCiaPath.replace(/[\\/][^\\/]+$/, "");
        const overlaidItems = (t.overlaidRelativePaths || []).slice(0, 25).map((p) => `<li><code>${escapeHtml(p)}</code></li>`).join("");

        resultBox.className = "result-box success";
        resultBox.innerHTML = `
          <div style="display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap;">
            <div><strong>✅ ¡Nuevo archivo .cia fusionado con toda la RomFS y verificado en frío!</strong></div>
            <button type="button" class="btn btn-secondary" id="btnOpenMergedCiaFolder">📂 Abrir carpeta del nuevo .cia</button>
          </div>
          <div class="result-grid" style="margin-top:8px;">
            <div class="metric-pill"><strong>Nuevo .cia:</strong> <code>${escapeHtml(t.outputCiaPath)}</code> (${(t.outputCiaSizeBytes / (1024 * 1024)).toFixed(1)} MB)</div>
            <div class="metric-pill"><strong>TitleID / Versión:</strong> <code>${escapeHtml(t.titleIdHex)}</code> (${escapeHtml(t.titleVersionFormatted)}) · SaveData: ${Math.round(t.preservedSaveDataSizeBytes / 1024)} KB</div>
            <div class="metric-pill"><strong>Archivos RomFS incorporados:</strong> ${t.overlaidModifiedRomFsFilesCount}</div>
            <div class="metric-pill"><strong>RomFS total en el .cia:</strong> ${t.outputRomFsFileCount} archivos (origen: ${t.sourceRomFsFileCount})</div>
            <div class="metric-pill"><strong>IVFC 4 niveles SHA-256:</strong> ${v.verifiedIvfcBlocksCount} bloques verificados · ${t.preservedContentsCount} contenidos</div>
            <div class="metric-pill"><strong>SHA-256 original intacto:</strong> <code style="font-size:0.75rem;">${escapeHtml(t.sourceCiaSha256Hex)}</code></div>
          </div>
          <details style="margin-top:8px;">
            <summary style="cursor:pointer;font-weight:600;">Ver rutas de RomFS incorporadas y comprobaciones en frío</summary>
            <ul style="margin:6px 0;padding-left:18px;">${(v.checksPassed || []).map((c) => `<li>✓ ${escapeHtml(c)}</li>`).join("")}</ul>
            <div><strong>Archivos superpuestos/añadidos (${t.overlaidModifiedRomFsFilesCount}):</strong></div>
            <ul style="margin:4px 0 0;padding-left:18px;">${overlaidItems}</ul>
          </details>
        `;
        resultBox.classList.remove("hidden");
        const openM = $("btnOpenMergedCiaFolder");
        if (openM) {
          openM.addEventListener("click", async () => {
            await fetch("api/app/open-folder", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ folderPath: outParentDir })
            });
          });
        }
        return;
      }

      if (state.status === "completed" && state.report) {
        const rep = state.report;
        $("seedInput").value = rep.seed;
        const checksHtml = (rep.validationMessages || [])
          .map((m) => `<li>✓ ${escapeHtml(m)}</li>`)
          .join("");
        const exportsHtml = (rep.exportedPaths || [])
          .map((p) => `<li><code>${escapeHtml(p)}</code></li>`)
          .join("");

        const isCiaJob = state.jobMode === "cia-randomize" && !!rep.ciaBuildTelemetry;
        const currentOutDir = isCiaJob
          ? rep.ciaBuildTelemetry.outputCiaPath.replace(/[\\/][^\\/]+$/, "")
          : $("outputDir").value.trim();

        const ciaBannerHtml = isCiaJob
          ? `<div style="margin-top:8px;padding:9px 12px;border-radius:8px;background:rgba(16,185,129,0.16);border:1px solid rgba(16,185,129,0.42);font-size:0.84rem;line-height:1.48;">
              📦 <strong>Nuevo .cia generado y verificado en frío:</strong> <code>${escapeHtml(rep.ciaBuildTelemetry.outputCiaPath)}</code> (${(rep.ciaBuildTelemetry.outputCiaSizeBytes / (1024 * 1024)).toFixed(1)} MB)<br/>
              • <strong>RomFS completa preservada:</strong> ${rep.ciaBuildTelemetry.outputRomFsFileCount} archivos totales (origen: ${rep.ciaBuildTelemetry.sourceRomFsFileCount}) · <strong>SaveDataSize:</strong> ${Math.round(rep.ciaBuildTelemetry.preservedSaveDataSizeBytes / 1024)} KB · <strong>IVFC SHA-256:</strong> ${rep.ciaVerification ? rep.ciaVerification.verifiedIvfcBlocksCount : 0} bloques<br/>
              • <strong>SHA-256 .cia original intacto:</strong> <code style="font-size:0.74rem;">${escapeHtml(rep.ciaBuildTelemetry.sourceCiaSha256Hex)}</code>
            </div>`
          : "";

        const diff = rep.persistedDiffTelemetry;
        const diffSummaryHtml = diff
          ? `<div class="metric-pill"><strong>Archivos internos modificados en disco:</strong> ${diff.persistedModifiedFilesCount} (Rivales: ${diff.persistedChangedStoryTeamsCount}, Pachangas: ${diff.persistedChangedPachangaTeamsCount}, Técnicas: ${diff.persistedChangedSkillTableEntriesCount})</div>`
          : "";
        const citraInstallBanner = rep.directCitraInstalledPath
          ? `<div style="margin-top:6px;padding:7px 10px;border-radius:8px;background:rgba(16,185,129,0.16);border:1px solid rgba(16,185,129,0.4);font-size:0.84rem;">🎮 <strong>Instalado directo en Citra:</strong> <code>${escapeHtml(rep.directCitraInstalledPath)}</code></div>`
          : "";

        resultBox.className = "result-box success";
        resultBox.innerHTML = `
          <div style="display:flex;justify-content:space-between;align-items:center;gap:10px;flex-wrap:wrap;">
            <div><strong>${isCiaJob ? "✅ ¡Nuevo archivo .cia randomizado y verificado en frío!" : "✅ ¡Partida generada y verificada en disco!"}</strong> ${rep.blindPlayMode ? "<span>(Modo a Ciegas activo: 0 spoilers mostrados)</span>" : ""}</div>
            <div style="display:flex;gap:8px;flex-wrap:wrap;">
              ${!isCiaJob ? `<button type="button" class="btn btn-primary" id="btnInstallToCitraNow">🎮 Instalar directo en Citra</button>` : ""}
              <button type="button" class="btn btn-secondary" id="btnOpenGeneratedFolder">📂 Abrir carpeta de salida</button>
            </div>
          </div>
          ${ciaBannerHtml}
          ${citraInstallBanner}
          <div id="citraQuickInstallMsg" style="margin-top:6px;font-size:0.84rem;"></div>
          <div class="result-grid">
            <div class="metric-pill"><strong>Seed:</strong> <code>${escapeHtml(rep.seed)}</code></div>
            <div class="metric-pill"><strong>Huella:</strong> <code>${escapeHtml(rep.seedFingerprint)}</code></div>
            <div class="metric-pill"><strong>Edición / TitleID:</strong> ${escapeHtml(rep.effectiveEdition)} (<code>${escapeHtml(rep.resolvedTitleId)}</code>)</div>
            <div class="metric-pill"><strong>Objetos Clave protegidos:</strong> ${rep.protectedKeyItemsCount}</div>
            ${diffSummaryHtml}
          </div>
          <details>
            <summary style="cursor:pointer;font-weight:600;">Ver comprobaciones de persistencia ARC0 y rutas exportadas</summary>
            <ul style="margin:8px 0 6px;padding-left:18px;">${checksHtml}</ul>
            <div style="margin-top:6px;"><strong>Archivos generados:</strong></div>
            <ul style="margin:4px 0 0;padding-left:18px;">${exportsHtml}</ul>
          </details>
        `;
        resultBox.classList.remove("hidden");

        const installBtn = $("btnInstallToCitraNow");
        if (installBtn) {
          installBtn.addEventListener("click", async () => {
            installBtn.disabled = true;
            const msgEl = $("citraQuickInstallMsg");
            msgEl.textContent = "⏳ Copiando romfs/ie_a.fa en la carpeta de mods de Citra...";
            try {
              const r = await fetch("api/citra/install", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                  sourcePath: currentOutDir,
                  edition: rep.effectiveEdition || $("editionSelect").value,
                  customTitleIdOverride: $("titleIdOverride").value,
                  citraModsRoot: $("citraModsRootInput").value.trim()
                })
              });
              const d = await r.json();
              if (!r.ok) throw new Error(d.error || "No se pudo instalar en Citra.");
              msgEl.innerHTML = `✅ <strong>Listo para jugar en Citra:</strong> <code>${escapeHtml(d.installedIeAFaPath)}</code> (Recuerda iniciar <strong>Partida Nueva</strong> para ver las nuevas técnicas iniciales del Raimon).`;
            } catch (e) {
              msgEl.innerHTML = `⚠️ ${escapeHtml(e.message)}`;
            } finally {
              installBtn.disabled = false;
            }
          });
        }

        const openBtn = $("btnOpenGeneratedFolder");
        if (openBtn) {
          openBtn.addEventListener("click", async () => {
            await fetch("api/app/open-folder", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ folderPath: currentOutDir })
            });
          });
        }
        return;
      }
    }
  }

  function escapeHtml(str) {
    return String(str ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  setWorkflowMode("cia-randomize");
})();
