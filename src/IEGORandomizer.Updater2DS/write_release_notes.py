#!/usr/bin/env python3
import sys

notes_file, cia_mb, cia_name = sys.argv[1:4]

content = f"""## Parche + Randomizador de Semillas Todo-en-1 `.CIA` para Nintendo 2DS — `v1.2.4` (~{cia_mb} MB)

Este único archivo **NO reescribe otro juego de 3 GB**. Es una herramienta **ligera (~{cia_mb} MB) e independiente** que instalas en segundos en tu Nintendo 2DS con **FBI** y opera directamente sobre el **`Inazuma Eleven GO: Luz (..._Randomizado.cia)` que ya tienes instalado en la consola**, sin borrar vídeos, voces ni tu partida guardada.

---

### ¿Qué corrige e incluye esta versión `v1.2.4` de UNA sola vez?
1. **Fix definitivo del error `0xC880448D`**: lector directo por **`PxiFS0` (`svc 0xB0`)** sobre el título instalado en la SD de Luma3DS (ya no depende de `romfsMountFromTitle` ni pide cartucho físico).
2. **Todos los bugs del juego corregidos a la vez**:
   - Fix del crasheo tras la pantalla de formación contra **Caballeros Templarios (`btl0002`)** y esqueleto `cp0010` (Victor Blade, uniforme `903`).
   - **Coherencia visual 100%** entre cinemáticas, mapa y partidos (retrato `.xi` + cabeza RPG/cinemática + cabeza de partido sincronizados).
   - **Pachangas del Capítulo 1 ganables** (tope de parada en pachangas iniciales + técnica ofensiva garantizada a Nivel 1 para Arion y para cualquier jugador sin Espíritu Guerrero inicial).
   - Restauración íntegra de `ITEM_REF` y textos en español de 3 columnas (`system_text_es.cfg.bin`).
3. **3 Modos integrados en el menú de tu Nintendo 2DS (sin necesitar PC nunca más)**:
   - **`[A]` REPARAR PARTIDA ACTUAL (Mantener mi Seed y Save)**: repara todos los bugs conservando al 100% tus jugadores, técnicas y tu partida guardada actual.
   - **`[X]` NUEVA SEED MANUAL (Teclado táctil 2DS)**: abre el teclado táctil de la 2DS para que escribas manualmente la semilla que quieras (ej. `DAVID2026`) y te genera una randomización nueva **Todo Activado** directamente en la consola.
   - **`[Y]` NUEVA SEED ALEATORIA (1 botón)**: genera una semilla aleatoria nueva al instante directamente desde la 2DS.

---

### Cómo instalar y usar en tu Nintendo 2DS
1. Abre **FBI** -> `Remote Install` -> `Scan QR Code` y escanea **`QR_FBI_ParcheYRandomizer_v1.2.4.png`** (si ya tenías instalado el icono `v1.2.3`, lo sobrescribe automáticamente).
2. Sal al **Menú HOME**, abre **`IEGO Luz Randomizer v1.2.4`** y elige **`[A]`** (reparar partida actual), **`[X]`** (escribir seed manual) o **`[Y]`** (seed aleatoria).
3. Cuando termine (~2–4 min), abre tu **Inazuma Eleven GO: Luz** de siempre y juega. ¡Puedes conservar el icono en tu 2DS para cambiar de seed cuando quieras!
"""

with open(notes_file, "w", encoding="utf-8") as f:
    f.write(content)
