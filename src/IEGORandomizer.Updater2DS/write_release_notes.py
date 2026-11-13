#!/usr/bin/env python3
import sys

notes_file, repair_mb, repair_cia, fresh_mb, fresh_cia, fresh_seed = sys.argv[1:7]

content = f"""## Actualización Ligera Independiente `.CIA` para Nintendo 2DS — `v1.2.3` (Sin reescribir 3 GB)

Estos archivos **NO reescriben otro juego entero de 3 GB**. Son **actualizaciones pequeñas e independientes** que descargas en segundos en tu Nintendo 2DS con **FBI** y aplican la corrección `v1.2.3` directamente sobre el **`Inazuma Eleven GO: Luz (..._Randomizado.cia)` que ya tienes dentro de la consola**, sin borrar tus vídeos, voces en español ni tu partida guardada (`00000001.sav`).

---

### Opción 1 (Recomendada para seguir tu partida actual en el Cap. 1): **`{repair_cia}`** (~{repair_mb} MB)
- **Mantiene exactamente tu semilla actual** (mismos jugadores, técnicas, espíritus guerreros, entrenadores y equipos que ya llevas en tu partida guardada, incluyendo a Samguk = Jude Sharp).
- **Corrige el crasheo tras la pantalla de formación contra Caballeros Templarios (`btl0002`)** y **sincroniza al 100% los modelos de cinemáticas con los de partido** (Samguk y todos los actores de historia ahora lucen exactamente igual en cinemáticas, mapa y partidos).
- **No necesitas empezar Partida Nueva**: instala este `.CIA` ligero (~{repair_mb} MB), ábrelo en el Menú HOME, pulsa **`[A]`** y continúa directamente tu partida guardada frente a Caballeros Templarios.
- **QR directo para FBI**: `QR_FBI_RepararPartidaActual_v1.2.3.png`

---

### Opción 2 (Nueva Semilla *Todo Activado* desde cero): **`{fresh_cia}`** (~{fresh_mb} MB — Seed `{fresh_seed}`)
- Si además quieres rejugar con una **nueva semilla aleatoria con Todo Activado** (`{fresh_seed}`) aplicada también encima del juego que ya tienes en la consola (sin bajar otros 3 GB), usa este `.CIA` ligero (~{fresh_mb} MB).
- **QR directo para FBI**: `QR_FBI_NuevaSemilla_v1.2.3.png`

---

### Cómo instalar y aplicar en tu Nintendo 2DS (en menos de 2 minutos)
1. **Instalar el `.CIA` ligero con FBI**:
   - **Por código QR (sin sacar la tarjeta SD)**: Abre **FBI** -> `Remote Install` -> `Scan QR Code` y apunta con la cámara al QR de la opción que prefieras (`QR_FBI_RepararPartidaActual_v1.2.3.png` o `QR_FBI_NuevaSemilla_v1.2.3.png`).
   - **O por tarjeta SD**: Copia el archivo `.cia` a tu SD e instálalo desde `FBI -> SD -> Install CIA`.
2. **Aplicar la actualización al juego ya instalado**:
   - Sal al **Menú HOME** de tu 2DS, abre el icono nuevo **`IEGO Luz Parche v1.2.3`** y pulsa **`[A]`**.
   - El actualizador leerá los ~850 MB intactos del juego que ya está dentro de tu consola, aplicará únicamente los archivos corregidos de `v1.2.3` y comprobará automáticamente `enable_game_patching = 1` en Luma3DS.
3. Abre tu **Inazuma Eleven GO: Luz** de siempre y juega (puedes borrar después el icono del actualizador si quieres; la actualización se queda aplicada en tu juego).
"""

with open(notes_file, "w", encoding="utf-8") as f:
    f.write(content)
