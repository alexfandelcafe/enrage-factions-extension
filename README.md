# EnRAGE Player Faction Extension — v0.3.6

Extensión independiente para crear y administrar una facción propia en enRAGE. Mantiene al personaje activo de la historia como líder nativo, registra el campamento en el sistema de campamentos de enRAGE y añade un administrador interactivo en el campamento.

## Cambios de v0.3.6 — campamento por prestigio, vida social y defensa

### Mejora visual según el prestigio

- `CampKit.CampLooks["player_faction"]` se configura desde la escala de PRESTIGIO de enRAGE en cinco etapas (0–19, 20–39, 40–59, 60–79 y 80–100).
- Las etapas incorporan progresivamente más tiendas, camas, asientos, faroles, leña, cajas, barriles, cofres y botellas. Cuando cambia de etapa, el kit físico se reconstruye una sola vez y solo cuando el campamento está cerca y la colisión está cargada.

### Interacción social, charlas y escenarios

- Los miembros registrados en el campamento exponen una interacción contextual `Hablar`; si la API de prompt de un ped no devuelve un grupo válido, queda habilitada la ruta de teclado contextual como respaldo.
- `Saludar` aumenta `Hacia ti`; `Provocar` lo disminuye, pero no convierte al miembro en enemigo del jugador. Los valores se persisten por ID del roster y se reflejan en la ficha de enRAGE y en la disposición que usa Companion.
- Los miembros intentan usar puntos de escenario próximos a los objetos del `CampKit`; los puntos se reservan para reducir solapamientos. Si no hay un escenario de objeto libre o falla el intento, se escoge una actividad alternativa de pie/sentado, lectura, humo, guitarra, fuego, pesca y otros escenarios de campamento.
- Periódicamente, dos miembros próximos intentan intercambiar líneas ambientales mediante `PLAY_PED_AMBIENT_SPEECH_NATIVE` (`0x8E04FEDD28D42462`). Se pasa un diccionario de voz vacío para pedir al juego que use la voz ambiental actual del propio ped, en vez de forzar el banco de voz de un sheriff. El formato de `ScriptedSpeechParams` sigue la estructura de 0x40 bytes descrita por [RDR2 NativeDB](https://www.rdr2mods.com/nativedb/index/audio/_play_ambient_speech1/) y la implementación de [ScriptHookRDR2DotNet V2](https://github.com/onenowy/ScriptHookRDR2DotNet-V2/blob/master/source/scripting_v3/RDR2/Entities/Peds/Ped.cs). La reproducción sigue dependiendo de que el juego admita la línea y de los bancos de voz disponibles; si una frase no se reconoce, el campamento continúa funcionando.

### Defensa reactiva

- Los residentes solo pasan a combate cuando el juego marca que un agresor está combatiendo al jugador, a un residente o al administrador. Un NPC que solo cruza el campamento no activa la defensa.
- Cuando cesa el contacto de combate, los defensores abandonan la tarea tras un breve margen y regresan a la rutina del campamento.

### Bloqueo de falsos campamentos enemigos propios

- Antes de `LearnCampSites`, la extensión vuelve a normalizar el roster y marca los handles exactos de los miembros existentes como entidades propias de enRAGE.
- Los prefijos de `SpawnCamp` y `RevealCamp`, junto con el posprocesado de `AssignCamps`, impiden que los miembros o el `CampSite` de `player_faction` se conviertan en un campamento enemigo o generen la notificación «encontraste un campamento».
- La comprobación de compatibilidad también elimina `LiveCamp` obsoletos asignados a `player_faction` o detectados dentro de 150 m del sitio propio. Los sitios nativos válidos de otras zonas se conservan; las relaciones/guerras con facciones externas siguen sujetas a los ajustes nativos.

## Cambios de v0.3.5 — roster físico, fuerza inicial y ajustes nativos

- Los registros de Companion `extcomp_` / `extvdl_` se recuperan tanto de `NemesisMod.Roster` como de `StoryRoster`; los registros antiguos de Companion se migran al roster normal de `player_faction` para que no queden invisibles en otra pestaña del roster.
- Cuando el campamento está cargado, la extensión materializa hasta tres miembros faltantes por escaneo a partir de los registros vivos y sus `ModelHash`, preservando el `Id` del roster y, cuando Companion guardó un atuendo, restaurándolo. No busca NPCs por modelo y no crea un segundo representante si el mismo `RosterId` ya tiene una entidad rastreada o guardada en este proceso. Tras reiniciar el juego, se vuelve a materializar la misma identidad persistente porque los peds del mundo no sobreviven como entidades vivas entre sesiones.
- El puente ya no depende de que `EnRAGEHooks._latestMainInstance` haya recibido su primer tick; si sigue en `null` al recargar scripts, usa la instancia activa de enRAGE. Si aún faltan listas o caché, escribe un diagnóstico y reintenta.
- La fuerza inicial se fija a cero y se baselina la lista de IDs que ya existían en el roster o la caché de Companion en el momento de la fundación. Así, los registros antiguos que Companion normaliza después de fundar la banda no se cuentan por error como reclutas nuevos. Los IDs incorporados después de la fundación sí suman `NemesisMod.NamedWorth(rank)`.
- Las opciones del administrador escriben y verifican los campos nativos de `Gang`: `TurfWars`, `Feuds`, `GangLifeEvents` y `AnswersGunfire`. Son permisos para que enRAGE ejecute sus sistemas bajo las condiciones normales, no órdenes que inicien inmediatamente una guerra/evento.
  - `TurfWars`: la lógica nativa requiere que las dos facciones participantes tengan guerras territoriales habilitadas y suficiente control en el mismo distrito.
  - `Feuds`: habilita que enRAGE considere esa facción para sus sistemas nativos de rivalidad; no crea por sí sola una rivalidad concreta.
  - `GangLifeEvents`: habilita eventos cuyo ámbito es una banda y que además cumplen sus condiciones/azar.
  - `AnswersGunfire`: cambia los comportamientos nativos de patrullas/raids ante disparos cercanos según los radios y estados del evento; no hace que cada NPC del campamento ataque siempre que oye un disparo.

## Cambios de v0.3.4 — reinicio, prestigio y jerarquía de miembros

### Administración del campamento después de recargar scripts

- Al abortar/cerrar una instancia del script, se ocultan y eliminan los prompts nativos del fundador y del administrador y se desinstalan únicamente los hooks Harmony de esta extensión.
- Al iniciar de nuevo, los hooks se instalan con el mismo ID de Harmony después de limpiar cualquier patch residual de una instancia anterior.
- El prompt del administrador usa el grupo de interacción real del ped, permite el grupo nativo `0` si el juego lo devuelve y comprueba el mismo modo de mantener pulsado que Companion.
- La interacción contextual tiene una ruta de entrada directa de respaldo; el registro `extension.log` indica si el menú abrió, si falló el registro de la facción, si faltó el renderer y qué controles de navegación recibió.
- Antes de abrir la administración se vuelve a asegurar el registro nativo de `player_faction`. Si enRAGE todavía no está disponible, el menú informa del motivo en lugar de quedar aparentemente abierto sin funcionar.

### Prestigio de la banda

En la barra de la facción del jugador, el rótulo **NOTORIEDAD** se reemplaza por **PRESTIGIO**, con los estados DESCONOCIDA, EMERGENTE, RECONOCIDA, TEMIDA y LEGENDARIA. Se reutiliza la escala de 0 a 100 que enRAGE ya expone para esa banda; la extensión no modifica el cálculo interno de notoriedad ni la IA de otras facciones.

### Ascender y degradar miembros desde su ficha

- Al seleccionar un miembro vivo de `player_faction` en el roster, sus acciones de detalle incluyen **Ascender rango** y/o **Degradar rango** según su rango actual.
- El ascenso usa el método nativo `NemesisMod.Promote(NemesisRecord)` cuando está disponible; la degradación baja un nivel y reinicia su XP de rango. Los límites se mantienen entre 1 y 5.
- Tras cada cambio, se guarda `Roster` y se vuelve a aplicar la relación de Companion para ese mismo registro.

### Ficha de miembro de banda y «HACIA TI»

- Companion mantiene la relación viva mediante `Hate`, `Fear` y `Disposition`; este build aplica `Disposition=Neutral` al rango 1 y `Friendly` a los rangos 2–5, sin convertir al miembro en hostil. `Like` se escribe solo si otra versión de NemesisRecord lo expone.
- La línea «HACIA TI» que muestra el dossier de enRAGE se obtiene por separado de `NemesisMod.RegardOfRecord(NemesisRecord)`. Para `player_faction` su valor sigue el rango del miembro: rango 1 −5, rango 2 15, rango 3 35, rango 4 65 y rango 5 90.
- Durante el dibujo de la ficha, la extensión presenta temporalmente el registro como miembro del reparto de banda para que enRAGE muestre la sección «CON LA BANDA» y la fila «HACIA TI»; inmediatamente después restaura el flag `Story` original. No deja al registro convertido permanentemente en personaje de historia.
- Los registros guardados `extcomp_` y `extvdl_` se reconcilian directamente desde `NemesisMod.Roster`, incluso si la caché de handles de Companion todavía está vacía tras recargar. Se fija su `Gang` en `player_faction` y se guardan en el roster nativo. No se identifica ni reemplaza a nadie por modelo.
- Los IDs de Companion siguen fijos: `RecruitGangId=player_faction` y `RecruitRosterId=player_faction`.

## Cambios de v0.3.0 — administrador del campamento

### NPC administrador

- Cuando la banda está fundada y el jugador se acerca al campamento, aparece un NPC administrador usando el mismo modelo configurado para el fundador.
- Ejecuta el escenario `WORLD_HUMAN_SIT_GUITAR` y la extensión vuelve a iniciarlo si otra tarea lo interrumpe.
- Reutiliza la protección del fundador: invulnerabilidad, no seleccionable como objetivo, eventos permanentes bloqueados, atributos de combate desactivados y relación amistosa con el jugador.
- El NPC se mantiene cerca del campamento; fuera de la zona se elimina y se vuelve a crear cuando el jugador regresa. Si recargás los scripts mientras sigue presente, la extensión intenta reconectarse al handle guardado para no duplicarlo.
- Al mantener la interacción contextual aparece el menú de administración.

### Menú de la facción

El menú puede cambiar:

- **Nombre de la banda**.
- **Nombre visible del líder**, sin alterar el ID de Arthur o John.
- **Nombres visibles de los rangos nativos**.
- **Guerras territoriales** de enRAGE.
- **Rivalidades** entre facciones.
- **Eventos de vida de la banda**.
- **Respuesta a disparos**.

La pantalla lateral muestra hombres, suministros y tesorería reflejados desde los datos nativos de enRAGE. Los controles fijos también quedan visibles para identificar la facción que se está administrando.

**Límite intencional de los rangos:** esta versión permite renombrar los rangos existentes, pero conserva sus umbrales internos y plazas de enRAGE. No inventa ni agrega rangos, no cambia promociones de miembros ni crea un sistema paralelo de jerarquías, para no romper la lógica nativa de rosters.

### IDs fijos para compatibilidad

Los IDs no se pueden editar desde el menú y se fuerzan a estos valores al cargar los datos:

```ini
RecruitGangId=player_faction
RecruitRosterId=player_faction
```

No cambies esos dos valores en `EnRAGECompanionsExtension.ini`: la Companion Extension los usa para incorporar sus reclutas a la facción de este proyecto.

## Restauración del campamento al recargar

- La extensión registra `player_faction` antes de buscar su `CampSite`.
- enRAGE carga `camps.json` antes de registrar esta facción y puede descartar una fila cuyo dueño todavía no existe. Si falta en `Camps.Extra`, la extensión reconstruye el `CampSite` desde el ID y las coordenadas de `PlayerFaction.json`, lo incorpora a la lista nativa y vuelve a guardar `camps.json`.
- Cuando el jugador está a menos de 140 m y la colisión del terreno ya está cargada, la extensión limpia una posible entrada obsoleta de `CampKit` y vuelve a solicitar el montaje de los objetos.
- El marcador del campamento usa el estilo nativo `BLIP_STYLE_MP_CAMP`. Al recargar el script, se limpia el handle anterior y **no** se recrea a partir del JSON: vuelve a aparecer solo cuando enRAGE confirma que el `CampSite` nativo ya fue restaurado.
- El fundador no vuelve a aparecer cuando la banda ya está fundada.

**Importante:** los objetos físicos y el administrador aparecen cuando el jugador vuelve a acercarse. El montaje de objetos requiere que el terreno esté cargado. Conservá `PlayerFaction.json` y `camps.json` si querés mantener el sitio del campamento.

## Marcador del campamento — PROPERTY

El campamento utiliza `BLIP_STYLE_MP_CAMP` mediante `BLIP_ADD_FOR_COORDS`; no llama a `SET_BLIP_CATEGORY` ni a `SET_BLIP_SPRITE` después de crearlo.

La documentación de estilos de RDR3 identifica `BLIP_STYLE_MP_CAMP` con categoría `PROPERTY`, color `COLOR_POSSE_ALLY` y sprite `BLIP_CAMP`:

- [femga/rdr3_discoveries — Blip Styles, sección BLIP_STYLE_MP_CAMP](https://github.com/femga/rdr3_discoveries/blob/master/useful_info_from_rpfs/blip_styles/README.md#L980-L991).
- El mismo recurso describe `BLIP_STYLE_CAMP` como estilo de campamento bajo `PROPERTY`, pero con el sprite `BLIP_REGION_CARAVAN` ([referencia](https://github.com/femga/rdr3_discoveries/blob/master/useful_info_from_rpfs/blip_styles/README.md#L376-L380)). Se seleccionó `BLIP_STYLE_MP_CAMP` por su icono específico de campamento.

## Fundación y viaje al campamento

1. El fundador inicial ejecuta `WORLD_HUMAN_SIT_GUITAR`.
2. La creación solicita primero el nombre de la banda y después permite elegir una ubicación mediante un menú con nombres.
3. Las opciones son Cumberland Forest, Big Valley, Scarlett Meadows, Tall Trees, Roanoke Ridge, Rio Bravo y Grizzlies East. No hay una opción de ubicación actual, por lo que Beecher's Hope no aparece en la lista.
4. Al confirmar, hay un fundido a negro. Si enRAGE acepta el sitio, se registra el campamento y el jugador se teletransporta a sus coordenadas; luego vuelve la imagen.
5. La banda recibe una primera contribución de 10 puntos de control de territorio en el distrito del campamento. El indicador se guarda para no aplicarla repetidamente.

## Líder, roster y economía

La banda utiliza el personaje activo de la historia como líder (`vdl_arthur` o `vdl_john`) y su página nativa del roster. No crea un NPC falso como líder.

Hombres, suministros y tesorería se inicializan en cero y luego se sincronizan con los mapas nativos de enRAGE. Los valores que muestra el menú son lecturas reflejadas, no un inventario económico separado.

## Instalación

1. Compilá con Visual Studio 2022 o MSBuild, el targeting pack de .NET Framework 4.8 y el `ScriptHookRDRNetAPI.dll` de tu instalación.
2. En Developer PowerShell ejecutá:

   ```powershell
   .\build.ps1 -RDR2GameDir "D:\Red Dead Redemption 2"
   ```

3. Copiá `EnRAGEPlayerFactionExtension.dll` y `0Harmony.dll` desde `bin\Release\net48` a la carpeta `scripts`.
4. Conservá `scripts\EnRAGEPlayerFactionExtension\PlayerFaction.json`; contiene el ID y las coordenadas de la banda, los ajustes, nombres de rango y el handle temporal del administrador.
5. Dejá los valores de la Companion Extension así:

   ```ini
   RecruitGangId=player_faction
   RecruitRosterId=player_faction
   ```

El proyecto no referencia `NemesisMod.dll` al compilar. Se conecta a enRAGE mediante Reflection y Harmony y puede convivir con `EnRAGECompanionsExtension`.

## Datos guardados y copias de seguridad

`PlayerFaction.json` se guarda en `scripts\EnRAGEPlayerFactionExtension\`. Incluye el estado de la banda, alias visual del líder, coordenadas del campamento, balances reflejados, progreso de territorio, nombres de rangos y ajustes configurables. Hacé una copia de seguridad de ese archivo y de `camps.json` antes de probar.

## Verificación y límites

Este paquete contiene código fuente, no una DLL compilada. En este entorno no están disponibles MSBuild, el compilador C# ni el `ScriptHookRDRNetAPI.dll` de tu instalación. Se comprueban la estructura del ZIP, los delimitadores del código y el archivo de proyecto, pero todavía hacen falta la compilación local y una prueba dentro del juego.

---

## Historial resumido

- **v0.3.6:** mejora visual por PRESTIGIO en cinco etapas; interacción social de campamento para saludar/provocar sin hostilidad; charlas ambientales, más escenarios y defensa reactiva; limpieza de campamentos LiveCamp falsos alrededor del sitio de `player_faction`.
- **v0.1.x:** fundador, registro de la facción y líder nativo.
- **v0.2.0–v0.2.1:** alias visual, campamento nativo exterior, selector y sincronización de recursos.
- **v0.2.2–v0.2.4:** selector con nombres, restauración de territorio y correcciones de compatibilidad nativa.
- **v0.2.5–v0.2.6:** restauración de CampSite/CampKit, blip PROPERTY y creación con fundido a negro.
- **v0.3.0:** NPC administrador de guitarra, menú de ajustes de facción y nombres de rangos; IDs fijos `player_faction`.
- **v0.3.3:** los miembros existentes que no siguen al jugador vuelven al campamento; escenarios cercanos a objetos con reserva de puntos y escenarios alternativos aleatorios de pie/sentado.
- **v0.3.5:** recuperación de registros Companion entre `Roster` y `StoryRoster`; materialización de miembros ausentes usando la identidad persistente del roster; baseline de hombres para que solo los reclutas posteriores sumen fuerza; fallback de instancia durante recargas; comprobación de flags nativos de la facción.
- **v0.3.4:** limpieza/reinstalación de prompts y hooks al recargar; barra PRESTIGIO; acciones de ascenso/degradación; integración de «Hacia ti» desde `RegardOfRecord` según el rango y migración de registros Companion ya guardados a `player_faction`.


## v0.3.1 corrections

- Cumberland Forest camp coordinate no longer overlaps the O'Driscoll site at `gang_odriscollboys_cml005` (`497.3, 1498.1`), and is outside enRAGE's 450 m Fort Wallace exclusion radius. The extension also reports which native rejection rule was detected (fort perimeter, same-faction camp within 400 m, six-camp limit, or an unclassified native rejection).
- Founder and camp-administrator prompts use the actual `_UI_PROMPT_GET_GROUP_ID_FOR_TARGET_ENTITY` group for the target ped and check hold-mode completion directly by native hash, following Companion's hold-prompt flow. The manager interaction also has a direct context-control fallback.
- Added a reflection bridge to the Companion extension's released-ped set and per-handle roster cache. When a living `player_faction` roster NPC is dismissed, the extension orders that exact existing ped handle to navigate back to camp and start a camp idle scenario after arrival. It does not spawn a replacement. Companion's pending ambient-wander timer is cleared for that same handle so it cannot immediately overwrite the camp-return task. If the entity has streamed out or died, this extension will not create a clone to replace it.

La integración inicial de v0.3.1 usaba `AmbientReleasedRecruitPeds`; desde v0.3.3 el puente consulta `RosterRecordsByPedHandle` y `_latestMainInstance._comps` para cubrir también miembros vivos que ya no están en la lista activa de seguimiento. `AmbientReleasedWanderAt` se usa de forma opcional para evitar que Companion sobrescriba la ruta al campamento. Si cambian esas estructuras internas en otra versión de Companion, la extensión registra el problema y omite la integración en lugar de inferir la identidad por el modelo.


## v0.3.2 corrections

- Camp and founder blip cleanup is deferred to the first game tick and retries every 250 ms until RDR2 confirms the saved native handles no longer exist. The extension keeps the stored handles while cleanup is pending rather than clearing them prematurely.
- Removed unconditional camp-blip creation from the beginning of the founded-faction tick. The camp marker is recreated only after `FindPlayerCampSite` confirms that enRAGE has loaded the native camp; successful camp creation and explicit rediscovery paths also restore the marker.
- This prevents the marker from being immediately recreated from saved coordinates during script reload, before the native camp has loaded.


## v0.3.3 — vida de campamento para los miembros que no siguen al jugador

- Los miembros vivos de `player_faction` que Companion tiene asociados a un handle real, pero que ya no aparecen en su lista activa de seguidores, vuelven caminando al área central del campamento. Los que sí siguen al jugador se excluyen inmediatamente.
- En v0.3.3 se reutilizaba únicamente el ped exacto de `RosterRecordsByPedHandle`. Desde v0.3.5 los registros guardados sin entidad viva también pueden materializarse en el campamento bajo el mismo `RosterId`; no se selecciona otro NPC por modelo.
- Al llegar, cada NPC intenta usar un escenario del mundo próximo a los puntos que enRAGE registró para los objetos de `CampKit`. La extensión reserva los puntos seleccionados y también evita elegir otro punto si un NPC administrado está usando un escenario muy cerca de él. Así se reduce que dos miembros intenten ocupar el mismo asiento/punto.
- Para escenarios de campamento se llama al nativo de RDR2 `_TASK_USE_NEAREST_SCENARIO_TO_COORD` (`0x322BFDEA666E2B0E`), con su firma de diez argumentos. No se usa el nativo de seis argumentos de GTA V. Referencia: [RDR2 NativeDB — _TASK_USE_NEAREST_SCENARIO_TO_COORD](https://www.rdr2mods.com/nativedb/index/task/_0x322bfdea666e2b0e/).
- Si no hay un punto cercano utilizable, o todos los puntos quedan reservados/ocupados, se recorren en orden aleatorio escenarios válidos de pie y sentado: espera de pie, mirada estoica, espera impaciente, sentarse en el suelo, descansar sentado y fumar sentado. Cuando un escenario termina, el NPC vuelve a buscar un punto libre antes de entrar otra vez en la lista aleatoria. Los nombres de escenario se contrastaron con la [lista de escenarios de RDR2](https://www.rdr2mods.com/wiki/pages/list-of-rdr2-scenarios/).
- La llegada se valida a 3,8 m del destino, coherente con el radio de parada de la orden de navegación; no se marca como llegado a 8 m, lo que podía dejar al NPC en el borde del campamento.
- El administrador del campamento usa el mismo selector de escenarios del campamento. El fundador inicial conserva su guitarra.

**Límite deliberado:** solo se asignan a la vida de campamento NPCs que Companion puede asociar inequívocamente con un registro `player_faction` y un ped real existente. No se deduce identidad por el modelo, porque podría mover al NPC equivocado o duplicarlo.
