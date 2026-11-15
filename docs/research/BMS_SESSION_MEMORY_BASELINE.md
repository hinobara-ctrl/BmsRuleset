# Baseline de memoria entre partidas BMS

## Identidad y alcance

- Ruleset original `a1ab55d16eee20aa37efb47ba96910e8f319e474`, idéntico en `dev` del fork y upstream. Auditoría previa `8ef40515e82d8847ba4cd5dde61f0b11cc5d368d`.
- Build Release x64, SDK .NET 10.0.401 y runtime 10.0.12. Código osu!lazer `9a10d935b05a3b896f4bd811d620f2023331db93` (2026.1005.0); osu-framework 2026.921.1, referencia de código `37c3e328c8cc7a018234ec78982ea12d3712096a`.
- Windows 10.0.26300.9974 x64, Ryzen 7 5700X (16 hilos lógicos), Radeon RX 6600. Perfil aislado, 1280 × 720 configurado en ventana de escritorio, 240 Hz update y 120 Hz draw, multihilo. Veldrid en el host de escritorio, DummyRenderer en headless. La resolución física del monitor no se midió.
- BASS en salida regular silenciada por volumen maestro en escritorio y dispositivo sin salida en headless. MPEG usa FFmpeg suplementario Windows x64 empacado; MP4 pasa por el video del framework. Identidad binaria en [original-binaries.csv](evidence/original-binaries.csv).

## Corpus original

Se usaron fixtures públicos del repositorio. La preparación de derivados se hizo fuera del árbol Git y conservó los bytes, excepto las sustituciones descritas, mediante ISO-8859-1.

| ID | Chart | Recursos y preparación |
| --- | --- | --- |
| control | `103_outlaw_ogg/session_no_bga.bms` | `01_outlaw_spn.bms` sin declaraciones BMP ni canales 04, 06, 07, 0A. Conserva el material de audio. |
| mpeg | `103_outlaw_ogg/01_outlaw_spn.bms` | Original, `bga.mpg`, 408 entradas de audio en el diagnóstico. |
| mp4 | `[Clue]Random/session_mp4.bms` | `_random_s2.bms` con `__bga.mpeg` sustituido por `__bga.mp4` del mismo conjunto. |
| multi | `103_outlaw_ogg/session_multi.bms` | Añade `#BMP02 session_second.mpg` y `#00107:02`. El segundo recurso usa los mismos bytes para aislar dos rutas de reproducción. |
| static | `103_outlaw_ogg/session_static.bms` | Sustituye el recurso BMP01 por el primer JPEG por nombre del fixture, copiado como `session_static.jpg`. |

Los CSV de gameplay incluyen los SHA-256 de charts. Vídeo MPEG y copia `11084CFB4A27A3F7202808FBBFC21651278D79AFA3D8E3706CC2AA73CBB217FA`. MP4 `F644E97BA58BEFFFE2C8D07EA74BCF46C8E254A8F6C9E7D59D0C66F75ACEF447`. JPEG `81E093A29CB2E2929C847706AADE4725E02CD65B301801A5006BE5EA2D1CF763`. El inventario local conserva todos los assets copiados. FLAC/WAVE de sesión queda **NO MEDIDO** en la serie original.

## Protocolo y evidencia del árbol original

1. Compilar `osu.Game.Rulesets.BmsRuleset.Tests.csproj` con `dotnet build -c Release`, fijar `OsuSourcePath`, usar directorio de artefactos separado y conservar el log. Copiar el fixture `103_outlaw_ogg` a `bms_test_songs` en la raíz de esos artefactos.
2. Ejecutar el DLL de pruebas con `dotnet test --filter` seleccionando `.Normal.Bga.`, `BmsPcmAssetTest`, `BmsPcmMaintenanceTest`, `BmsPcmLeaseScheduleTest`, `BmsBassMixerBridgeTest`, `BmsPreviewTrackTest` y `BmsPreviewAudioResolverTest`. Resultado 177 aprobadas, 2 omitidas por condiciones incompatibles con el backend empaquetado, 0 fallos. Compilación con 57 advertencias y 0 errores.
3. Usar el CLI existente en un proceso y almacenamiento nuevos por mapa. Ejecutar tres rondas en el orden control, MPEG, MP4, multi, estático en headless. Repetir control, MPEG y MP4 en escritorio. Pasar `--omit-audio-probe` solo en headless.

```sh
dotnet "$test_dll" --gameplay-diagnostic --filter gameplay --chart "$chart" --output "$new_report_dir" --duration 13 --warmup-seconds 1 --update-hz 240 --draw-hz 120
```

4. Repetir tres veces `dotnet test --filter FullyQualifiedName~BmsSupplementalVideoDecodePerformanceTest` estableciendo `BMS_VIDEO_DECODE_REPORT` en un archivo nuevo. El microbenchmark decodificó 3000 frames por repetición en 720,6–727,5 ms de pared y 781,3–843,8 ms de CPU.

| Evidencia original | Resultado |
| --- | --- |
| [Headless, 15 procesos](evidence/original-headless-summary.csv) | Privada a 11 s, control 215,75–219,46 MiB; MPEG 292,27–328,16; MP4 793,18–799,18; multi 360,46–368,33; estático 218,72–223,70. |
| [Escritorio, 9 procesos](evidence/original-desktop-summary.csv) | Privada a 11 s, control 367,07–374,05 MiB; MPEG 480,86–490,32; MP4 688,66–691,89. En MPEG, carga 1299–1883 ms y P99 de update 0,89–1,17 ms. |
| Compilación y pruebas | SHA-256 del log `9F935EE09CD556090780639938D6AC4BA1837601D311039D059E68591E690B30`. TRX `B31C4C7978AAD6D37FDF02CB48EA77CD30E162C411F5CB8AF35F3C9E2874D23E`. |

El primer intento de escritorio con acceso restringido terminó antes de cargar y el finalizador del harness ocultó el error original con una excepción de referencia nula. Se preservó el log. Los nueve resultados de escritorio proceden de ejecuciones con acceso al escritorio y almacenamiento aislado. El conteo de frames iniciados corresponde al renderer, no a eventos de presentación del compositor. En nueve ejecuciones de escritorio el probe registró cero callbacks BASS superiores al presupuesto; la latencia, los underruns reales y el audio audible permanecen **NO MEDIDOS**.

Los datos crudos se generaron fuera del checkout en el directorio de evidencias de sesión, bajo `original-build`, `original-tests`, `original-headless`, `original-desktop`, `original-desktop-probe`, `original-desktop-retry` y `original-decode-benchmark`. Los resúmenes versionados carecen de rutas locales. No se forzó GC en los diagnósticos de memoria.

## Condición para interpretar los datos

Los procesos de un solo mapa cuantifican carga, variación y memoria durante gameplay. La acumulación y las métricas de disposición requieren una serie continua en el mismo proceso. Los tiempos de selección real, del primer BGA presentado, de disposición entre mapas, de carga recurrente y los bytes GPU/BASS quedan **NO MEDIDOS** en esta primera serie. El protocolo de sesión y sus resultados se registran por separado.
