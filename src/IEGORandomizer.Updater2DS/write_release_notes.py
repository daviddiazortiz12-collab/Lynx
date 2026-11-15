#!/usr/bin/env python3
import sys

notes_file, cia_mb, cia_name = sys.argv[1:4]
content = f"""## Randomizador y reparador 2DS v1.2.7 — parche de prueba para fichajes

Para **Inazuma Eleven GO: Luz europeo** (0004000000112F00) y la instalación randomizada utilizada al preparar esta aplicación. No es compatible con Sombra. Archivo: `{cia_name}` (~{cia_mb} MB).

### Qué cambia
En las semillas generadas en la consola, las cartas Fichatrón conservan su jugador, indicador y requisitos originales en lugar de permutarlos independientemente. Además, las cartas Fichatrón se protegen frente a la sustitución como objetos corrientes en tiendas y recompensas. Los jugadores, sus estadísticas y el resto de la randomización siguen activos.

Este cambio evita dos posibles causas de desajustes, pero **no se ha confirmado que fueran la causa de un fallo en la consola ni se ha probado todos los métodos de fichaje jugando**. En el juego, ganar una pachanga no garantiza que un jugador se quiera unir.

### Cómo aplicar a tu semilla
1. Haz copia de tu partida con Checkpoint y copia a un lugar seguro la carpeta `luma/titles/0004000000112F00` de tu SD antes de hacer cambios.
2. Instala la aplicación y abre **IEGO Luz Randomizer v1.2.7**. Pulsa **[X]** para escribir la misma semilla; normalmente el teclado propone la última guardada. Confírmala y espera a que termine. Si no conoces la semilla anterior, no podemos garantizar conservar su randomización.
3. **No uses [A] para actualizar una semilla creada en consola:** [A] reconstruye la reparación preparada para el juego instalado y puede sustituir el LayeredFS de tu semilla actual. [Y] genera otra semilla distinta.
4. Comprueba con un agente del Fichatrón si aparecen cartas, y prueba las pachangas. El juego puede no ofrecer un fichaje tras cada victoria. Si no aparecen, anota el capítulo, la seed y qué equipo/agente has probado.

Se han ejecutado pruebas de construcción y de archivos, no una partida completa en hardware real. No se garantiza la ausencia de errores ni que se conserven todos los efectos de una partida guardada al regenerar una semilla.
"""
with open(notes_file, "w", encoding="utf-8") as f:
    f.write(content)
