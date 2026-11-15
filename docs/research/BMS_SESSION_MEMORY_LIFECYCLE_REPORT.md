# Ciclo de vida y acumulación de memoria entre partidas BMS

## Reproducción

El selector y las partidas se ejecutaron dentro de un mismo cliente aislado por tanda y con renderizado real. La escena importa el corpus con `BmsFileImporter`, abre `BmsSoloSongSelect` y usa el inicio de canción que crea el `PlayerLoader` habitual. Verifica el hash de la partida y un frame procesado por el reproductor del framework o una textura de frame creada por el backend suplementario. A partir de ese punto corre seis segundos de reloj del chart a 4× y regresa al selector. Se eligieron doce charts y doce vídeos distintos, comprobados por SHA-256, en dos fixtures públicos y diez carpetas locales distintas. El inventario íntegro se conserva localmente. [Corpus resumido](evidence/continuous-corpus.csv)

La escena toma muestras antes de entrar, en el cargador, al entrar al jugador, en el primer frame BGA observado, en actividad, al salir, en el selector, al terminar la disposición de ambos screens y a partir de 750 ms después de esa disposición. Usa `PrivateMemorySize64`, Working Set, `GC.GetTotalMemory(false)`, tamaño y compromiso del último GC, colecciones, número de hilos y referencias débiles. La estimación administrada puede incluir objetos todavía sin recoger. Las referencias débiles de los jugadores y los displays sirven para verificar elegibilidad para GC, aunque no cuantifican la memoria retenida por esos objetos.

El primer frame observado es un hito del pipeline CPU. El contador de frames, y la textura asignada en el backend suplementario, no verifican la presentación de ese frame. La comprobación de fin de disposición lee `Drawable.IsDisposed` del framework fijado; no bloquea la cola ni presupone que terminen BASS, la GPU o todas las tareas globales. El ensayo mudo a 4× solo sirve para reproducir presión y retención.

```powershell
$env:BMS_SESSION_MEMORY_MANIFEST = '<archivo TSV, 12 filas ID, ruta del chart y SHA-256>'
$env:BMS_SESSION_MEMORY_REPORT = '<directorio nuevo>/session.jsonl'
dotnet "<DLL de tests Release>" --session-memory-diagnostic
```

Utilizar un directorio de trabajo y un `LOCALAPPDATA`/TEMP aislados por corrida, y mantener el mismo orden del TSV. Para el diagnóstico separado, establezca `BMS_SESSION_MEMORY_REACHABILITY=1`; tras las doce salidas espera dos GC completos con finalizadores entre ambos. `BMS_SESSION_MEMORY_HOLD=1` deja ~30 s para una captura externa únicamente en ese modo. Los archivos crudos quedan fuera de Git.

## Resultado de las tandas

Mediciones en Veldrid/Direct3D11. La primera tanda distinta se detuvo en el cargador del cuarto mapa; quedan sus tres ciclos completos y evidencia del timeout. La corrida piloto de dos familias de recursos también se conserva como calibración, separada de las rondas con doce vídeos. Se añadió registro de foco y una solicitud de foco al iniciar si la ventana estaba inactiva. Las tres tandas completas registraron el foco activo durante las cargas observadas. En B2 y en alcanzabilidad se copió el directorio de ejecución original y se sustituyó solamente la asamblea de pruebas: hashes idénticos del ruleset productivo, osu.Game y osu.Framework. [Verificación de binarios](evidence/session-runtime-hashes.csv)

| Tanda | Salidas | Privada inicial (MiB) | Estable tras 1 (MiB) | Estable tras 12 (MiB) | Pico muestreado (MiB) | Pendiente ciclos 4–12 (MiB/ciclo) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| B1 | 12 | 451,98 | 778,19 | 2468,60 | 2500,18 | +113,86 |
| B2 | 12 | 436,68 | 758,18 | 2465,86 | 2513,12 | +129,76 |
| Alcanzabilidad, tramo natural | 12 | 450,58 | 782,69 | 2390,61 | 2485,56 | +105,62 |

La pendiente es la regresión lineal de los puntos estabilizados de los ciclos 4–12; el orden y la duración de assets condicionan el número. La serie contiene mesetas y caídas puntuales. Entre la primera y última salida el aumento estabilizado fue 1608–1708 MiB. El pico es el máximo muestreado entre fases, sin sondeo continuo. Esta prueba reproduce crecimiento hasta alrededor de 2,4 GiB en doce salidas; la sesión de aproximadamente 10,3 GB observada por el usuario tiene corpus y duración diferentes. [Serie abreviada](evidence/continuous-summary.csv), [Resumen](evidence/continuous-summary.json).

En B1/B2, desde la muestra del cargador hasta gameplay listo transcurrieron 2621–2827 ms; desde gameplay hasta el primer frame observado, 401–2637 ms. La muestra de selector cayó 397–405 ms después de pedir salida, y la de fin de disposición de jugador y cargador a 800–813 ms. Las duraciones incluyen la cadencia de pasos del harness. En las muestras estabilizadas se observaron 84–114 hilos, entre 6 y 14 GC de generación 2 acumulados y un Player más por salida. La memoria privada fue creciente en conjunto incluso después de haberse dispuesto ambos screens por cada mapa.

Tras dos GC completos con finalizadores, seguían alcanzables **los doce Player dispuestos y 24 BmsBgaDisplay**. La estimación administrada bajó aproximadamente 76 MiB entre las muestras de control y la privada aumentó 27 MiB; el GC también dejó más memoria comprometida. El experimento confirma retención real de objetos anteriores y evidencia de crecimiento privado, pero la cantidad retenida por esos objetos exige una ruta de raíz y contabilidad separada. El heap confirma la ruta `SkinEditorOverlay.State → ValueChanged → BmsTextHud o BmsStageHud → BmsDrawableRuleset → grafo del ReplayPlayer`. Apareció en tres jugadores de la captura. Los callbacks estaban registrados directamente en el bindable de larga vida. Los dos HUD disponían sus eventos de gameplay, pero dejaban esa suscripción. El heap contenía además nueve `BmsSupplementalVideoFrameSource` marcados como dispuestos y sus nueve arrays de datos comprimidos únicos, por **211.100.668 bytes (201,32 MiB)**. Los arrays son una parte medible del heap de esa captura. No se extrapolan a todos los bytes privados ni a todos los codecs. El dump y los comandos SOS completos permanecen locales.

## Orden de disposición y posibles solapamientos

1. `BmsSoloSongSelect.OnStart` crea su cargador y el reproductor que corresponde a los mods. `PlayerLoader.prepareNewPlayer` empieza `LoadComponentAsync`. El framework enlaza la cancelación de la solicitud con el contenedor; si el reproductor no fue entregado, `PlayerLoader.Dispose` encadena su disposición al fin de la carga. Referencias: `BmsSoloSongSelect.cs:141–192`, osu `PlayerLoader.cs:550–566,672–765` y osu-framework `CompositeDrawable.cs:140–208`.
2. `Player.OnExiting` detiene efectos y su uso del reloj de beatmap, reinicia ajustes de música e inicia la salida visual. El stack hace `OnExiting`, devuelve leases de bindables y puede reanudar el selector con las pantallas salientes todavía en `exited`. Cuando expiran se quitan de los hijos y se programan en `AsyncDisposalQueue`. Referencias: osu `Player.cs:1194–1222` y osu-framework `ScreenStack.cs:285–393`, `AsyncDisposalQueue.cs:18–76`.
3. `CompositeDrawable.Dispose` cancela cargas pendientes y dispone hijos; `DrawableRuleset.Dispose` libera el tracker y dependencias. En BMS, `BmsDrawableRuleset.Dispose` quita sincronizadores y dispone controladores, `BmsPlayfield.Dispose` libera sus suscripciones/caché y el BGA elimina sus stores y preloader. Los callbacks de video comprueban generación y evento antes de instalar el drawable. Referencias: osu-framework `CompositeDrawable.cs:306–331`, osu `DrawableRuleset.cs:392–400` y ruleset `BmsDrawableRuleset.cs:95–114`, `BmsPlayfield.cs:106–114`, `BmsBgaDisplay.cs:279–296,452–492,559–591`.
4. Un worker suplementario señalado se une al terminar su frame en curso y drena sus frames. La cola BASS tiene una vida separada y ejecuta detach/free en el hilo de audio. La textura administrada y el recurso del driver tienen vidas distintas; la cola del draw thread y el GC pueden posponer la limpieza. Referencias: `BmsSupplementalVideoFrameSource.cs:115–138`, `BmsBassMixerBridge.cs:64–116`, osu-framework `GLTexture.cs:134–233`, `VeldridTexture.cs:126–207`.

El solapamiento demostrado es el intervalo entre regreso y fin de disposición de las pantallas. El ensayo estabilizado espera ambas pantallas antes de la siguiente selección. La carga de un nuevo jugador mientras el anterior todavía se dispone, el drenado completo de las demás colas y la duración individual de cada liberación quedan pendientes de medición.

## Reevaluación de MEM-01 a MEM-13

| Hallazgo previo | Evidencia y relación con esta acumulación |
| --- | --- |
| MEM-01 · ArrayPool y subida | Defecto de propiedad independiente. En `BmsSupplementalVideoDrawable.cs:104–114` el `using` devuelve el buffer tras `SetData`; los renderers de producción encolan el mismo upload. La prueba previa de consumo diferido observó mutación de los píxeles. Falta atribución de la pendiente a esa ruta. |
| MEM-02 · datos BGA comprimidos | `BmsBgaDisplay.cs:332–368,795–902,1015–1024` muestra preload, caché por rutas y devolución del mismo respaldo vía stream. El fallback del drawable puede duplicar los datos. El store vacía sus diccionarios al disponerse. |
| MEM-03 · cola de frames | `BmsSupplementalVideoFrameSource.cs:74–105`. El límite nominal de tres depende de las condiciones de adelanto; el harness anterior observó hasta ocho. Cola por worker, con drenado al disponer. Su contribución a la pendiente de esta sesión queda sin cuantificar. |
| MEM-04 · worker en espera | `BmsBgaVideoPreloader.cs:20–74` y `BmsSupplementalVideoFrameSource.cs:61–138`. Un worker preparado por ruta puede vivir hasta consumir o disponer el preloader. Cancelación y join comprobados previamente en aislamiento. Los hilos se midieron por proceso, sin contabilidad individual de workers y FFmpeg. |
| MEM-05 · recursos GPU | `GLTexture.cs:134–233` y `VeldridTexture.cs:126–207` sitúan liberaciones y uploads fuera de la vida visible del drawable. Bytes del driver y cola del renderer **NO MEDIDOS**. |
| MEM-06 · rehost y carga tardía | `BmsBgaDisplay.cs:452–492,559–591` transfiere los stores y valida la generación del evento. Una ruta tardía puede solaparse hasta cancelación/disposición. Los displays débiles se cuentan por separado; dos por jugador es compatible con original y rehost. |
| MEM-07 · residentes PCM | `BmsPcmAssetCache.cs:10–117`. El presupuesto de 512 MiB es blando; las leases fijadas y preparaciones pueden excederlo. La disposición cancela, espera preparaciones y vacía entradas. Bytes PCM residentes en estas sesiones **NO MEDIDOS**. |
| MEM-08 · WAVE y preparación | Los buffers temporales de conversión WAVE y rutas que quedan fuera de ese presupuesto pueden explicar picos y compromiso reutilizable. Sus bytes y la cobertura WAVE/FLAC de la tanda acelerada quedan pendientes de inventario. |
| MEM-09 · handles BASS | `BmsBassMixerBridge.cs:64–116` encola detach/free desde Dispose. El fin de la pantalla puede antecederlo. La estabilidad funcional del callback del baseline de escritorio aporta una referencia, pero quedan pendientes handles y latencia para estas transiciones. |
| MEM-10 · preview global | `BmsWorkingBeatmap.cs:30,97–129` conserva el preview activo único. Reemplazarlo detiene y dispone el anterior. `BmsGameplayAudioController.cs:58–81` restaura solo si sigue activo. Tareas canceladas y participantes después de la salida requieren trazado si aparecen como raíces. |
| MEM-11 · caché de mapas y perfiles | `BmsWorkingBeatmapCache.cs:12–85` usa valores débiles, con poda de stores externos y claves pequeñas que pueden persistir. `BmsJudgementProfileProvider.cs:22` conserva una última tabla de ventanas. No se atribuyen los bytes de sesión a estos diccionarios. |
| MEM-12 · playfield y eventos | `BmsPlayfield.cs:106–114,403–428` muestra suscripciones, fallbacks y recursos de instancia. La alcanzabilidad de Player y BGA permite identificar candidatos; una ruta de raíz concreta es necesaria para atribuirla. |
| MEM-13 · tablas | La importación de tablas maneja JSON, notificaciones y caché fuera del recorrido de jugar/salir. El experimento no ejecutó importación de tablas de dificultad. |

## Fuentes

- [Auditoría anterior y pruebas aisladas](https://github.com/hinobara-ctrl/BmsRuleset/tree/8ef40515e82d8847ba4cd5dde61f0b11cc5d368d/docs/research).
- [PlayerLoader](https://github.com/ppy/osu/blob/9a10d935b05a3b896f4bd811d620f2023331db93/osu.Game/Screens/Play/PlayerLoader.cs), [Player](https://github.com/ppy/osu/blob/9a10d935b05a3b896f4bd811d620f2023331db93/osu.Game/Screens/Play/Player.cs).
- [ScreenStack](https://github.com/ppy/osu-framework/blob/37c3e328c8cc7a018234ec78982ea12d3712096a/osu.Framework/Screens/ScreenStack.cs), [CompositeDrawable](https://github.com/ppy/osu-framework/blob/37c3e328c8cc7a018234ec78982ea12d3712096a/osu.Framework/Graphics/Containers/CompositeDrawable.cs), [AsyncDisposalQueue](https://github.com/ppy/osu-framework/blob/37c3e328c8cc7a018234ec78982ea12d3712096a/osu.Framework/Allocation/AsyncDisposalQueue.cs).
