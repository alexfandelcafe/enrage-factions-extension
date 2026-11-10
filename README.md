# EnRAGE Player Faction Extension — v0.5.1

Extensión para fundar y administrar una facción propia dentro de **enRAGE** en Red Dead Redemption 2. La banda usa el sistema nativo de facciones, roster, campamentos, territorio y recursos de enRAGE; esta extensión añade el proceso de fundación, la administración, la integración con Companion y la vida cotidiana de los miembros en el campamento.

> Esta guía describe el comportamiento de la rama `feature/camp-mission-giver`. Los cambios de misión deben probarse dentro de RDR2 antes de considerarlos validados en juego.

## Misiones narrativas y encuentros (v0.5.1)

Los contratos comparten ahora un controlador de narrativa, interacción y encuentros:

- **Reconocimiento:** inspecciona dos puntos distintos para reconstruir el rastro antes de volver.
- **Patrulla:** recorre tres controles secuenciales; el rumbo y la distancia varían entre salidas.
- **Provisiones:** el alijo físico puede estar vigilado; despeja la zona y recoge el cajón con su prompt contextual.
- **Vigilancia:** exige permanecer 24 segundos dentro de un perímetro de 35 m. El reloj se pausa al salir y puede activar una emboscada.
- **Despacho:** genera un contacto físico y requiere verificarlo y entregar el documento mediante dos interacciones contextuales.
- **Convoy:** presenta escoltas y una carreta saqueable con prompt contextual; permite completar el encargo si el vehículo se destruye o desaparece.
- **Limpiar el escondite:** combate contra un grupo de 3–5 enemigos. Se desbloquea tras completar un encargo.
- **Sabotear el carro:** elimina a los escoltas y destruye un carro de suministros. Se desbloquea tras tres encargos.
- **Rescatar al explorador:** neutraliza a los captores y libera a un NPC vivo mediante un prompt contextual. Si no aparece o muere, el encargo falla sin recompensa.
- **Cazar al depredador:** enfrenta al jugador a una manada de 2–3 lobos. Se desbloquea tras tres encargos.
- **Narrativa y sonido:** hay líneas de diálogo contextual en subtítulos o notificaciones, orientación de NPC hacia el jugador, sonidos de interfaz y un evento de música de combate del propio juego, con limpieza al resolver el encuentro.
- **Interacciones v0.4.2:** la carreta del convoy usa un prompt ambiental nativo “Saquear cargamento” asociado a la entidad, en lugar de detectar G sin indicar la acción. Los resúmenes iniciales y de transición ya no se envían como avisos enRAGE cuando también hay diálogo por subtítulos. Los eventos de música se preparan antes de activarse y se reintentan si el motor no los acepta en el primer intento.

Los textos son diálogo contextual escrito por la extensión; no son doblaje original ni nuevas voces grabadas. La música y el comportamiento visual de los subtítulos deben probarse en RDR2. Este entorno permite revisar el código, pero no ejecutar una sesión de juego real.

## Índice

- [Resumen del funcionamiento](#resumen-del-funcionamiento)
- [Fundar la banda](#fundar-la-banda)
- [Líder, roster y rangos](#líder-roster-y-rangos)
- [Integración con Companion y controles](#integración-con-companion-y-controles)
- [Vida de campamento](#vida-de-campamento)
- [Administrador y menú de gestión](#administrador-y-menú-de-gestión)
- [Prestigio y aspecto del campamento](#prestigio-y-aspecto-del-campamento)
- [Territorio, recursos y opciones nativas](#territorio-recursos-y-opciones-nativas)
- [Persistencia y recuperación](#persistencia-y-recuperación)
- [Configuración](#configuración)
- [Instalación y compilación](#instalación-y-compilación)
- [Registro y diagnóstico](#registro-y-diagnóstico)
- [Límites conocidos](#límites-conocidos)

## Resumen del funcionamiento

La extensión crea una facción con identificador fijo `player_faction`. El personaje de historia activo —Arthur o John— se registra como su líder real en el roster nativo de enRAGE. No se crea un líder NPC sustituto.

Una vez fundada, la banda dispone de:

- Un campamento exterior registrado como `CampSite` nativo de enRAGE, con su marcador de mapa y sus objetos físicos `CampKit`.
- Un NPC administrador que permite cambiar el nombre, el alias del líder, los nombres de rango y varias opciones nativas de la facción.
- Integración con el roster y, cuando está instalada, con **EnRAGECompanionsExtension** para reclutas, órdenes de seguimiento, relaciones y recuperación de personajes.
- Miembros que regresan al campamento cuando dejan de seguir al jugador, buscan actividades de campamento y mantienen conversaciones ambientales entre ellos.
- Defensa reactiva del campamento frente a NPCs que estén atacando directamente al jugador, a un residente o al administrador.
- Un segundo NPC, distinto del administrador, aparece cerca del campamento y ofrece misiones propias de la extensión, sin depender de los eventos de enRAGE.
- Recuperación de datos y del campamento después de recargar los scripts.

La extensión se conecta a enRAGE mediante Reflection y Harmony. No necesita una referencia de compilación a `NemesisMod.dll`; localiza los tipos y métodos de enRAGE en tiempo de ejecución.

## Fundar la banda

### Encontrar al fundador

Antes de fundar la banda aparece un NPC fundador cerca de las coordenadas configuradas en el INI. Por defecto está cerca de Beecher's Hope. El marcador del fundador ayuda a localizarlo.

Al acercarte, aparece su prompt contextual de fundación. Al mantener la interacción:

1. Se solicita el nombre de la banda mediante el teclado del juego.
2. Se abre el selector de ubicación del campamento.
3. Al confirmar una ubicación, la pantalla se funde a negro y la extensión intenta registrar el campamento con la API nativa de enRAGE.
4. Si enRAGE acepta la ubicación, se guardan el identificador, la región y las coordenadas reales del sitio. El jugador viaja hasta ese punto y vuelve la imagen.
5. La banda recibe una contribución inicial de **10 puntos de control territorial** en el distrito del campamento. La operación se marca como realizada para no repetirla en cada recarga.

Las ubicaciones disponibles son:

| Ubicación | Zona |
|---|---|
| Cumberland Forest | Bosque al norte de Valentine |
| Big Valley | Praderas al oeste de Strawberry |
| Scarlett Meadows | Campo abierto de Lemoyne |
| Tall Trees | Bosque al oeste de Blackwater |
| Roanoke Ridge | Bosque rocoso del noreste |
| Rio Bravo | Desierto de New Austin |
| Grizzlies East | Montañas del este de Ambarino |

No existe una opción de «ubicación actual». Beecher's Hope se reserva como zona del fundador y no se ofrece como ubicación del nuevo campamento.

### Validaciones de ubicación

La creación depende de las reglas nativas de enRAGE. Entre las condiciones que pueden provocar un rechazo están:

- El perímetro de exclusión de Fort Wallace.
- Otro campamento de la misma facción a menos de 400 metros.
- El límite nativo de seis campamentos para una facción.
- Otras validaciones internas de enRAGE.

Cuando se rechaza un sitio, la extensión informa del motivo detectado y permite elegir otra ubicación. No da por fundada la banda si el registro del campamento falla.

### Facción ya existente sin campamento

Si la facción ya está fundada pero no tiene un `CampSite` registrado, la ficha del líder en el roster ofrece la acción **Elegir campamento**. El selector permite colocar el campamento sin volver a fundar la banda.

## Líder, roster y rangos

### Identidad del líder

El líder es el personaje de historia activo:

- `vdl_arthur` para Arthur.
- `vdl_john` para John.

La extensión mantiene el ID nativo del personaje y utiliza la página de líder que ya proporciona enRAGE. Desde esa ficha se puede cambiar el nombre visible del líder; esto modifica su alias, no su identidad ni su ID de historia.

### Miembros de la banda

Los miembros se administran desde el roster nativo de enRAGE. La extensión no crea un segundo roster separado. Los datos de cada registro —ID, rango, estado y pertenencia a la banda— siguen siendo los datos que usa enRAGE.

Para los miembros vivos de `player_faction`, la ficha muestra la sección de miembro de banda y el valor **HACIA TI**. La extensión cambia temporalmente el modo de presentación mientras se dibuja la ficha y restaura después el indicador original del registro; no convierte permanentemente a esos personajes en personajes de historia.

### Ascender y degradar

Al seleccionar un miembro vivo de la facción en el roster aparecen las acciones que correspondan a su rango:

- **Ascender rango**, si todavía no alcanzó el rango 5.
- **Degradar rango**, si está por encima del rango 1.

El ascenso utiliza `NemesisMod.Promote(NemesisRecord)` cuando está disponible. La degradación reduce un nivel y reinicia la XP de rango. Los límites son los cinco rangos nativos de enRAGE; no se crean niveles nuevos ni se modifican los umbrales internos.

Después del cambio se guarda el roster y se vuelve a aplicar la relación que Companion utiliza para el miembro.

### Relación y valor «HACIA TI»

La extensión sincroniza la afinidad de Companion de acuerdo con el rango: el rango 1 usa disposición neutral y los rangos 2 a 5 usan disposición amistosa. No convierte a los miembros en enemigos del jugador.

El valor que muestra enRAGE en **HACIA TI** sigue esta escala base:

| Rango | Valor base |
|---:|---:|
| 1 | -5 |
| 2 | 15 |
| 3 | 35 |
| 4 | 65 |
| 5 | 90 |

La extensión proporciona ese valor a través de `RegardOfRecord`; no sustituye el sistema general de relaciones de las demás facciones.

## Integración con Companion y controles

La integración completa con **EnRAGECompanionsExtension** permite que los reclutas de Companion pertenezcan a la facción del jugador y que sus órdenes y datos sigan vinculados al mismo registro del roster.

### Identificadores fijos

Los dos identificadores de reclutamiento deben ser:

```ini
RecruitGangId=player_faction
RecruitRosterId=player_faction
```

La extensión fuerza esos valores cuando detecta la integración de Companion. No deben cambiarse por otros IDs en el INI de Companion.

Cuando `EnRAGECompanionsExtension.ini` tiene `RecruitRosterId=player_faction`, la extensión también bloquea el reclutamiento genérico de Companion hasta que la facción esté fundada y registrada en enRAGE: no aparece «Cabalga conmigo» antes de ese momento y el método de reclutamiento tiene una protección adicional. La comprobación se hace desde esta extensión; no requiere modificar el repositorio de Companion.

Los registros creados por Companion con prefijos `extcomp_` y `extvdl_` se reconcilian con el roster normal de enRAGE. La extensión revisa tanto `NemesisMod.Roster` como `StoryRoster`, mueve los registros aplicables a la facción `player_faction` y guarda el roster. No intenta reconocer miembros buscando NPCs que compartan el mismo modelo.

### Interacción social nativa: R/F

**Los miembros de la facción usan la interacción social nativa de RDR2.** Al apuntar/enfocar a un miembro y usar los controles nativos de saludo o provocación —R/F con la configuración de teclado habitual— se ejecuta la interacción del juego.

La extensión ya no muestra el menú contextual propio **«Hablar»** para los miembros. Tampoco reemplaza el sistema social nativo por una acción de diálogo con G.

Para que el sistema nativo siga disponible después de reclutar o recargar scripts, la extensión:

- Mantiene a los miembros seleccionables como objetivo y habilita las banderas de ped que necesita la interacción social.
- Restaura la configuración después de que Companion aplique sus perfiles de reclutado liberado o despedido.
- Permite que se ejecute el ciclo social original de enRAGE/RDR2 para los miembros de la facción.
- Oculta únicamente el prompt social adicional de enRAGE después de su actualización.

### Orden de seguimiento: G

La orden independiente de Companion para llamar al miembro o hacer que siga al jugador se conserva. La extensión no bloquea el prompt genérico de seguimiento ni elimina la acción de G.

En otras palabras:

- **R/F:** saludo y provocación nativos de RDR2.
- **G:** orden de seguimiento/llamada de Companion.
- **«Hablar» contextual propio de la extensión:** eliminado para los miembros.

Las teclas efectivas dependen de la configuración de controles del juego y de Companion.

## Vida de campamento

### Encargado de misiones independientes

Cuando la facción tiene un campamento nativo válido, aparece un NPC nuevo cerca del administrador. El prompt **Pedir una misión** asigna uno de diez contratos. La selección es ponderada, evita repetir el último contrato completado, desbloquea los nuevos tipos gradualmente y aumenta la dificultad cada cierto número de misiones.

| Encargo | Objetivo | Recompensa base |
|---|---|---|
| **Reconocimiento de ruta** | Inspeccionar dos puntos y regresar. | +5 prestigio, +5 suministros y $25. |
| **Patrulla de frontera** | Recorrer tres puntos en orden y regresar. | +8 prestigio, +8 suministros y $35. |
| **Recuperar provisiones** | Despejar el alijo, recoger el cajón y regresar. | +9 prestigio, +12 suministros y $20. |
| **Vigilar el cruce** | Permanecer en la zona 24 segundos y despejar la emboscada si aparece. | +8 prestigio, +7 suministros y $34. |
| **Entregar el despacho** | Verificar al contacto, entregar el documento y regresar. | +8 prestigio, +6 suministros y $48. |
| **Interceptar un convoy** | Eliminar a 2–4 escoltas y saquear la carreta. | +11 prestigio, +13 suministros y $65. |
| **Limpiar el escondite** | Eliminar a 3–5 enemigos en un refugio. | +10 prestigio, +9 suministros y $42. |
| **Sabotear el carro** | Eliminar a los escoltas y destruir el vehículo. | +11 prestigio, +7 suministros y $68. |
| **Rescatar al explorador** | Eliminar a los captores y liberar al prisionero. | +13 prestigio, +10 suministros y $38. |
| **Cazar al depredador** | Abatir una manada de 2–3 lobos. | +10 prestigio, +7 suministros y $52. |

La dificultad tiene tres niveles (**baja, media y alta**) y multiplica las recompensas base en un 30 % por nivel. La cantidad de enemigos crece con la dificultad; también aumentan su precisión y munición. Los contratos de convoy y sabotaje incluyen un vehículo físico. En el asalto al convoy se genera una carreta con escoltas hostiles cerca del objetivo; el número de enemigos, su precisión y la munición aumentan con la dificultad. La escena solo se crea cerca del jugador, y los handles guardados se validan por modelo y posición antes de reutilizarse o limpiarse.

El alijo y la carreta del convoy utilizan prompts contextuales nativos asociados a sus entidades. El despacho requiere dos interacciones con el contacto. El rescate tiene un prompt específico para liberar al explorador; el sabotaje exige destruir el carro después de eliminar a los escoltas.\n\nEl catálogo de contratos, el controlador de entidades del objetivo y el encargado del campamento están separados en clases propias. Los objetivos usan el sprite **blip_objective**, en lugar de un icono de punto de interés. El progreso y la dificultad activa se guardan en PlayerFaction.json; el sistema sigue usando marcadores propios y no consume eventos de WorldEvent ni EncounterKind de enRAGE.

### Aislamiento de la facción del jugador

La facción `player_faction` no participa en el ciclo nativo de tesorerías, escondites y cachés de suministros de enRAGE; sus recursos se administran mediante la extensión. Después de `LearnCampSites`, la extensión limpia candidatos y campamentos falsos cercanos al campamento propio.

La sincronización con Companion solo migra registros `extcomp_*` / `extvdl_*` que aparecen en su caché exacta de registros por handle. El prefijo del ID por sí solo no prueba que un NPC haya sido reclutado. Los registros no confirmados no se materializan como miembros de la vida del campamento.


La vida de campamento se aplica a miembros vivos de `player_faction` que Companion puede vincular inequívocamente con un registro y un ped real. Los miembros que están siguiendo activamente al jugador no reciben las tareas de regreso ni las rutinas de ocio; cuando dejan de seguirlo, vuelven a entrar en el ciclo de campamento.

### Regreso al campamento

- La extensión reutiliza el handle exacto del NPC asociado al registro del roster.
- Si el personaje está montado, espera a que Companion complete el desmontaje antes de iniciar la navegación.
- El miembro camina hacia una posición libre alrededor del centro del campamento.
- Se considera que llegó al aproximarse a unos 3,8 metros del destino.
- Si una tarea posterior lo aleja más de 18 metros del centro cuando ya estaba instalado, se le vuelve a ordenar regresar.

No se identifica a un miembro por su modelo ni se sustituye por otro NPC aleatorio.

### Recuperación de miembros que no tienen un ped vivo

Los registros guardados del roster pueden sobrevivir a una sesión aunque las entidades del mundo no. Cuando el campamento está cargado y el jugador se encuentra dentro del área de trabajo, la extensión puede volver a materializar hasta **tres registros faltantes por escaneo** usando el `ModelHash` guardado en el registro.

Se conserva el mismo `RosterId`. Cuando Companion guardó el atuendo del recluta, la integración vuelve a aplicarlo al ped materializado y registra el nuevo handle en su caché. Si el handle exacto sigue existiendo, se reutiliza en lugar de crear otro representante.

Tras cerrar completamente el juego, los handles temporales se descartan porque RDR2 puede reutilizarlos para otras entidades. La identidad persistente del miembro permanece en el roster y permite recuperar su presencia física cuando el campamento vuelve a cargarse.

### Escenarios y actividades

Al llegar, cada miembro intenta usar un punto de escenario cercano a un objeto del `CampKit`: asientos, camas u otros puntos que enRAGE haya registrado para el campamento. Los puntos elegidos se reservan y se comprueba si otro miembro o el administrador ya está usando un punto cercano. Esto reduce que varios NPCs se acumulen en el mismo lugar.

Si no hay un punto utilizable, se intenta una actividad alternativa de pie o sentado, pero la lista de reserva se limita a escenarios que pueden iniciarse con seguridad en la posición actual del NPC. No se asigna pesca lejos del agua ni escenarios que requieren una silla, banco, repisa o punto de fogata concreto. Las sillas y los bancos se intentan mediante los puntos de escenario nativos próximos a los objetos del `CampKit`; si no hay un punto válido, el NPC usa una alternativa sencilla de pie o sentado en el suelo.

Cuando un escenario termina, el miembro busca de nuevo un punto libre. Si un punto no puede utilizarse, se prueba otro en lugar de insistir indefinidamente en el mismo.

### Conversaciones ambientales

Cuando hay al menos dos miembros cerca del centro del campamento, la extensión puede iniciar intercambios de voz ambientales entre ellos. Se seleccionan saludos o frases genéricas de conversación y, tras una breve pausa, una segunda persona puede responder.

Se usa el nativo de voz ambiental de RDR2 con el banco de voz del propio ped, sin imponer un banco de voz fijo a todos los reclutas. La reproducción depende de las líneas y bancos de voz que el juego tenga disponibles para cada personaje; si una frase no está disponible, el resto del sistema de campamento continúa funcionando.

### Defensa reactiva

La defensa no se activa por el simple hecho de que un NPC pase cerca del campamento o lleve un arma. La extensión busca un agresor que esté combatiendo directamente al jugador, a un residente de la banda o al administrador.

Cuando detecta ese combate:

1. Los miembros residentes que están en el área del campamento pueden interrumpir su escenario.
2. Se les ordena combatir contra el agresor identificado.
3. Se conserva ese objetivo mientras continúe la amenaza.
4. Cuando el agresor deja de combatir al jugador o a los miembros, se espera un breve margen y los defensores vuelven a las rutinas de campamento.

La defensa de residentes es distinta de la opción nativa **Responder a disparos** de enRAGE, que controla otros comportamientos de patrullas y eventos.

## Administrador y menú de gestión

Cuando el jugador está cerca del campamento aparece un NPC administrador usando el modelo configurado para el fundador. Se mantiene cerca del sitio y desaparece cuando el jugador se aleja lo suficiente; al regresar, se vuelve a crear. Si se recargan los scripts durante la misma sesión, la extensión intenta reconectar con el handle del administrador antes de crear otro.

El administrador:

- Es un NPC de servicio del campamento, no un recluta.
- Tiene protección propia: no se ofrece como objetivo normal y no se le aplican las rutinas sociales de los miembros.
- Utiliza el mismo sistema de selección de escenarios del campamento. La guitarra es una actividad posible, no una tarea permanente obligatoria.
- No puede incorporarse como recluta genérico de Companion.

Al acercarte y mantener su prompt contextual se abre **ADMINISTRACIÓN DE LA BANDA**. El menú usa arriba/abajo para moverse, aceptar para elegir y atrás para volver o cerrar.

### Opciones disponibles

| Opción | Funcionamiento |
|---|---|
| Renombrar banda | Cambia el nombre visible de la facción; conserva el ID `player_faction`. |
| Nombre visible del líder | Cambia el alias mostrado; Arthur/John conserva su ID nativo. |
| Administrar rangos | Permite renombrar los cinco rangos existentes. |
| Guerras territoriales | Activa o desactiva el permiso nativo `TurfWars`. |
| Rivalidades entre bandas | Activa o desactiva `Feuds`. |
| Eventos de banda | Activa o desactiva `GangLifeEvents`. |
| Responder a disparos | Activa o desactiva `AnswersGunfire`. |

La pantalla lateral muestra hombres, suministros y tesorería sincronizados con enRAGE, además de los identificadores fijos de la facción y el roster.

**Los nombres de rango son etiquetas visuales.** La extensión no cambia los umbrales de XP, las plazas ni la estructura interna de los cinco rangos de enRAGE.

## Prestigio y aspecto del campamento

La barra de la facción muestra **PRESTIGIO** en lugar de **NOTORIEDAD**. Utiliza el valor nativo de enRAGE en la escala de 0 a 100; no reemplaza ni recalcula el sistema de notoriedad.

| Valor | Estado mostrado |
|---:|---|
| 0–19 | DESCONOCIDA |
| 20–39 | EMERGENTE |
| 40–59 | RECONOCIDA |
| 60–79 | TEMIDA |
| 80–100 | LEGENDARIA |

El mismo valor selecciona una de cinco configuraciones visuales de `CampKit.CampLooks["player_faction"]`. A medida que aumenta el prestigio, el campamento cambia de una parada rudimentaria a un asentamiento más organizado. Cada objeto usa una clave de CampKit exclusiva y un modelo deliberado; se evita reutilizar la misma clave con varios ejemplares, que era lo que producía sillas, faroles y cajas visualmente idénticos. Los niveles amplían el número y la variedad de tiendas, camas, asientos, iluminación y reservas, en lugar de multiplicar un único modelo.

La configuración visual se actualiza al cambiar de etapa y ahora cada pieza tiene su propio identificador y modelo. La reconstrucción física se difiere hasta que el jugador esté cerca y la colisión del terreno esté cargada; no se desmonta el campamento a distancia solo para cambiar su aspecto. La extensión también corrige la orientación de las piezas de CampKit que deben mirar hacia el centro: las sillas, los taburetes, los bancos y las tiendas se giran hacia la fogata. La corrección es idempotente, por lo que las comprobaciones posteriores no las vuelven a invertir.

## Territorio, recursos y opciones nativas

### Territorio

Al fundar el campamento se intenta añadir un primer apoyo de 10 puntos de control al distrito donde se encuentra. Se usa el sistema nativo de territorio de enRAGE y se guarda un indicador para no volver a aplicar esa contribución después de cada recarga.

### Hombres, suministros y tesorería

La extensión utiliza los mapas nativos de enRAGE para hombres, suministros y tesorería. Al inicializar una banda nueva, los tres valores empiezan en cero. La pantalla de administración muestra esos valores nativos; no mantiene una economía paralela.

La fuerza se sincroniza a partir de los IDs del roster:

- Los miembros que ya estaban guardados cuando se funda la banda se registran como línea base y no inflan la fuerza inicial.
- Los nuevos IDs incorporados después de la fundación aportan el valor de fuerza nativo correspondiente a su rango (`NemesisMod.NamedWorth(rank)`).
- Los ascensos y degradaciones actualizan la contribución de ese registro.
- Las incorporaciones ya contabilizadas no vuelven a sumarse en cada escaneo.

Los suministros y la tesorería siguen siendo los valores gestionados por enRAGE y sus sistemas nativos.

### Opciones de facción

Las opciones del administrador modifican y verifican directamente los campos nativos de `Gang`:

- **TurfWars:** permite que enRAGE considere a la banda para guerras territoriales. La otra facción participante también debe tenerlas habilitadas y deben cumplirse las condiciones de control del distrito.
- **Feuds:** permite que enRAGE considere a la banda para sus sistemas de rivalidad. Activarlo no declara una guerra de inmediato.
- **GangLifeEvents:** habilita eventos cuyo ámbito sea una banda, sujetos a las condiciones y probabilidades del sistema nativo.
- **AnswersGunfire:** habilita las respuestas nativas de patrullas/eventos ante disparos cercanos. No obliga a todos los residentes del campamento a atacar cada vez que oyen un disparo.

Los valores iniciales de una banda nueva son: guerras territoriales activadas; rivalidades, eventos de banda y respuesta nativa a disparos desactivados. Se pueden cambiar desde el administrador.

### Evitar que la banda sea tratada como un campamento enemigo

La extensión marca como entidades propias de enRAGE los handles exactos de los miembros conocidos. También evita que los registros de `player_faction` o el `CampSite` propio se asignen o revelen como un campamento enemigo.

Esto incluye bloquear las rutas nativas de aparición/revelación de campamentos para los miembros propios y limpiar referencias obsoletas a campamentos hostiles creados por escaneos anteriores cerca del sitio de la banda. La limpieza se limita a los datos erróneos de la propia facción; los campamentos válidos de otras zonas y las relaciones o guerras con facciones externas siguen perteneciendo a los sistemas de enRAGE.

## Persistencia y recuperación

La extensión guarda sus datos en:

```text
scripts/
└── EnRAGEPlayerFactionExtension/
    ├── PlayerFaction.json
    └── extension.log
```

`PlayerFaction.json` conserva, entre otros datos:

- Estado de fundación, nombre e identificadores.
- Nombre visible del líder.
- ID, región y coordenadas del campamento.
- Ajustes de facción y etiquetas personalizadas de los rangos.
- Estado de inicialización de recursos, contabilidad de fuerza y apoyo territorial.
- Etapa visual de prestigio y datos auxiliares de recuperación.

El archivo `camps.json` pertenece al sistema de campamentos de enRAGE y también debe conservarse. Si enRAGE carga ese archivo antes de registrar `player_faction` y descarta la fila por no conocer aún la facción, la extensión puede reconstruir el `CampSite` usando el ID y las coordenadas guardadas en `PlayerFaction.json`.

### Qué ocurre al recargar scripts

- Se limpian los prompts y marcadores nativos antiguos antes de recrearlos.
- Se desinstalan los hooks Harmony pertenecientes a esta extensión y se vuelven a instalar al reconectar con enRAGE.
- La extensión vuelve a registrar la facción y busca su `CampSite`.
- Cuando el sitio está restaurado, vuelve a crear el marcador del campamento.
- Al acercarse a menos de 140 metros, y cuando la colisión está lista, restaura los objetos físicos del `CampKit`.
- Verifica que la caché de enRAGE corresponda a objetos físicos vivos; si el campamento figura como creado pero sus objetos desaparecieron o el montaje quedó vacío, fuerza una reconstrucción y vuelve a intentarlo sin recargar scripts.
- Limpia la caché interna de piezas cuyo modelo falló temporalmente para permitir un nuevo intento de creación.
- Se intenta recuperar el administrador existente y se reconstruye la asociación de miembros con Companion.

Los handles de NPC solo son referencias temporales válidas durante el mismo proceso del juego. Al iniciar un proceso nuevo de RDR2 se descartan esos handles para evitar enlazar por error una entidad distinta; los IDs persistentes del roster no se descartan.

**Hacé copias de seguridad de `PlayerFaction.json` y del `camps.json` de enRAGE antes de probar cambios importantes o versiones nuevas.**

## Configuración

Al iniciar, la extensión crea `scripts/EnRAGEPlayerFactionExtension.ini` si todavía no existe. También se distribuye `EnRAGEPlayerFactionExtension.ini.example` como plantilla.

| Clave | Valor predeterminado | Función |
|---|---:|---|
| `FounderX` | `-1641.797` | Coordenada X del fundador. |
| `FounderY` | `-1399.678` | Coordenada Y del fundador. |
| `FounderZ` | `82.804` | Coordenada Z del fundador. |
| `FounderHeading` | `90.0` | Orientación inicial del fundador. |
| `FounderModel` | `U_M_M_Rwfrancher_01` | Modelo del fundador y del administrador del campamento. |
| `SpawnDistance` | `300.0` | Distancia máxima a la que se crea el fundador; se limita al intervalo 300–600. |
| `InteractionDistance` | `3.0` | Radio base del grupo contextual y de la protección que desactiva ataques cuerpo a cuerpo cerca del fundador/administrador; se limita al intervalo 1,5–5. |
| `FounderPrompt` | `Fundar una banda` | Texto del prompt del fundador. |

Los IDs `RecruitGangId` y `RecruitRosterId` no pertenecen a este INI. Se configuran en `EnRAGECompanionsExtension.ini` y deben mantenerse como `player_faction`.

## Instalación y compilación

### Requisitos

- Red Dead Redemption 2.
- enRAGE instalado y funcionando.
- ScriptHookRDR2DotNet V2 y el `ScriptHookRDRNetAPI.dll` correspondiente a esa instalación.
- Visual Studio 2022 o MSBuild, con el targeting pack de .NET Framework 4.8, para compilar.
- EnRAGECompanionsExtension para la integración de reclutamiento, seguimiento y recuperación completa del roster físico.

### Compilar

Desde Developer PowerShell de Visual Studio, en la carpeta del repositorio:

```powershell
.\build.ps1 -RDR2GameDir "D:\Red Dead Redemption 2"
```

El proyecto apunta a .NET Framework 4.8 y x64. El script comprueba que exista `ScriptHookRDRNetAPI.dll` en el directorio del juego y ejecuta Restore/Build con MSBuild.

### Instalar

1. Copiá `EnRAGEPlayerFactionExtension.dll` y `0Harmony.dll` desde `bin\Release\net48` a la carpeta `scripts` del juego.
2. Copiá `EnRAGEPlayerFactionExtension.ini.example` como `EnRAGEPlayerFactionExtension.ini` si querés definir la posición/modelo del fundador antes del primer inicio. Si no lo hacés, la extensión crea el INI con los valores predeterminados.
3. En `EnRAGECompanionsExtension.ini`, dejá:

   ```ini
   RecruitGangId=player_faction
   RecruitRosterId=player_faction
   ```

4. Conservá `scripts/EnRAGEPlayerFactionExtension/PlayerFaction.json` y el archivo de campamentos de enRAGE. No borres esos datos al actualizar la DLL si querés mantener tu banda y su ubicación.

El proyecto no referencia `NemesisMod.dll` durante la compilación. La conexión con enRAGE y sus métodos internos se realiza en ejecución mediante Reflection y Harmony.

## Registro y diagnóstico

El registro se guarda en:

```text
scripts/EnRAGEPlayerFactionExtension/extension.log
```

Se registran, entre otros eventos, la conexión con enRAGE/Companion, la instalación de hooks, el registro de la facción, la restauración del campamento, la recuperación de miembros, las tareas de escenarios y los errores de sincronización.

Ante un problema:

1. Comprobá que enRAGE y ScriptHook estén cargados correctamente.
2. Confirmá que los IDs de Companion sean exactamente `player_faction`.
3. Revisá que `PlayerFaction.json` y el archivo de campamentos de enRAGE existan y sean válidos.
4. Revisá las últimas líneas de `extension.log` y buscá mensajes de conexión, restauración o integración pendientes.

## Límites conocidos

- Los cinco rangos y sus umbrales siguen siendo los nativos de enRAGE; solo se pueden cambiar sus nombres visibles.
- Las guerras, rivalidades, eventos de banda y respuestas nativas a disparos siguen dependiendo de las condiciones y probabilidades de enRAGE. Activar una opción no fuerza un evento.
- La vida de campamento requiere una asociación inequívoca entre el registro del roster y el ped. No se elige un NPC por coincidencia de modelo.
- La materialización de miembros persistidos requiere los datos del roster y la integración de Companion; solo se realiza cuando el campamento está cargado.
- Las conversaciones ambientales dependen de las líneas y bancos de voz disponibles para cada ped.
- La configuración física del campamento se reconstruye cuando el jugador se acerca y la colisión del terreno está cargada.

---

## Historial resumido

- **v0.3.6:** aspecto del campamento por prestigio en cinco etapas; vida de campamento con escenarios y conversaciones ambientales; defensa reactiva; sincronización de roster y recursos; prevención de falsos campamentos enemigos.
- **v0.3.5:** recuperación de registros Companion, materialización de miembros faltantes por ID persistente, línea base de fuerza inicial y ajustes nativos de facción.
- **v0.3.4:** recuperación tras recargar scripts, barra PRESTIGIO, acciones de ascenso/degradación y relación «HACIA TI» por rango.
- **v0.3.3:** regreso al campamento de los miembros que dejan de seguir al jugador y selector de escenarios con reserva de puntos.
- **v0.3.0:** administrador del campamento, menú de gestión e identificadores fijos `player_faction`.
- **v0.1.x–v0.2.x:** fundación de la facción, líder nativo, selector de campamento, restauración de `CampSite`/`CampKit`, marcador y sincronización inicial.
