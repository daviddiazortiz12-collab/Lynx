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
  $("btnPickCitraModsFolder").addEventListener("click", () =>
    openPicker("citraModsRootInput", "folder", "Seleccionar carpeta load/mods de Citra o Azahar"));

  // Local Diagnostic Mode (zero spoilers)
  $("btnDiagnoseLocal").addEventListener("click", async () => {
    const diagBox = $("diagnosticResultBox");
    const inputPath = $("inputPath").value.trim();
    if (!inputPath) {
      diagBox.className = "result-box failure";
      diagBox.innerHTML = "<strong>Falta entrada:</strong> Selecciona primero tu archivo <code>ie_a.fa</code>, carpeta <code>RomFS</code> o <code>.3ds</code>.";
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
          targetOutputOrCitraDir: $("outputDir").value.trim() || $("citraModsRootInput").value.trim()
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

        const currentOutDir = $("outputDir").value.trim();
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
            <div><strong>✅ ¡Partida generada y verificada en disco!</strong> ${rep.blindPlayMode ? "<span>(Modo a Ciegas activo: 0 spoilers mostrados)</span>" : ""}</div>
            <div style="display:flex;gap:8px;flex-wrap:wrap;">
              <button type="button" class="btn btn-primary" id="btnInstallToCitraNow">🎮 Instalar directo en Citra</button>
              <button type="button" class="btn btn-secondary" id="btnOpenGeneratedFolder">📂 Abrir carpeta de salida</button>
            </div>
          </div>
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
})();
