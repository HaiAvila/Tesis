# Datos para la presentación

Para el chat que ayuda a armar la presentación de la defensa. Aquí está lo que va en las
diapositivas (videos, fotos, tabla de pistas, latencia, resultados) y qué documentos del repo leer
para lo demás. Todo está verificado contra el código y los datos al 14 de noviembre de 2026.

## Qué leer del repo (en este orden)

| Archivo | Qué tiene | Cómo usarlo |
|---|---|---|
| `CLAUDE.md` (raíz) | El resumen del proyecto entero: propósito, hardware, protocolo, roles, arquitectura de red, base de datos. | Léelo completo primero. Está en inglés. |
| `Docs/pipeline.md` | Quién hace qué: equipo de previz, actor, director, servidor; los escenarios (ensayo remoto, ensayar solo, inspección, día de rodaje). | Corto. Base para la diapositiva de "cómo se usa". |
| `Docs/pod-reference.md` | El hardware a detalle: componentes, cableado, PCB, batería, mediciones de radio. | Para la diapositiva de hardware. |
| `Docs/pod-map.md` | Qué pod va en qué parte del cuerpo. | Una tabla de 9 filas. |
| `Latency/README.md` | Cómo se midió la latencia y qué corridas cuentan. | Para la diapositiva de latencia. |
| `Docs/Controles.md` | Todos los controles, en español. | Solo si se necesita explicar un botón. Es largo. |
| `Docs/STATUS.md` | El estado de cada pieza contra el plan. Muy largo (96 KB). | No lo leas entero: busca un tema cuando haga falta. |
| `Docs/Videos.md` | La lista con la que se grabaron los videos. | Contexto; la lista final de videos está abajo. |
| `Analysis/analizar_tomas.py` | El análisis de las tomas. El comentario del inicio explica qué mide. | Solo el comentario del inicio. |

Lo demás (código en `Assets/`, `Firmware/`, `Bridge/`) no hace falta para la presentación.

## Los videos, en orden

En `C:\Users\haili\Videos\Tesis_Pres_Vid\`. Algunos se ven mientras ella habla, y otros lado a lado
en la misma diapositiva.

| # | Archivo | Dura | Qué muestra |
|---|---|---|---|
| 1 | `Pods_on_edited.mp4` | 30 s | Se prenden los pods y aparecen en la ventana del servidor (9/9, batería y señal de cada uno). |
| 2 | `calibracion.mp4` | 72 s | La calibración (tres poses) y luego el avatar del servidor siguiendo los movimientos reales. |
| 3 | `menu_act_tutorial.mp4` | 55 s | Desde el Quest del actor: menú principal, menú de pausa (la rueda) y el tutorial. |
| 4 | `menu_dir_tutorial.mp4` | 60 s | Lo mismo para el director. Va **lado a lado** con el 3. |
| 5 | `ensayo_shot12.mp4` | 38 s | Ensayo remoto actor-director del shot 12 (escena 3). |
| 6 | `inspeccion_sho12.mp4` | 61 s | Inspección de Escenas del mismo shot: la repetición y las pistas que el sistema generó a partir del ensayo. |
| 7 | `shot14_shot15Edicion.mp4` | 46 s | Shot 14: las pistas generadas quedaron perfectas, sin tocarlas. Shot 15: el director adelantó un poco las pistas en el editor. Muestra la generación automática y la edición manual. |
| 8 | `Shot1_cues.mp4` | 29 s | Escena 1, shot 1: la toma con pistas que salió **a la primera** (toma 61). Es la prueba principal (ver "Resultados"). |
| 9 | `Shot6_reps_max_cues.mp4` | 50 s | Shot 6 con **otra persona** (una amiga) con los pods. Ella sintió que no siguió las pistas y pidió repetir; la segunda vez le salió. Muestra que quien trae los pods sabe por sí misma si lo hizo bien, sin que nadie se lo diga. |

**Pendiente (miércoles):** el ensayo remoto de verdad, ella con los pods en un lugar y una amiga
con el director de escritorio en otro, en video o fotos. Es lo que prueba lo "remoto".

**Ensayar Solo** se explica de palabra: el actor ensaya con las pistas de la preferida sin el
director. Funciona igual que una segunda toma con director (video 8), así que no lleva video propio.

## Fotos

En `C:\Users\haili\Videos\Tesis_Pres_Vid\Fotos\`:

- `shot1_comparacion.png`: la gráfica de recorridos del shot 1 (ver "Resultados").
- `shot6_comparacion.png`, `shot6_preferida.png`, `shot6_second_take.png`: el reporte de la toma
  del shot 6 y las líneas de tiempo de pistas de la preferida y de la toma con pistas.
- `latencia_grafica.png`: la gráfica de latencia (ver "Latencia").
- Hardware (las agrega ella): un pod en isométrico, un pod de lado (interruptor, el hueco del nRF,
  el compartimento del LRA), el interior con la PCB y un pod en la mano. **No hay foto del cuerpo
  con los pods, ni la habrá:** la calibración en video ya los muestra puestos.

## Tabla de pistas hápticas

Los 9 pods: 1 muñeca izq., 2 muñeca der., 3 bíceps izq., 4 bíceps der., 5 cuádriceps izq.,
6 cuádriceps der., 7 espinilla izq., 8 espinilla der., 9 esternón.

Cada pista es un **patrón que toca el propio pod** (una sola orden por Bluetooth reproduce el patrón
completo, así el ritmo no se pierde aunque la radio esté ocupada). Los patrones del motor:
CLIC (un toque corto), DOBLE (dos toques), TRIPLE (tres toques espaciados), LARGO (una vibración
larga), PULSO (toques repetidos a un ritmo), PASO (vibración, silencio, vibración) y SUBIDA
(4 toques que suben de intensidad, 40 → 100 %).

**Las pistas que salen de la toma preferida** (el sistema las genera solo cuando el director marca
una toma como preferida, y el director puede editarlas):

| Pista | Pod | Patrón | Cuándo | Para qué |
|---|---|---|---|---|
| Acción | Los 9 | TRIPLE | Al empezar la toma | Arranca la cámara: empieza a actuar. |
| Corte | Los 9 | LARGO (suave) | Al terminar | Se acabó la toma. |
| Pasos | Espinilla de esa pierna (7 u 8) | PASO | En cada paso que dio el actor en la preferida | Llevar el ritmo y saber qué pie mover. |
| Giro | Muñeca del lado del giro (1 o 2) | CLIC | 0.3 s antes de que el cuerpo empiece a girar en la preferida | Hacia qué lado girar y cuándo. |
| Marca | Esternón (9) | DOBLE | Al llegar a donde la preferida se detuvo | Llegaste a tu marca. |
| Aviso | Esternón (9) | SUBIDA | 1.5 s antes de llegar a la marca, solo en los shots que lo piden | Prepárate: algo viene. |
| Latido | Esternón (9) | DOBLE a ritmo de corazón | Todo el shot, solo escena 1 shot 4 | Ritmo emocional (tensión). |
| Proximidad de la mano, Contacto, Sostener | Muñeca de la mano que se mueve | CLICs cada vez más rápidos y DOBLE; LARGOs encadenados; LARGO y DOBLE | Solo en los shots que lo piden | Acercar la mano a un objeto, tocarlo, mantener una postura. |

**Las pistas en vivo** (durante la toma, según dónde está el actor en ese momento, medido por su
visor):

| Pista | Pod | Patrón | Cuándo | Para qué |
|---|---|---|---|---|
| Proximidad a la marca | Esternón (9) | PULSO cada vez más rápido (de 0.8 s entre toques a 2.5 m, a 0.1 s en la marca), luego DOBLE al llegar | Mientras camina hacia su siguiente marca | Qué tan lejos está la marca (como el control de un PS5). |
| Corrección de orientación | Muñeca hacia donde hay que girar (1 o 2) | CLIC, se repite cada 1.5 s | Si lleva 1.5 s mirando más de 35° fuera de donde miraba en la preferida | "Gira hacia este lado." |

**Las pistas de la acción del set** (lo que pasa en la escena, al mismo tiempo para todos):

| Pista | Pod | Patrón | Escena | Para qué |
|---|---|---|---|---|
| El gigante: temblor | Espinillas (7 y 8) | PULSO | Escena 1 (montaña) | Sentir el suelo temblar. |
| El gigante: aviso | Esternón (9) | SUBIDA | Escena 1 | Algo grande se acerca. |
| El gigante: pisadas | Cuádriceps (5 y 6) | LARGO | Escena 1 | Cada pisada del gigante. |
| El robot pasa | Bíceps der. (4) y luego izq. (3) | LARGO | Escena 3 (departamento) | Por qué lado pasa. |

**La calibración:** CLIC a los 3, 2 y 1 s antes de cada pose, DOBLE cuando se captura, y al final
cada pod hace HECHO o FALLA (dos patrones propios del firmware).

**El interruptor "Pistas de guía"** del director apaga todo menos Acción y Corte. Es para el piloto
(tomas con pistas contra sin pistas); no se muestra en la presentación.

De las 18 pistas del diseño original (5 del MVP, 8 de respaldo, 5 experimentales), las de arriba son
las que el sistema reproduce hoy. Las experimentales (Laban, sincronía entre varios actores, notas
del director en vivo, texturas) siguen como trabajo futuro, salvo el Latido.

## Latencia

Medida el 10 de noviembre: desde que Unity manda la pista hasta que el micrófono de la PC oye vibrar
el motor (incluye el retraso del propio micrófono, así que exagera un poco). 30 pistas por corrida.

| Pods conectados | Pistas | Mediana | 90 % de las pistas bajo | Máximo | Sobre 100 ms |
|---|---|---|---|---|---|
| 1 | 30 | 52.5 ms | 73 ms | 85 ms | 0 |
| Los 9 | 60 (dos corridas) | 62.5 ms | 110 ms | 131 ms | 9 de 60 |

Meta: menos de ~100 ms. Se cumple en la mayoría de las pistas. Con los 9 pods compartiendo una
sola radio, la mediana sube unos 10 ms y más o menos 1 de cada 7 pistas llega después de 100 ms.
La orientación de cada pod llega a unas 11 veces por segundo, y no es el camino que importa para la
latencia; lo que tiene que ser rápido es la orden que va hacia el pod. Gráfica:
`Fotos\latencia_grafica.png` (también `Latency/latencia_grafica.png`, hecha con
`Latency/grafica_latencia.py`).

## Resultados: las tomas con pistas contra la preferida

La **preferida** es la toma que el director marca como la buena. El sistema genera las pistas a
partir de ella, y cada toma nueva se compara con ella. Todo se mide con la posición del **visor**
(la cabeza), no de los pies.

**Escena 1, shot 1: la prueba principal (video 8).** Preferida = toma 48 (11 de noviembre). La toma
con pistas, toma 61, salió a la primera:
- llegó a la marca y se detuvo a **21 cm** de ella, 0.9 s más tarde que la preferida;
- momento a momento estuvo, en promedio, a 12 cm de donde iba el actor en la preferida;
- duró 1.5 s más.

Otra toma del mismo shot, la toma 54 (del mismo día, más temprano), quedó peor: a 53 cm de la marca
y 1.9 s tarde. La gráfica `shot1_comparacion.png` muestra los recorridos vistos desde arriba: la
toma 61 (azul) va encima de la preferida (negro).

**Cómo defender los 21 cm** (no cambiar el número: es 0.214 m medido):
- El sistema mide la cabeza, no los pies. Con los pies en el mismo lugar, la cabeza se mueve
  10-20 cm solo por inclinarse o frenar (el cuerpo se va un poco hacia adelante al detenerse).
  Se ve en la gráfica: todas las líneas, incluida la preferida, hacen zigzag por cómo se balancea
  la cabeza en cada paso.
- 21 cm es menos que el largo de un pie y como un tercio de un paso.
- Lo que demuestra que funciona es el recorrido completo: el mismo camino que la preferida, a la
  primera, contra otra toma que quedó claramente más lejos.
- Frase sugerida: *"El sistema mide la posición de la cabeza, que se mueve de forma natural al
  caminar. La toma con pistas se detuvo a 21 cm de la marca de la preferida, menos que el largo de
  un pie, y siguió el mismo camino a la primera."*

**Escena 2, shot 6 (fotos del reporte y de las líneas de tiempo).** Preferida = toma 35 en la base
del servidor (la app la mostró como "toma 44", el número en la otra máquina). La primera toma con
pistas, toma 62, salió sin repetir. El reporte de la app dice: a 0.38 m de la marca, **0.4 s tarde**,
mirando 2° desviado, recorrido a 0.17 m de la preferida en promedio, duración +1.4 s (10.4 s contra
9.0 s). Ella sintió con claridad qué pie mover.

**Por qué el reporte dice 0.4 s y las líneas de tiempo dan 0.6 s:** cada uno decide "ya se
detuvo" con un umbral de velocidad distinto.
- Las líneas de tiempo marcan la Marca cuando la cabeza baja de 0.08 m/s: 7.2 s en la preferida,
  7.8 s en la toma, o sea 0.6 s.
- El reporte toma el momento en que la toma bajó de 0.12 m/s (más permisivo, lo detecta antes) y
  lo compara con la Marca de la preferida (0.08 m/s). Mezcla los dos umbrales, por eso da 0.2 s
  menos.
- El análisis (`informe.html`), con 0.12 m/s en las dos tomas, da 0.8 s.
- Lo honesto: **llegó entre 0.6 y 0.8 s tarde, según qué tan quieto cuenta como "detenido"**. Todo
  lo demás del reporte coincide con el análisis.

**El análisis de una toma** (`Analysis/analizar_tomas.py`, sin instalar nada):
`python analizar_tomas.py --escena 1 --shot 1 --desde 2026-11-13 --salida resultados\shot1_presentacion`
y abrir `informe.html`. Arriba dice en palabras cuál toma se parece más a la preferida, y la
gráfica la resalta.

## Lo que todavía no se puede afirmar

- **Que las pistas mejoran el desempeño** contra no tener pistas: eso lo mide el piloto (dos
  personas, tomas con y sin pistas), que todavía no se hace. Las tomas viejas de desarrollo no
  sirven como comparación sin pistas (solo llegaron a la marca el 24 % de las veces porque eran
  pruebas). Hoy lo que se muestra es que el sistema funciona de punta a punta, no una mejora medida.
  El plan del piloto (miércoles 18 de noviembre, una persona que no conoce el sistema, escena 1
  shot 1, 3 tomas sin pistas y luego 3 con pistas, con cuestionario) está en
  `Docs/Piloto_2026-11-18.md`.
- **El ensayo remoto en dos lugares distintos:** se graba el miércoles.
