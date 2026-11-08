(() => {
  const $ = (id) => document.getElementById(id);

  let modalTargetInput = null;
  let modalDirsOnly = false;
  let modalParentDir = "";

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
  async function openPicker(targetInputId, mode, title) {
    try {
      const nativeRes = await fetch(`api/fs/pick-native?mode=${encodeURIComponent(mode)}`, { method: "POST" });
      if (nativeRes.ok) {
        const nativeData = await nativeRes.json();
        if (nativeData.supported && nativeData.selectedPath) {
          $(targetInputId).value = nativeData.selectedPath;
          if (targetInputId === "inputPath") inspectInputPath(nativeData.selectedPath);
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
    modalDirsOnly = mode === "folder";
    $("modalTitle").textContent = title;
    $("modalNewSubfolder").style.display = modalDirsOnly ? "block" : "none";
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
        }
      });
      entriesEl.appendChild(row);
    });
  }

  function closeModal() {
    $("browserModal").classList.add("hidden");
  }

  $("btnPickInputFile").addEventListener("click", () =>
    openPicker("inputPath", "file", "Seleccionar archivo .3ds o ie_a.fa"));
  $("btnPickInputFolder").addEventListener("click", () =>
    openPicker("inputPath", "folder", "Seleccionar carpeta RomFS extraída"));
  $("btnPickOutputFolder").addEventListener("click", () =>
    openPicker("outputDir", "folder", "Seleccionar carpeta de salida separada"));

  $("btnCloseModal").addEventListener("click", closeModal);
  $("btnModalCancel").addEventListener("click", closeModal);
  $("btnModalUp").addEventListener("click", () => {
    if (modalParentDir) loadModalDirectory(modalParentDir);
  });
  $("btnModalGo").addEventListener("click", () => loadModalDirectory($("modalCurrentPath").value));
  $("btnModalSelectCurrent").addEventListener("click", () => {
    let chosen = $("modalCurrentPath").value.trim();
    const sub = $("modalNewSubfolder").value.trim();
    if (sub && modalTargetInput === "outputDir") {
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
        customTitleIdOverride: $("titleIdOverride").value
      }
    };
  }

  $("btnGenerate").addEventListener("click", async () => {
    const inputPath = $("inputPath").value.trim();
    const outputDirectory = $("outputDir").value.trim();
    const resultBox = $("resultBox");
    const progressArea = $("progressArea");

    resultBox.classList.add("hidden");

    if (!inputPath || !outputDirectory) {
      resultBox.className = "result-box failure";
      resultBox.innerHTML = "<strong>Faltan rutas:</strong> Selecciona el archivo/carpeta de entrada y una carpeta de salida separada.";
      resultBox.classList.remove("hidden");
      return;
    }

    $("btnGenerate").disabled = true;
    progressArea.classList.remove("hidden");
    $("progressBarFill").style.width = "2%";
    $("progressLabel").textContent = "Enviando configuración al motor...";

    try {
      const res = await fetch("api/jobs", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          inputPath,
          outputDirectory,
          options: buildOptionsPayload()
        })
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

      if (state.status === "completed" && state.report) {
        const rep = state.report;
        $("seedInput").value = rep.seed;
        const checksHtml = (rep.validationMessages || [])
          .map((m) => `<li>✓ ${escapeHtml(m)}</li>`)
          .join("");
        const exportsHtml = (rep.exportedPaths || [])
          .map((p) => `<li><code>${escapeHtml(p)}</code></li>`)
          .join("");

        resultBox.className = "result-box success";
        resultBox.innerHTML = `
          <div><strong>✅ ¡Run generada y verificada correctamente!</strong> ${rep.blindPlayMode ? "<span>(Modo a Ciegas activo: 0 spoilers mostrados)</span>" : ""}</div>
          <div class="result-grid">
            <div class="metric-pill"><strong>Seed:</strong> <code>${escapeHtml(rep.seed)}</code></div>
            <div class="metric-pill"><strong>Huella:</strong> <code>${escapeHtml(rep.seedFingerprint)}</code></div>
            <div class="metric-pill"><strong>Edición / TitleID:</strong> ${escapeHtml(rep.effectiveEdition)} (<code>${escapeHtml(rep.resolvedTitleId)}</code>)</div>
            <div class="metric-pill"><strong>Objetos Clave protegidos:</strong> ${rep.protectedKeyItemsCount}</div>
          </div>
          <details>
            <summary style="cursor:pointer;font-weight:600;">Ver comprobaciones de integridad y rutas exportadas</summary>
            <ul style="margin:8px 0 6px;padding-left:18px;">${checksHtml}</ul>
            <div style="margin-top:6px;"><strong>Archivos generados:</strong></div>
            <ul style="margin:4px 0 0;padding-left:18px;">${exportsHtml}</ul>
          </details>
        `;
        resultBox.classList.remove("hidden");
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
})();
