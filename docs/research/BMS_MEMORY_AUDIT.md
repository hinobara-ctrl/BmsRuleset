# Auditoría de memoria de BmsRuleset

## Resultado

La ruta de video suplementario devuelve al pool el array de un upload mientras el renderer todavía puede leerlo. La cola cuya constante dice tres fotogramas llegó a **ocho** con el decodificador original. La precarga puede conservar múltiples decodificadores y datos comprimidos duplicados durante una partida. El registro informado de **10,3 GB** carece de métrica identificada y sigue pendiente de atribución.

En 25 ciclos de apertura y cierre de recursos de un MPEG de 30,89 MiB, los workers terminaron, sus colas quedaron vacías y los 25 arrays de entrada fueron recolectables. Private Bytes quedó por encima de la primera iteración, sin aumentar ciclo tras ciclo. Una sonda de gameplay headless mostró mayor consumo con MPEG durante el arranque. Estos resultados acotan el problema. Falta medir transiciones repetidas dentro del cliente completo con GPU.

## Base y método

- Base de la rama research/bms-memory-lifecycle-audit: a1ab55d16eee20aa37efb47ba96910e8f319e474. Los dev de hinobara-ctrl/BmsRuleset y Sebaex/BmsRuleset coincidían al iniciar.
- osu! de solo lectura: 9a10d935b05a3b896f4bd811d620f2023331db93, 2026.1005.0.
- osu-framework de solo lectura: 37c3e328c8cc7a018234ec78982ea12d3712096a. El paquete que declara osu! es 2026.921.1.
- Se consultó el historial local de BGA y las búsquedas de incidencias memory y BGA de Sebaex en GitHub. No devolvieron coincidencias.

Private Bytes aproxima memoria privada comprometida. Working Set representa páginas residentes e incluye memoria que puede ser compartida. GC.GetTotalMemory(false) estima bytes de objetos administrados y su diferencia con Private Bytes no mide la memoria FFmpeg. El experimento de reachability fuerza GC en un punto separado de las mediciones naturales. GPU y nativo precisan contadores de propietarios, raíces o trazas del allocator.

## Propiedad de recursos

| Recurso | Propietario | Duración |
|---|---|---|
| BGA comprimido | BmsBgaResourceStore | Cache del display. Los streams pueden prolongar la vida del array después de Dispose. |
| Decoder suplementario | Preloader antes del consumo, drawable después | Worker cancelado y esperado al cerrar. El decoder dispone recursos y GCHandle. |
| Frames y uploads | Frame source → frame → upload → renderer | El traspaso final devuelve el backing array prematuramente. |
| Texturas | Stores y sprites manteniendo wrappers; renderer nativo | OpenGL/Veldrid liberan con disposición diferida. |
| Video framework | Video recibe stream del proveedor | Video.cs:206–225 cierra decoder y dispone texturas de frame. MP4/H.264 sigue esta ruta. |
| PCM y FLAC | PlaybackSession → controller → PCM cache y resource store | Cache de PCM por sesión, leases para voces, cache WAVE separado. BASS bridge agenda detach/free. |
| Notas, score y replay | Beatmap, playfield, procesadores, persistencia y resultados | Escalan con chart o Course. Reset retira historiales activos. |
| Preview, skins y selección | Working beatmap, preview activo, stores y renderer | Caches débiles y un preview estático. Se inspeccionaron las bajas de eventos al disponer. |
| Tablas y Realm | Estado global de ruleset y tareas de importación | Tablas e índice vivos con la aplicación. Entradas de archivos y JSON completos consumen memoria transitoria. |

## H1 · Upload diferido

**MEM-01. Defecto de propiedad confirmado. Prioridad alta, confianza alta.** En Media/Video/Supplemental/BmsSupplementalVideoDrawable.cs:103–114, CreateUpload transfiere los píxeles (BmsSupplementalVideoFrame.cs:16–22). Texture.SetData delega en NativeTexture (osu.Framework/Graphics/Textures/Texture.cs:198–218). VeldridTexture.SetData:191–204 y GLTexture.SetData:222–235 encolan la referencia. La lectura se da después en VeldridTexture.DoUpload:454–488 y GLTexture.Upload:247–253. TextureUpload.cs:28 reserva la liberación manual al upload sin encolar. Dummy no reproduce el backend diferido.

BmsPooledTextureUpload.Dispose:24–31 devuelve el array al salir del using y Data sigue exponiéndolo. Un pool controlado confirmó que el consumidor tardío lee el contenido escrito al alquilar otra vez el mismo buffer. El guard de disposición evita otra devolución secuencial y no sincroniza hilos. La combinación de código original y prueba demuestra la carrera de reutilización. Queda por comprobar la corrupción visual efectiva con GPU real. Una fuga exige demostrar que se acumulan buffers vivos. Al corregir este defecto, los buffers válidos pueden permanecer en cola más tiempo y habrá que medir ese coste.

## H2 · Precarga de archivos completos

**MEM-02. Consumo por diseño y oportunidad de reducción. Prioridad alta, confianza alta.** UI/HudComponents/BmsBgaDisplay.cs:330–373 precarga todo BMP declarado, incluso si jamás se muestra. Get:854–902 usa MemoryStream y ToArray. El buffer de trabajo y el array final pueden coexistir temporalmente. Los alias resueltos comparten bytes en lectura serial. El lock parcial deja que dos misses del mismo path hagan I/O y asignen por separado. Queda por medir la concurrencia real.

El test confirma que GetStream:905–909 envuelve el array, incluso cuando Dispose:1015–1025 ya ha limpiado el diccionario. El camino caliente pasa el mismo array al frame source y decoder. El camino fallback suplementario copia para probar el codec (SupplementalBmsBgaVideoProvider.cs:66–87) y para reproducir (BmsSupplementalVideoDrawable.cs:74–84). Durante reproducción pueden convivir el array original del stream y la copia del decodificador. En el framework Video el stream entregado sigue contando como propietario del backing array.

Las imágenes pueden vivir simultáneamente en comprimido y como textura. Si una ruta sirve de base y overlay, el renderer puede tener dos texturas porque BmsBgaLayerTextureLoaderStore.cs:12–33 aplica transparencia negra sólo para el overlay. Un RGBA lógico ocupa 4 × ancho × alto, antes de pool, mipmaps y asignaciones nativas. No equivale a una medición de VRAM.

## H3 · Colas y decoders calientes

**MEM-03. La constante no es un tope. Prioridad alta, confianza alta.** BmsSupplementalVideoFrameSource.cs:67–107 espera sólo al alcanzar simultáneamente los tres frames y target + 0,10 s. Un salto de reloj o varios timestamps dentro del horizonte dejan avanzar al productor. El filtro de obsolescencia limita parte del exceso. Con MPEG real de fixture, target 0,15 s y sin consumidor se observaron **ocho** frames. A 1080p ocho RGBA son 63,28 MiB lógicos frente a 23,73 MiB para tres. La prueba no usa resolución 1080p.

**MEM-04. Decoders abiertos durante el chart. Prioridad alta en mapas con varios videos, confianza alta en la ruta.** BmsBgaVideoPreloader.cs:19–40 abre un probe, lo cierra y arranca un worker por path compatible. Conserva fuentes hasta el primer uso o Dispose (:44–72). En EOF, FrameSource.cs:83–90 vuelve a esperar 8 ms con el decoder todavía abierto. El worker que nunca llega a mostrarse puede vivir hasta finalizar el mapa. Tres fuentes simultáneas se probaron en aislamiento. Falta contar cuántas conviven en charts reales.

BmsSupplementalVideoDecoder.cs:53–115,121–140,429–450 libera codec, packet, AVFrame, swscale, format/AVIO, stream y GCHandle. Un fallo al adoptar el buffer inicial también tiene liberación. FrameSource.Dispose:115–138 cancela, despierta, espera al worker y vacía la cola. La espera no tiene límite propio para un frame en curso. Los ciclos ensayados cerraron. No cubren archivo dañado que bloquee FFmpeg. TryTakeLatestFrame:40–59 devuelve descartes al pool y respeta el target.

## H4 · Texturas y GPU

**MEM-05. Retención temporal plausible. Prioridad media/alta, confianza alta en los contratos, impacto pendiente.** BmsSupplementalVideoDrawable.cs:107–121 crea textura directa o la reemplaza al variar tamaño y no tiene liberación explícita del wrapper. Renderer.cs:134,1171–1191 registra referencias débiles y devuelve Texture. Texture.Dispose:340–348 es vacío para el wrapper base. VeldridTexture.cs:105–137 y GLTexture.cs:145–168 finalizan y agendan destrucción nativa. El tiempo depende de sprites, draw nodes, uploads, GC y el hilo de render.

TextureStore.Dispose:327–335 dispone stores de entrada. LargeTextureStore.cs:61–63 emplea wrappers refcount para fondos externos. No hay que disponer texturas ajenas por el solo hecho de reemplazar un sprite. La hipótesis de una raíz fuerte en el registro general de texturas queda descartada por código. La retención temporal y la acumulación GPU quedan pendientes de medir al salir y cambiar de resolución.

## H5 · Superseder y salir durante carga

**MEM-06. Posible solapamiento retenido durante carga. Prioridad media, confianza media en el impacto.** BmsBgaDisplay.cs:447–489 transfiere recursos, stores y preloader al display reubicado y nulifica el shell. Eso descarta una segunda copia persistente de toda la precarga por el mero rehost en este SHA. El host se retira en el siguiente OnUpdate del padre y la suscripción se quita al disponer (:1034–1070).

setLayerDrawable:558–596 usa Clear, generación y evento activo. Un callback obsoleto dispone su drawable. La carga anterior puede continuar hasta que termine y solaparse con la nueva. BmsDeferredVideoDrawable.cs:25–50 pasa el stream al proveedor y carga otro drawable de forma anidada. CompositeDrawable.cs:145–208,306–329 liga cancelación a Dispose y dispone también componentes aún en carga. Clear puede agendar disposal (:879–886). El callback y Clear no son barreras de memoria. Falta forzar salida con I/O en curso, callback ya encolado, rehost removido y activación inmediata de otro video. El Get que completa I/O después de Dispose puede volver a escribir en la caché sin un guard de ciclo de vida. No se demostró un root que la mantenga viva entre mapas.

## Resto del ruleset

| ID | Evidencia | Evaluación |
|---|---|---|
| MEM-07 · PCM | Media/Audio/Samples/BmsPcmAssetCache.cs:12–19,60–87,130–168 contabiliza sólo arrays float publicados. Expulsa completos sin leases sobre 512 MiB. Los assets incompletos o en uso pueden superar el presupuesto. Los dos permisos se ceden entre chunks y pueden quedar processors parciales vivos. Fuera del presupuesto quedan entrada comprimida, WAVE, BASS, temporales, voces y otros caches como el de preview. | Consumo esperado potencialmente elevado. Prioridad alta si el perfil apunta a audio. Los tests existentes de leases y expulsión aprueban. Falta medir un chart denso real. |
| MEM-08 · FLAC | IO/ResourceStore/BmsAudioResourceStore.cs:15–84 retiene WAVE completo por path y limita a cuatro decodes FLAC concurrentes por proceso. BmsFlacDecoder.cs:45–69,160–179,355–397 consume toda la entrada, dimensiona salida y libera nativo. El límite de asignación inicial no es tope de WAVE. Controller.Dispose:357–374 dispone PCM y store. | Cache residente independiente. Prioridad media/alta. Sin fuga demostrada. |
| MEM-09 · BASS y voces | BmsPcmPlaybackSession.cs:86–99 dispone controller, bridge y mixer. BmsBassMixerBridge.cs:64–75,110–121 agenda detach/free al hilo de audio. BmsPcmVoiceMixer.cs:469–495 libera las reservas. | Falta medir handles tras cerrar/cambiar dispositivo y latencia. Tests focales pasan. |
| MEM-10 · Preview | Beatmaps/BmsWorkingBeatmap.cs:30,112–134,171–186 fija el último preview en un estático, dispone el anterior al reemplazarlo. BmsEventPreviewTrack.cs:149–165,197–208 cancela tareas y dispone playback. Sus closures pueden retener chart mientras preparan el timeline. | Una referencia global al último preview, no una lista por canción. Medir objetos alcanzados al cambiar a otro ruleset y salir del selector. Prioridad media. |
| MEM-11 · Cachés globales | BmsWorkingBeatmapCache.cs:14–46,54–99 guarda valores débiles, poda paths de texturas y borra UUID por invalidación/relectura. BmsFileResourceStore.cs:21,123–146 retiene claves de índices débiles. BmsJudgementProfileProvider.cs:12–25,80–104 cachea por layout/rate sin expiración y un último valor por hilo. BmsFrameRateUnlock.cs:14,22–50 guarda hosts con lease, BmsGameplaySettingsController.cs:47–55 libera normalmente. | Crecimiento posible de metadatos según corpus y sesiones. No hay evidencia de un impacto de varios GiB. Prioridad baja/media. |
| MEM-12 · Skins y gameplay | BmsGameplaySkinCache.cs:29–36 cancela SourceChanged, BmsDrawableRuleset.cs:95–113 limpia mods/controladores y eventos. BmsPlayfield.cs:111, BmsColumn.cs:161 y displays de salud, combo, juicio y timing tienen bajas. BmsHealthProcessor.History.cs:40–49 añade estados por evento y limpia al reset. | Pool e historiales dominados por el chart/score vivo. Falta snapshot por tipo con skins/hitlights y replay. |
| MEM-13 · Parser, importaciones y tablas | BmsBeatmapDecoder.cs:92–140 carga bytes, líneas originales y materializaciones. BmsFileImporter.cs:402–477 lee archivos completos en concurrencia acotada. BmsTableDownload.cs:17–28 lee respuesta completa, tiene timeout/cancelación. DifficultyTableStore.cs:54,139–254,423–426 retiene tablas e índice. BmsRulesetRuntime.cs:14–51,65–92 mantiene store global. Las suscripciones al store de carousel/curso inspeccionadas tienen baja. | Picos transitorios y estado persistente esperado. Falta medir grandes importaciones y ramas de excepción. No hay una tabla nueva por mapa reproducido. |

## Experimentos

Release .NET 10 con tool Brotli existente. Build inicial 0 errores, 57 advertencias. Build incremental final 0 errores, 28 advertencias. TRX y logs se guardaron fuera del repositorio.

| Ejecución | Aprobados | Fallos | Omitidos | Nota |
|---|---:|---:|---:|---|
| focus-01 | 181 | 2 | 2 | Los dos previews Outlaw no encontraron el fixture en el artifacts-path aislado. |
| focus-02 | 183 | 0 | 2 | Mismo binario y filtro. Se copió el fixture original a la ruta esperada, fuera del checkout. |
| focus-03 | **184** | **0** | **2** | Build nuevo con el control adicional de alcance del MPEG grande. |

Las omisiones son TestMpegFallsToMissingWhenSupplementalUnavailable y TestDeclinesCleanlyWhenNativeBackendNotEmbedded. El backend estaba embebido. Los fixtures cubren routing MP4/MPEG/AVI/WMV. La repetición de recursos se centró en MPEG y es headless. Los casos de PCM/BASS/preview reutilizan pruebas existentes. H1 usa un consumidor diferido simulado de forma determinista. H3 mide el decoder real.

- Se midieron 25 ciclos con 0, 1 y 3 fuentes pequeñas. Cada worker iniciado terminó y su cola quedó vacía. En focus-01, 1 fuente pasó de 111,70 a 111,94 MiB Private y tres de 111,94 a 113,53 MiB. Máximo de primer frame/Dispose 1,97/0,16 ms para una fuente y 2,04/0,46 ms para tres. Son muestras del mismo proceso y no estiman frame time del juego.
- 25 ciclos de store y worker con el MPEG Outlaw de 32.393.220 bytes. Private Bytes tras Dispose, ciclos 1/5/10/15/20/25: 174,89 / 320,50 / 320,60 / 320,96 / 310,22 / 309,05 MiB. En el último, Working Set 387,31 MiB y managed estimado 91,29 MiB. Hubo 25 colecciones Gen2 adicionales del ciclo 1 al 25. En el punto separado de GC/finalizadores, **0 de 25 arrays** seguían alcanzables. Se descarta la retención de esas entradas en este harness. No se atribuye la meseta de Private Bytes a un allocator concreto.
- Cuatro recursos pequeños MP4/MPEG/AVI/WMV suman 25.546 bytes precargados. El stream MP4 observa el mismo array y retiene acceso después de disponer el store.
- Sonda del gameplay existente con dos procesos nuevos para Outlaw. El control quita declaraciones BMP/BGA y eventos 04/06/07/0A, y usa los mismos keysounds. El log confirma que la otra variante abre el MPEG precalentado. En el arranque de 8 s, cerca de 1 s de chart, sin BGA se observaron 202,89 MiB Private, 278,06 MiB Working Set y 135,79 MiB managed estimado. Con MPEG se observaron 320,23, 392,25 y 214,79 MiB. Diferencia de **117,34 MiB Private** en ese punto. Los tiempos de carga aislados fueron 1464,66/1401,00 ms, con posibles tareas locales concurrentes. No establecen ventaja de rendimiento.

Una segunda sonda de 22 s, en orden invertido y después del foco de pruebas, registró tres puntos con DummyRenderer y 240 updates/s. Ambas variantes observaron como máximo 345 objetos vivos. No mide presentaciones GPU. La sonda de audio estaba desactivada.

| BGA | Private MiB, ~1/11/21 s | Working Set MiB, ~1/11/21 s | Managed estimado MiB, ~1/11/21 s | Carga | Update P50 / P95 / P99 / máx. |
|---|---|---|---|---:|---|
| Sin video | 197,62 / 212,27 / 219,52 | 275,41 / 296,12 / 303,45 | 133,34 / 144,38 / 150,21 | 1371,12 ms | 0,226 / 0,365 / 0,623 / 26,988 ms |
| MPEG | 312,47 / 331,75 / 334,33 | 386,77 / 412,56 / 415,79 | 181,79 / 199,44 / 204,44 | 1429,96 ms | 0,231 / 0,383 / 0,642 / 27,177 ms |

La diferencia Private fue de 114,85 / 119,48 / 114,81 MiB en esos puntos aproximados. El intervalo P99 entre updates fue 4,65 / 4,73 ms sin/con MPEG. Los dos procesos terminaron con código 0. Este ensayo acota el coste de un chart con video manteniendo los keysounds. No mide un cambio futuro, VRAM, stuttering del render o latencia de audio.

## Reproducción

Compilar el proyecto de tests con la referencia fijada y un directorio de artifacts nuevo:

    dotnet build osu.Game.Rulesets.BmsRuleset.Tests/osu.Game.Rulesets.BmsRuleset.Tests.csproj -c Release "-p:OsuSourcePath=$OsuSource" "-p:BmsNativeBrotliToolPath=$NativeTool" --artifacts-path $Artifacts

El DLL de test está en bin/osu.Game.Rulesets.BmsRuleset.Tests/release bajo ese directorio. Con build externo, copiar el fixture original bms_test_songs/103_outlaw_ogg al subdirectorio bms_test_songs en el artifacts root para los dos tests de preview existentes. El caso nuevo de reachability usa CallerFilePath para encontrar el fixture del repositorio.

    $Filter = 'FullyQualifiedName~BmsMemoryLifecycleAuditTest|FullyQualifiedName~.Normal.Bga.|FullyQualifiedName~BmsPcmAssetTest|FullyQualifiedName~BmsPcmMaintenanceTest|FullyQualifiedName~BmsPcmLeaseScheduleTest|FullyQualifiedName~BmsBassMixerBridgeTest|FullyQualifiedName~BmsPreviewTrackTest|FullyQualifiedName~BmsPreviewAudioResolverTest'
    dotnet test $TestDll --filter $Filter --logger 'trx;LogFileName=tests.trx' --results-directory $Evidence -- NUnit.ConsoleOut=0 NUnit.ExplicitMode=None
    dotnet $TestDll --gameplay-diagnostic --filter gameplay --headless --chart $Chart --output $DiagnosticOutput --duration 22 --warmup-seconds 1 --update-hz 240 --draw-hz 120 --omit-audio-probe

Las aserciones nuevas de la carrera y del desborde son reproducciones, no expectativas jugables aceptadas. Tras una corrección, sustituir el control de exceso de cola por una garantía de límite y conservar los resultados de este corte.

## Windows y límites de lo comprobado

1. Usar un perfil dedicado, la misma versión Release, renderer, resolución, modo de hilos, límite FPS y foco. Repetir 20–50 vueltas por bloque dentro del mismo proceso. Separar inicialización de cachés y periodo estable.
2. Incluir mismo chart sin BGA, charts distintos sin video, MP4, MPEG/AVI/WMV, alternancia, reinicio, salida durante precarga, videos supersedidos, muchas muestras/FLAC y cambios de preview. Identificar backend efectivo, porque extensión y codec difieren.
3. Medir antes, pico de carga, cuadro activo y reposo tras volver al selector. Registrar Private Bytes, Working Set, heap comprometido y vivo, colecciones y latencias, contadores de workers/streams/frames y PCM/WAVE, uploads/GPU, FPS, P95/P99/máximo de frame time, tiempo a primer cuadro válido, salida y audio underruns.
4. Si el estado post-salida crece tras estabilizar, comparar localmente raíces administradas, VRAM y heap nativo en puntos equivalentes. Separar GC explícito de la medición normal. Mantener dumps y rutas locales fuera de Git.

Quedan pendientes la atribución de 10,3 GB, las transiciones reales de 20–50 partidas en el mismo cliente, GPU, cambios de dispositivo, salida durante carga y el coste de futuras correcciones. El protocolo anterior distingue qué condiciones reproducir en la siguiente fase.

**INVESTIGATION_PARTIAL_WITH_LIMITATIONS**
