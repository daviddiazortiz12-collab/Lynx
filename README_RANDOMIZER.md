# IEGO Randomizer — Inazuma Eleven GO: Luz y Sombra

Aplicación independiente de PC para generar partidas randomizadas reproducibles, seguras y libres de softlocks en **Inazuma Eleven GO: Luz** y **Inazuma Eleven GO: Sombra** (Nintendo 3DS / Citra), construida reutilizando la capa de datos binarios de **Lynx** y **StudioElevenLib** sin duplicar código ni depender de los formularios de edición manual de Lynx.

---

## 1. Arquitectura Modular

El proyecto se organiza en `IEGORandomizer.sln` con separación estricta entre acceso a datos, motor de randomización, interfaz de usuario y pruebas automatizadas:

```text
/
├── Lynx/                           # Proyecto original Lynx (datos, mappers y lógica reutilizados directamente)
├── Libs/StudioElevenLib.dll        # Librería Level-5 (ARC0, CfgBin, T2bþ, PTreeNode, CRC32, compresores)
├── IEGORandomizer.sln              # Solución independiente del randomizer
├── src/
│   ├── Lynx.Core/                  # Capa de datos sin dependencias de WinForms:
│   │   ├── Games/SafeGOGame.cs     # Subclase segura de solo lectura sobre Lynx.InazumaEleven.Games.Game
│   │   ├── Games/TeamSlotAccessor.cs
│   │   ├── RomFs/Arc0SafeArchive.cs        # Lector multiplataforma ARC0 y escritura exclusiva en copia
│   │   ├── RomFs/Nintendo3dsRomReader.cs   # Parser puro C# para .3ds (NCSD), .cxi (NCCH) e IVFC/RomFS
│   │   ├── RomFs/GameWorkspace.cs          # Sesión de trabajo con verificación SHA-256 del original
│   │   ├── Maps/MapDataService.cs          # Cofres TBoxConfig (Luz/Sombra), pachangas EncountMap y FUNCPOINT
│   │   └── Scripts/NutbScriptAuditor.cs    # Auditor de bytecode Squirrel 2.2.3 (.nutb, 0xFAFA "RIQS")
│   │
│   ├── IEGORandomizer.Engine/      # Motor desacoplado de la interfaz gráfica:
│   │   ├── Seeding/                # SeedEngine (HMAC-SHA256) + DeterministicRng (Xoshiro256** + Sattolo Derangement)
│   │   ├── Protection/             # StoryProtectionService (Raimon historia, Objetos Clave, FUNCPOINT, .nutb)
│   │   ├── Validation/             # VisualCoherenceValidator (ModelNumber < 2048, .xi/.xc) + ArchiveIntegrityValidator
│   │   ├── Modules/                # Subgeneradores aislados:
│   │   │   ├── PlayerRandomizerModule.cs   # A. Jugadores, stats, elementos, posiciones, Avatares y PalPack
│   │   │   ├── SkillRandomizerModule.cs    # B. Supertécnicas, talentos, Avatares wk* y usabilidad de PT Nv.1
│   │   │   ├── TeamRandomizerModule.cs     # C. Equipos rivales, pachangas, dorsales, tácticas y equipamiento
│   │   │   ├── RewardRandomizerModule.cs   # D. Cofres del mapa, cadenas scr_br*, drops y tiendas
│   │   │   └── UniformRandomizerModule.cs  # E. Equipaciones con verificación de modelos 3D uza/uzb
│   │   ├── Export/ModPackageExporter.cs    # Paquetes Citra, Luma3DS LayeredFS e ie_a.fa independiente
│   │   └── RandomizerCoordinator.cs        # Orquestador sin estado con validación pre y post-exportación
│   │
│   └── IEGORandomizer.App/         # Aplicación de escritorio PC (Interfaz Gráfica + modo CLI):
│       ├── Program.cs              # Servidor local + lanzador de ventana nativa de escritorio
│       ├── CliRunner.cs            # Ejecución por línea de comandos (--cli)
│       └── wwwroot/                # Interfaz enfocada en generación de runs y Modo Partida a Ciegas
│
└── tests/
    └── IEGORandomizer.Tests/       # Suite de pruebas unitarias e integración (ARC0, .3ds, seeds, derangement, seguridad)
```

---

## 2. Garantías de Seguridad e Integridad

1. **Inmutabilidad del archivo original**:
   - Los archivos de entrada (`.3ds`, carpeta `RomFS` o `ie_a.fa`) se abren **exclusivamente con `FileAccess.Read`**.
   - `SafeGOGame.Save()` bloquea explícitamente cualquier escritura in-situ; toda exportación pasa por `Arc0SafeArchive.SaveCopyTo(...)` hacia una carpeta de salida distinta y verifica que el hash **SHA-256** del archivo de entrada permanezca idéntico byte a byte al finalizar.
   - El programa **jamás** interactúa con consolas físicas Nintendo 2DS/3DS ni utiliza herramientas de hardware.
2. **Coherencia Visual (`VisualCoherenceValidator`)**:
   - Verifica el límite interno del motor de IEGO1 (`0 < ModelNumber < 2048`, documentado en `IE4_CHARA_FLAG_GAME_MAX` de `code.bin`).
   - Excluye maniquíes/NPCs sin cuerpo (`Body == 0` o `CharaBaseType != 1`) o sin recursos gráficos (`.xi` en `/data/bustup/face/`, `.xc` en `/data/chr/model/rpg/face/` y `/data/chr/model/waza/face/`).
   - El intercambio visual opcional transfiere de forma atómica `(NameHash, NicknameHash, Body, Gender, Skin, Year)` junto con sus archivos `.xi`/`.xc`, evitando cabezas o tonos de piel desajustados.
   - Verifica que toda equipación asignada tenga sus modelos 3D `uzaXXXX.xc` / `uzbXXXX.xc` y sincroniza las equipaciones y jugadores de pachanga con los modelos del mapa (`ENCOUNT_INFO`).
3. **Protección Anti-Softlock (`StoryProtectionService`)**:
   - Protege todos los **Objetos Clave** (`ITEM_IMPORTANT` / `ItemCategory == 14`), **Tickets Versus** (`ItemCategory == 15`), cofres vinculados a eventos de mapa (`FUNCPOINT` `TBoxBitCheck`), y objetos/partidos/habilidades/Espíritus Guerreros invocados por scripts compilados **Squirrel 2.2.3 (`.nutb`)**.
   - Garantiza mediante la fórmula real de `GOStatCalculator` (`0x0033E404`) que toda supertécnica inicial tenga un coste de PT utilizable desde nivel 1.
4. **Semillas Deterministas y Modo Partida a Ciegas**:
   - Cada subsistema deriva su propio flujo `Xoshiro256**` mediante `HMAC-SHA256(seed, subsistema)` y permuta jugadores y cartas PalPack mediante **derangements de Sattolo** ($p(x) \neq x$).
   - El **Modo Partida a Ciegas** está activado por defecto: oculta asignaciones concretas en la interfaz y omite `spoiler_log.txt`.

---

## 3. Requisitos y Compilación en PC

- **.NET SDK 10.0** (compatible con Windows x64, Linux y macOS; no requiere paquetes externos de Internet gracias a `Libs/StudioElevenLib.dll` incluido en el repositorio).

```bash
dotnet build IEGORandomizer.sln --configuration Release
dotnet run --project tests/IEGORandomizer.Tests/IEGORandomizer.Tests.csproj --configuration Release
```

---

## 4. Uso en PC (Interfaz Gráfica y CLI)

### Modo Escritorio (GUI)
```bash
dotnet run --project src/IEGORandomizer.App/IEGORandomizer.App.csproj --configuration Release
```
1. Selecciona tu entrada (`.3ds` desencriptado, carpeta `RomFS` extraída o `ie_a.fa`).
2. Elige la edición (**Luz** o **Sombra**, detectada automáticamente en contenedores `.3ds`).
3. Activa las categorías deseadas, introduce una **Seed** o pulsa **Aleatoria**, mantén activado **Modo Partida a Ciegas** y elige una **Carpeta de salida** nueva.
4. Pulsa **Generar Run Randomizada**.

### Modo Línea de Comandos (CLI)
```bash
dotnet run --project src/IEGORandomizer.App/IEGORandomizer.App.csproj --configuration Release -- \
  --cli \
  --input "C:\Juegos\IEGO\ie_a.fa" \
  --output "C:\Juegos\IEGO_Mods\Run_01" \
  --seed "PEGASO-2026" \
  --edition luz
```

---

## 5. Estructura de Salida Exportada (Citra y LayeredFS)

En la carpeta de destino seleccionada se generan automáticamente:
- `Citra/load/mods/{TitleID}/romfs/ie_a.fa` + `LEEME_CITRA.txt` (listo para copiar en *Abrir ubicación de mods* de Citra).
- `Luma3DS/luma/titles/{TitleID}/romfs/ie_a.fa` + `LEEME_LAYEREDFS.txt` (estructura de archivos estándar preparada en PC).
- `Standalone/ie_a.fa` (archivo ARC0 independiente).
- `run_manifest.json` (metadatos reproducibles, huella de verificación e informe de validación sin spoilers).

Title IDs soportados de serie:
- **Inazuma Eleven GO: Luz (EUR)**: `0004000000112F00`
- **Inazuma Eleven GO: Sombra (EUR)**: `0004000000113000` (con selector opcional para revisión alternativa `000400000017C200`).
