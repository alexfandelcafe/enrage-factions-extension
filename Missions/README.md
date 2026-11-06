# Misiones editables

Al iniciar la extensión, se crea la carpeta:

```text
Red Dead Redemption 2/scripts/EnRAGEPlayerFactionExtension/Missions/
```

La primera vez se copia `defaults.json` a esa carpeta. **No se sobrescriben archivos ya existentes**, para conservar tus modificaciones.

## Añadir una misión

Crea un archivo `.json` nuevo en esa carpeta. Puedes poner una sola misión como objeto JSON o varias dentro de `{"missions":[...]}`. No hace falta modificar ni recompilar la DLL. El catálogo vuelve a cargarse cuando le pides el siguiente contrato y no hay una misión activa; si no ves los cambios, reinicia el juego.

Cada misión necesita un `id` único, `title`, `objectiveType` y recompensas. Ejemplo mínimo:

```json
{
  "id": "my-first-contract",
  "title": "Rastrear a los cazadores",
  "startInstruction": "Sigue los rastros y vuelve con un informe.",
  "objectiveType": "ReachLocation",
  "suppliesReward": 4,
  "treasuryReward": 35,
  "prestigeReward": 6,
  "weight": 2,
  "waypointCount": 2,
  "targetDistance": 280,
  "objectiveRadius": 24,
  "briefing": [
    "ENCARGADO: Hay desconocidos en la ruta norte.",
    "ENCARGADO: Examina ambos puntos y no dejes que te sigan."
  ],
  "checkpointLines": [
    "NOTA: Has localizado un rastro. Comprueba el siguiente punto."
  ],
  "returnLines": [
    "ENCARGADO: El informe nos ayudará a proteger el camino."
  ],
  "completionLine": "Buen trabajo. La banda ya conoce esa ruta."
}
```

## Tipos de objetivo compatibles

- `ReachLocation`: visitar uno o varios puntos.
- `PatrolRoute`: recorrer una ruta de puntos en orden.
- `RecoverSupplies`: encontrar un alijo, despejar la zona si hay guardias y recogerlo mediante el prompt contextual. Si el objeto falla en tres intentos al generarse, el contrato se cierra de forma segura.
- `HoldArea`: mantener la posición durante un tiempo; puede aparecer una emboscada.
- `DeliverDispatch`: hablar con un contacto y completar la entrega mediante prompts contextuales. Si el contacto falla tres veces al generarse, se intenta un punto alternativo con un prompt propio; si también falla, se cierra el contrato para evitar un bloqueo.
- `InterceptConvoy`: derrotar a los escoltas y saquear la carreta mediante el prompt contextual.

La extensión admite archivos de misión adicionales sin una cantidad máxima práctica. Los tipos de objetivo son comportamientos del motor ya implementados: un JSON cambia el contenido, parámetros, diálogos y recompensas, pero no puede inventar un tipo de mecánica completamente nuevo sin ampliar el código.

## Campos opcionales

- `waypointCount`: cantidad de puntos para `ReachLocation` o `PatrolRoute`.
- `targetDistance`: distancia aproximada al campamento para elegir el objetivo.
- `objectiveRadius`: radio de llegada al punto.
- `interactionRadius`: radio de interacción; para convoy, define el alcance del prompt.
- `areaRadius`, `durationSeconds`, `encounterAtSeconds`: parámetros de `HoldArea`.
- `spawnEncounter`: activa (`true`) o desactiva (`false`) el encuentro hostil de una misión `HoldArea`.
- `promptLabel`: texto del prompt para recoger provisiones o saquear el convoy.
- `firstPromptLabel`, `secondPromptLabel`: textos de los prompts de conversación y entrega.
- `briefing`, `checkpointLines`, `encounterLines`, `lootLines`, `contactLines`, `handoverLines`, `returnLines`, `completionLine`: diálogos editables del encargo, incluido el saqueo y el intercambio con el contacto.
- `weight`: frecuencia relativa de selección. Los contratos recientes se excluyen de la selección inmediata si hay otras misiones disponibles.

Los nombres de los campos son sensibles al formato JSON: usa comillas dobles y separa cada propiedad con comas. Los valores de recompensa se escalan un 30 % por nivel de dificultad, igual que los contratos originales.

## Editar o desactivar contratos incluidos

Edita `defaults.json` en la carpeta del juego para cambiar los seis contratos iniciales. Puedes desactivar uno quitándolo del arreglo `missions`, o cambiar su `weight` a `0` para impedir que se seleccione. Los archivos adicionales se cargan después de `defaults.json` y pueden sobrescribir una misión predeterminada si reutilizan exactamente el mismo `id`. Guarda una copia de seguridad antes de actualizar la extensión.
