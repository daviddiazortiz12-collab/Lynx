#!/usr/bin/env python3
import sys

notes_file, size_mb, cia_name = sys.argv[1], sys.argv[2], sys.argv[3]

content = f"""## Actualizador Ligero Independiente `.CIA` para Nintendo 2DS — `v1.2.3` (~{size_mb} MB)

Este archivo **NO reescribe un juego entero de 3 GB**. Es una **actualización pequeña e independiente (`~{size_mb} MB`)** que descargas directamente en tu Nintendo 2DS con **FBI** y aplica el parche `v1.2.3` sobre el *Inazuma Eleven GO: Luz* que **ya tienes instalado** en la consola, conservando intactos tus vídeos, voces en español y tu partida guardada (`00000001.sav`).

### Cómo instalar y aplicar en tu Nintendo 2DS (2 minutos)
1. **Instalar el `.CIA` ligero en FBI**:
   - **Con código QR (sin sacar la SD)**: Abre **FBI** -> `Remote Install` -> `Scan QR Code` y escanea la imagen `QR_Instalar_En_FBI_2DS.png` adjunta abajo.
   - **O por tarjeta SD**: Copia `{cia_name}` a tu SD e instálalo desde `FBI -> SD -> Install CIA`.
2. **Aplicar la actualización al juego ya instalado**:
   - Vuelve al **Menú HOME** de tu 2DS, abre el nuevo icono **`IEGO Luz Parche v1.2.3`** y pulsa **`[A]`**.
   - El actualizador leerá los archivos base intactos de tu juego ya instalado y aplicará únicamente los cambios corregidos de `v1.2.3` (y activará automáticamente `enable_game_patching = 1` en Luma3DS si estuviera desactivado).
3. Una vez aplicado, abre tu **Inazuma Eleven GO: Luz** normal de siempre (incluso puedes borrar después el icono del actualizador desde Configuración si quieres: el parche queda aplicado en tu juego).

### Qué corrige `v1.2.3`
- **Sin crasheo tras la formación del Raimon contra Caballeros Templarios (`btl0002`)**: preserva `ITEM_REF_BEGIN` al 100% en `item_config.cfg.bin`, respeta `TEXT_INFO` de 3 parámetros en `system_text_*.cfg.bin` y protege el esqueleto `(Body, Gender)` para el uniforme especial `903` de Victor Blade (`para_cp0010`).
- **Coherencia total cinemática + mapa + partido**: Samguk y todos los personajes mantienen exactamente el mismo modelo 3D y retrato tanto en cinemáticas (`evcXXXX`) como en partidos.
"""

with open(notes_file, "w", encoding="utf-8") as f:
    f.write(content)
