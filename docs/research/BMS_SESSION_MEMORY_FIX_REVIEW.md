# Revisión de la corrección de memoria entre partidas BMS

## Causa y cambio

`BmsTextHud.cs` y `BmsStageHud.cs` registraban un delegado que capturaba el HUD en `SkinEditorOverlay.State.ValueChanged`. El editor vive durante toda la sesión. Cada delegado quedaba registrado después de disponer el HUD, y el volcado mostró que desde el editor seguía alcanzable el grafo del `ReplayPlayer` anterior. Véanse [el baseline original](BMS_SESSION_MEMORY_BASELINE.md) y [la reproducción continua y el análisis de raíces](BMS_SESSION_MEMORY_LIFECYCLE_REPORT.md).

Cada HUD ahora mantiene su propio `Bindable<Visibility>`, sincronizado con el editor por `BindTo`. Los enlaces entre bindables son débiles y el framework desconecta los bindables que pertenecen al drawable al abandonar la pantalla y durante su disposición. El callback de visibilidad queda en el bindable del HUD. Se conserva la actualización inicial para los hosts sin editor y la reacción del HUD activo a cambios del editor. La corrección abarca las dos suscripciones demostradas.

`BmsTextHudEditModeTest.cs` agrega una prueba con un editor compartido. Comprueba que ambos HUD responden mientras están vivos y que al disponer cada uno sus datos y apariencia dejan de cambiar. Antes del arreglo la prueba falló cuando el editor cambió un HUD de texto ya dispuesto, de `Sample Text Event` a `Game Start`. Con la corrección pasaron 249 pruebas focalizadas, 2 omitidas con el mismo backend y 0 fallos. Release terminó con 57 advertencias y 0 errores. Los TRX de antes y después se conservaron por separado. Al recompilar el harness para el control de foco se repitieron las tres pruebas de HUD con 0 fallos.

## Comparación con el mismo corpus

El par final reutilizó el TSV de doce canciones y doce vídeos distintos, el mismo ejecutable de diagnóstico por SHA-256, el mismo `osu.Game.dll` y framework por SHA-256, y el renderer Veldrid/Direct3D11. La diferencia productiva fue la DLL BMS. Se habilitó `BMS_SESSION_MEMORY_ALLOW_INACTIVE=1` en ambas corridas para permitir que el cargador siga si Windows quita el foco; el CSV registra el foco observado. Esta opción solo se aplica a una instancia de `PlayerLoader` de prueba. Los intentos que se quedaron esperando el foco se conservaron separados. [Hashes de las DLL del par](evidence/background-runtime-paired-hashes.csv).

| Medida | Original | Corrección |
| --- | ---: | ---: |
| Carga/salida reproducida | 12/12 | 12/12 |
| Memoria privada antes de entrar al primero (MiB) | 603,65 | 473,09 |
| Memoria privada estabilizada tras mapa 1 (MiB) | 917,86 | 765,33 |
| Memoria privada estabilizada tras mapa 12 (MiB) | 2428,43 | 1439,75 |
| Aumento entre las salidas 1 y 12 (MiB) | 1510,57 | 674,42 |
| Pico muestreado de memoria privada (MiB) | 2492,86 | 1784,63 |
| Pendiente estable de los ciclos 4–12 (MiB/ciclo) | +101,57 | +26,15 |
| Memoria privada tras GC diagnóstico (MiB) | 2444,67 | 1391,92 |
| Estimación administrada tras GC (MiB) | 1066,65 | 283,39 |
| Heap comprometido después del último GC (MiB) | 1640,66 | 892,84 |
| Jugadores anteriores alcanzables tras GC | 12 | 0 |
| Displays BGA anteriores alcanzables tras GC | 24 | 0 |

Ambas tandas completaron las 110 muestras con foco activo y las 12 reproducciones alcanzaron 4×. Al estabilizarse después de la salida 12, la diferencia fue **988,68 MiB menos**. El crecimiento entre las salidas 1 y 12 bajó **836,15 MiB**, descontando la diferencia inicial entre las dos corridas. Tras el GC controlado la diferencia de memoria privada fue de **1.103.888.384 bytes (1052,75 MiB)**. El heap corregido, tomado tras ese GC, tampoco contenía instancias de `ReplayPlayer`, `BmsBgaDisplay` ni `BmsSupplementalVideoFrameSource`. La captura diagnóstica previa conservaba 201,32 MiB en nueve buffers comprimidos de frame sources ya dispuestos.

La curva corregida subió durante los primeros mapas y cayó de 1748,24 MiB tras el mapa 9 a 1439,75 MiB tras el mapa 12. Quedó crecimiento de memoria privada frente al arranque, sin acumulación de jugadores ni de displays viejos después del GC. La diferencia de memoria privada abarca memoria administrada y nativa del proceso. El ensayo no atribuye la diferencia completa a un allocator o codec.

Las pendientes por ciclo son regresiones lineales de las muestras estabilizadas de los ciclos 4–12. La longitud de la serie permite observar el cambio de tendencia y la retención de pantallas, con capacidad limitada para caracterizar una sesión mucho más larga. El GC controlado se hizo tras las doce salidas y no forma parte de la serie natural. [Muestras de cada ciclo](evidence/paired-lifecycle-summary.csv), [Resumen](evidence/paired-lifecycle-summary.json).

## Rendimiento y audio

En el ensayo continuo, las medianas original → corrección fueron 2635,0 → 2633,5 ms desde la muestra del cargador hasta jugador listo, 1216,3 → 1210,2 ms hasta el primer frame BGA observado, 400,5 → 400,0 ms hasta regreso al selector y 808,2 → 804,0 ms hasta fin de disposición. La cadencia de pasos limita la precisión.

El CLI gráfico a velocidad normal repitió tres veces sin BGA, MPEG y MP4. Las medianas de carga original → corrección fueron 1312 → 1320, 1338 → 1326 y 1534 → 1527 ms. El P99 de update fue 0,91 → 0,91, 0,98 → 0,90 y 0,86 → 0,81 ms. Se registraron entre 1 y 3 updates por ejecución mayores de 8 ms en ambos árboles y cero callbacks BASS por encima del presupuesto en las 18 ejecuciones. Las tandas originales tuvieron predominantemente la ventana activa y las de corrección inicialmente inactiva. La comparación de tiempos a velocidad normal se reporta como serie orientativa por la diferencia de foco. [Datos gráficos y de audio](evidence/desktop-performance-after-before.csv).

En el control con ventana inactiva se completó un par para el mapa sin BGA y otro para MPEG, y tres pares para MP4. En MP4, la mediana del P95 fue 0,4311 → 0,5175 ms (+20,0 %) y la del P99 0,7646 → 0,8970 ms (+17,3 %). El P95 y el P99 subieron en las tres parejas. Las diferencias medianas fueron +0,0864 y +0,1324 ms de update. Los dos últimos pares usaron el mismo harness y dependencias por hash. En los seis procesos MP4 con foco igualado hubo cero callbacks BASS sobre presupuesto. El aumento supera el umbral de revisión del 5 %. El resultado identifica un costo de update asociado al candidato en esa prueba. Su causa y traducción a tiempos de presentación quedan pendientes. [Control de foco inicial](evidence/desktop-focus-control.csv), [Repeticiones adicionales de MP4](evidence/desktop-mp4-focus-repeats.csv).

El benchmark gráfico cuenta actualizaciones del motor y frames iniciados por el renderer. La presentación efectiva, latencia audible de keysounds, underruns de hardware, reproducción WAVE/FLAC bajo carga prolongada y recursos GPU/BASS por mapa quedan **NO MEDIDOS**. La carrera de propiedad de uploads y el comportamiento de las colas de video conservan la clasificación de la revisión original. El alcance del cambio fue el ciclo de vida de las suscripciones del HUD.

## Evidencia y decisión

- Baseline original de producción `a1ab55d16eee20aa37efb47ba96910e8f319e474`; baseline por mapa `0469cef`; diagnóstico `7a1ad58`; evidencia continua previa `e9091c8`; modo de foco comparable `043a5c3`.
- TRX de la prueba fallida `1F4990414027BAA3558B89E7F4517502A4E35B6E09DCDCA4FD6118111CA3FDFB`; TRX de las 249 aprobadas `28EDE20A2A642B89CFE5B8CE9D66127034935F392C2F7882C65379D8F69C8EE5`.
- SHA-256 de la DLL corregida probada `C543C0EB046564CFDA8578E751DDA07C3F999ECBC475F206EF8994DDEAB51AF2`.

**PARTIAL_FIXES_PENDING_CLIENT_VALIDATION.** La corrección eliminó la raíz de retención medida. Se conserva como prototipo aislado en la rama de investigación por el aumento repetido de P95 y P99 de update en MP4. Requiere revisión externa antes de adoptarse. También quedan pendientes la sesión habitual prolongada, la presentación efectiva y el audio audible.
