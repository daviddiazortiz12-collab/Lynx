#!/usr/bin/env python3
import sys

notes_file, cia_mb, cia_name = sys.argv[1:4]
content = f"""## Randomizador y reparador 2DS v1.2.6 — versión de prueba

Herramienta para **Inazuma Eleven GO: Luz europeo** (0004000000112F00), destinada a la copia randomizada indicada durante la preparación. No es compatible con Sombra. Archivo: `{cia_name}` (~{cia_mb} MB).

### Cambios en semillas nuevas [X] / [Y]
- Corregido el índice de cofres que escribía sobre el identificador de control en lugar del objeto de Sombra.
- Se incluyen cofres de mapas y rutas, objetos de tiendas y recompensas de equipos, manteniendo protegidos los identificados como importantes para la historia. Algunos pueden permanecer protegidos.
- Estadísticas sorteadas independientemente de los valores originales, dentro de límites; se conservan excepciones de seguridad. Las técnicas activas pueden elegirse entre más categorías sin depender de la posición. Hay exclusiones de compatibilidad y seguridad.
- Si faltaba una cara original entre los archivos instalados, la receta puede incluirla para ampliar los personajes candidatos al intercambio visual. Aun así, los personajes y equipos con restricciones de historia o recursos incompatibles pueden permanecer sin cambiar.
- **No se ha implementado el cambio temporal de técnica original durante el evento:** los huecos que se han identificado como referenciados por guiones de historia siguen protegidos. No se garantiza que el análisis detecte todos los eventos.

### Antes de instalar y jugar
1. Haz copia de seguridad de tu partida con Checkpoint y de `luma/titles/0004000000112F00` de la SD. Crear una semilla sustituye el LayeredFS anterior; no borra intencionadamente el save, pero mezclar una partida existente con una nueva semilla puede producir resultados inesperados. Para una run nueva, usa una partida nueva.
2. Instala con FBI y abre **IEGO Luz Randomizer v1.2.6**. `[X]` genera una semilla manual y `[Y]` una aleatoria; `[A]` repara la partida instalada y no aplica estas mejoras de nueva semilla.
3. Se han ejecutado pruebas automáticas y de reconstrucción sobre los archivos reales, no una partida completa en hardware real. **No se puede garantizar ausencia de fallos ni de bloqueos**; pruébalo con la copia de seguridad disponible.
"""
with open(notes_file, "w", encoding="utf-8") as f:
    f.write(content)
