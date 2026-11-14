# Videos para la presentación

Lista para grabar los videos que muestran cómo funciona el sistema. Tres fuentes a la vez:
**celular** (tu cuerpo real), **Quest del actor** (lo que ve el actor) y **escritorio** (el
director, el avatar y el servidor). Graba cada video de una sola vez, sin cortar; se edita después.

## Preparación (cada sesión de grabación)

- [X] Ya pasó la lista de pruebas (`Pruebas_2026-11-12.md`): se graba la versión que funciona.
- [X] El **director en escritorio en Ensayo Remoto** ya se probó (visor, grabar con **R**, Corte).
  El video principal depende de eso.
- [X] Pods cargados, Quest cargado.
- [X] **Captura del Quest** (con el Quest conectado por USB). Se borra cada vez que el Quest se
  reinicia, así que córrelo otra vez después de reiniciarlo:

  ```
  adb shell "setprop debug.oculus.capture.width 1920 && setprop debug.oculus.capture.height 1080 && setprop debug.oculus.fullRateCapture 1 && setprop debug.oculus.capture.bitrate 30000000"
  ```

  Si la app se traba al grabar, cambia `fullRateCapture 1` por `0`.
- [X] Grabación de prueba de 30 s en el Quest: ¿se ve completo y fluido?
- [X] **Celular:** horizontal, en tripié, cuerpo completo, pods visibles, buena luz, de frente al
  receptor.
- [X] **Escritorio:** OBS grabando la pantalla completa a 1080p60, con la ventana del director y
  `Servidor.exe` a la vista.
- [X] Al empezar cada video: arranca los tres, y **aplaude con los controles en las manos
  mirándolos** (se ve en el celular, en el Quest y en el avatar, y se oye). En CapCut se alinean
  con el aplauso.
- [X] En el visor: gira la cabeza **despacio** y mantenla derecha. Los giros rápidos se ven mal en
  pantalla.
- [ ] Al terminar: conecta el Quest y pídeme que baje los videos.

# Parte A: las piezas (antes del sistema completo)

Videos cortos que explican cada pieza por separado, para que al ver el sistema completo nadie se
pregunte "¿y eso cómo funciona?" o "¿para qué se necesita?".

## A1. El hardware (fotos)

- [X] Los pods terminados.
- [X] Un pod por dentro.
- [X] Poniéndote un pod en la mano.
- [X] **Hoy:** el **receptor** en su cable.
- [X] **Hoy:** los **9 pods puestos en el cuerpo** (cuerpo completo: esternón, bíceps, muñecas,
  cuádriceps, espinillas).

## A2. Los pods se conectan (celular + escritorio) VIDEO DONE

- [X] Abre `Servidor.exe` con los pods apagados: "Receptor en COMx", "Pods 0/9".
- [X] Prende los pods uno por uno: el contador sube hasta 9/9 y cada uno muestra su batería y su
  señal. Explica: 9 pods → un receptor por Bluetooth → USB → el servidor.

## A3. El avatar sigue tu cuerpo (celular + ventana del servidor) VIDEO DONE

La ventana del servidor muestra el avatar **por detrás** (tu izquierda es su izquierda). Si el
celular te graba **por detrás** también, no se ve en espejo y se comparan directo.

- [X] **Calibración** con **Y**: el celular te muestra en cada pose (T, brazos abajo con
  sentadilla, brazos al frente con las palmas juntas) y la ventana la cuenta regresiva.
- [X] Muévete: brazos, sentadilla, caminar, girar. El avatar te sigue.
- [ ] Explica: cada pod da la rotación de su hueso; la calibración aprende cómo está pegado cada
  uno; las medidas del cuerpo ajustan el tamaño del avatar.

## A4. El vocabulario de pistas (sin video: una tabla en la presentación)

Grabar las pistas no sirve (no se ven ni se sienten en video). En su lugar va una tabla en la
presentación: qué pista, en qué pod y para qué se usa. Claude te da la información para el chat de
la presentación.

# Parte B: el sistema completo

## V1. Conexión y menús (escritorio + Quest) YA ESTÁ

- [X] Abrir `Servidor.exe`: entra al cuarto de Normcore y se conectan los pods.
- [X] Abrir `Director.exe`: conectando, luego el menú del director (Ensayo Remoto, Inspección de
  Escenas). Abre la rueda del menú de pausa una vez.
- [X] Ponerte el Quest del actor: el menú del actor (Ensayo Remoto, Ensayar Solo).

## V2. Ensayo Remoto, el video principal (las tres fuentes, una sola toma)

SHOT 12 ESCENA 3 SIGUIENTE SHOT TAMBIÉN

- [X] **Director:** Ensayo Remoto → Selección de Escenas → elige un shot.
- [X] **Actor:** entra solo al mismo shot.
- [X] **Tutorial** de los dos (si ya salió antes, vuélvelo a abrir desde el menú de pausa).
- [X] **Calibración** con **Y**: el celular te muestra en cada pose, el Quest muestra la cuenta
  regresiva, el escritorio muestra cómo se acomoda el avatar.
- [X] **Director:** el visor (**V**), cambia de lente con las flechas y encuadra al actor.
- [ ] **Toma 1:** Acción → actúa → Corte. VAMOS A USAR EL 12
  - [X] El celular y el avatar en el escritorio se mueven igual (la prueba de que están
    sincronizados).
  - [X] Después de Corte: el **reporte de la toma**. PARA ESTE VAMOS A USAR EL comparacion shot 1 (salio a la primera)y shot 6 (5:11 es el bueno, donde digo que se repite es en 3:46)
  - [X] El menú de guardar: muestra preferida, favorita y descartar; al final márcala
    **preferida**.
- [ ] **Toma 2 del mismo shot:** ahora **sientes las pistas** que salieron de la preferida. Que
  el celular capte cómo reaccionas. En la edición pon un letrero cuando llega cada pista (cuál y en
  qué pod). no
- [ ] **Pistas de guía:** muestra el interruptor.
- [X] **Siguiente shot** en el menú: carga el siguiente shot. Ahí termina el video. 12 to 13

## V3. Inspección de Escenas (solo escritorio)

SHOT 12 ESCENA 3

- [X] El mismo shot → la lista de tomas → abre la **preferida**.
- [X] La repetición: el avatar se mueve, la cámara sigue el encuadre del director, se oyen las
  voces.
- [X] La **fila de pistas** bajo el slider: el cursor pasa por cada pista cuando el avatar da un
  paso o gira.
- [X] **Editar pistas** (lápiz): arrastra una pista, agrega una desde la paleta, borra una,
  minimiza el editor.

## V4. Ensayar Solo (celular + Quest, y la ventana del servidor)

- [ ] El mismo shot, solo, sintiendo las pistas de la preferida del director.
- [ ] El mismo movimiento que en V2 (para ponerlos lado a lado en la edición).

## V5. Opcional (cortos)

- [X] **Análisis:** correr `python Analysis/analizar_tomas.py` y recorrer `informe.html` (con
  pistas / sin pistas).
- [ ] **Latencia:** los resultados (53 ms con 1 pod, 62.5 ms con 9), como imagen o tabla.

## Notas

(lo que salió mal, qué hay que regrabar)

**Para decir en la presentación (shot 6, fotos en `C:\Users\haili\Videos\Tesis_Pres_Vid\Fotos`):**
preferida = toma 44 (9.0 s, Marca a los 7.2 s); la primera toma con pistas (10.4 s, Marca a los
7.8 s) salió a la primera, sin repetir. El reporte dice "0.4 s tarde"; con el mismo criterio en las
dos tomas son **0.6 s** (la Marca de cada línea de tiempo, "detenido" = menos de 0.08 m/s) o
**0.8 s** (`informe.html` del análisis, menos de 0.12 m/s en las dos). El reporte de la app mezcla
los dos umbrales (0.12 m/s en la toma, 0.08 m/s en la preferida), por eso da menos. Resumen
honesto: llegó entre 0.6 y 0.8 s tarde, según qué tan quieto cuenta como "detenido".
Análisis: `python analizar_tomas.py --escena 2 --shot 6 --desde 2026-11-13 --salida resultados\shot6_presentacion` (toma 62 contra la preferida 35 en la base del servidor; la app
mostró "toma 44", el número de la otra máquina). Lo demás coincide: a 0.38 m de la marca, 2° desviado,
recorrido a 0.17 m de la preferida, duración +1.4 s (10.4 − 9.0).
