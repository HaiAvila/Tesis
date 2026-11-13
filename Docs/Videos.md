# Videos para la presentación

Lista para grabar los videos que muestran cómo funciona el sistema. Tres fuentes a la vez:
**celular** (tu cuerpo real), **Quest del actor** (lo que ve el actor) y **escritorio** (el
director, el avatar y el servidor). Graba cada video de una sola vez, sin cortar; se edita después.

## Preparación (cada sesión de grabación)

- [ ] Ya pasó la lista de pruebas (`Pruebas_2026-11-12.md`): se graba la versión que funciona.
- [ ] El **director en escritorio en Ensayo Remoto** ya se probó (visor, grabar con **R**, Corte).
  El video principal depende de eso.
- [ ] Pods cargados, Quest cargado.
- [ ] **Captura del Quest** (con el Quest conectado por USB). Se borra cada vez que el Quest se
  reinicia, así que córrelo otra vez después de reiniciarlo:

  ```
  adb shell "setprop debug.oculus.capture.width 1920 && setprop debug.oculus.capture.height 1080 && setprop debug.oculus.fullRateCapture 1 && setprop debug.oculus.capture.bitrate 30000000"
  ```

  Si la app se traba al grabar, cambia `fullRateCapture 1` por `0`.
- [ ] Grabación de prueba de 30 s en el Quest: ¿se ve completo y fluido?
- [ ] **Celular:** horizontal, en tripié, cuerpo completo, pods visibles, buena luz, de frente al
  receptor.
- [ ] **Escritorio:** OBS grabando la pantalla completa a 1080p60, con la ventana del director y
  `Servidor.exe` a la vista.
- [ ] Al empezar cada video: arranca los tres, y **aplaude con los controles en las manos
  mirándolos** (se ve en el celular, en el Quest y en el avatar, y se oye). En CapCut se alinean
  con el aplauso.
- [ ] En el visor: gira la cabeza **despacio** y mantenla derecha. Los giros rápidos se ven mal en
  pantalla.
- [ ] Al terminar: conecta el Quest y pídeme que baje los videos.

# Parte A: las piezas (antes del sistema completo)

Videos cortos que explican cada pieza por separado, para que al ver el sistema completo nadie se
pregunte "¿y eso cómo funciona?" o "¿para qué se necesita?".

## A1. El hardware (fotos)

- [x] Los pods terminados.
- [x] Un pod por dentro.
- [x] Poniéndote un pod en la mano.
- [ ] **Hoy:** el **receptor** en su cable.
- [ ] **Hoy:** los **9 pods puestos en el cuerpo** (cuerpo completo: esternón, bíceps, muñecas,
  cuádriceps, espinillas).

## A2. Los pods se conectan (celular + escritorio)

- [ ] Abre `Servidor.exe` con los pods apagados: "Receptor en COMx", "Pods 0/9".
- [ ] Prende los pods uno por uno: el contador sube hasta 9/9 y cada uno muestra su batería y su
  señal. Explica: 9 pods → un receptor por Bluetooth → USB → el servidor.

## A3. El avatar sigue tu cuerpo (celular + ventana del servidor)

La ventana del servidor muestra el avatar **por detrás** (tu izquierda es su izquierda). Si el
celular te graba **por detrás** también, no se ve en espejo y se comparan directo.

- [ ] **Calibración** con **Y**: el celular te muestra en cada pose (T, brazos abajo con
  sentadilla, brazos al frente con las palmas juntas) y la ventana la cuenta regresiva.
- [ ] Muévete: brazos, sentadilla, caminar, girar. El avatar te sigue.
- [ ] Explica: cada pod da la rotación de su hueso; la calibración aprende cómo está pegado cada
  uno; las medidas del cuerpo ajustan el tamaño del avatar.

## A4. El vocabulario de pistas (celular + terminal)

Para mandar pistas a mano: **cierra `Servidor.exe`** (ocupa el receptor) y abre dos terminales en
la carpeta `Bridge`: `python bridge.py` en una y `python cue.py` en la otra. En `cue.py` escribe
`<pod> <pista> [parámetro]`; el pod `0` es todos.

- [ ] Celular de cerca a un pod junto al micrófono (la vibración no se ve, pero se oye).
- [ ] Cada pista con su uso: `9 2 30` proximidad (pulsos en el esternón), `1 1` orientación
  (muñeca izquierda), `7 8` paso (espinilla), `0 3` Acción (los 9), `0 4` Corte (los 9), `9 9` aviso que
  sube, `9 5` confirmación (marca).
- [ ] Opcional: los resultados de latencia (53 ms con 1 pod, 62.5 ms con 9).

# Parte B: el sistema completo

## V1. Conexión y menús (escritorio + Quest)

- [ ] Abrir `Servidor.exe`: entra al cuarto de Normcore y se conectan los pods.
- [ ] Abrir `Director.exe`: conectando, luego el menú del director (Ensayo Remoto, Inspección de
  Escenas). Abre la rueda del menú de pausa una vez.
- [ ] Ponerte el Quest del actor: el menú del actor (Ensayo Remoto, Ensayar Solo).

## V2. Ensayo Remoto, el video principal (las tres fuentes, una sola toma)

Usa la **escena 1** (el gigante se ve mejor).

- [ ] **Director:** Ensayo Remoto → Selección de Escenas → elige un shot.
- [ ] **Actor:** entra solo al mismo shot.
- [ ] **Tutorial** de los dos (si ya salió antes, vuélvelo a abrir desde el menú de pausa).
- [ ] **Calibración** con **Y**: el celular te muestra en cada pose, el Quest muestra la cuenta
  regresiva, el escritorio muestra cómo se acomoda el avatar.
- [ ] **Director:** el visor (**V**), cambia de lente con las flechas y encuadra al actor.
- [ ] **Toma 1:** Acción → actúa → Corte.
  - [ ] El celular y el avatar en el escritorio se mueven igual (la prueba de que están
    sincronizados).
  - [ ] Después de Corte: el **reporte de la toma**.
  - [ ] El menú de guardar: muestra preferida, favorita y descartar; al final márcala
    **preferida**.
- [ ] **Toma 2 del mismo shot:** ahora **sientes las pistas** que salieron de la preferida. Que
  el celular capte cómo reaccionas. En la edición pon un letrero cuando llega cada pista (cuál y en
  qué pod).
- [ ] **Pistas de guía:** muestra el interruptor.
- [ ] **Siguiente shot** en el menú: carga el siguiente shot. Ahí termina el video.

## V3. Inspección de Escenas (solo escritorio)

- [ ] El mismo shot → la lista de tomas → abre la **preferida**.
- [ ] La repetición: el avatar se mueve, la cámara sigue el encuadre del director, se oyen las
  voces.
- [ ] La **fila de pistas** bajo el slider: el cursor pasa por cada pista cuando el avatar da un
  paso o gira.
- [ ] **Editar pistas** (lápiz): arrastra una pista, agrega una desde la paleta, borra una,
  minimiza el editor.

## V4. Ensayar Solo (celular + Quest, y la ventana del servidor)

- [ ] El mismo shot, solo, sintiendo las pistas de la preferida del director.
- [ ] El mismo movimiento que en V2 (para ponerlos lado a lado en la edición).

## V5. Opcional (cortos)

- [ ] **Análisis:** correr `python Analysis/analizar_tomas.py` y recorrer `informe.html` (con
  pistas / sin pistas).
- [ ] **Latencia:** los resultados (53 ms con 1 pod, 62.5 ms con 9), como imagen o tabla.

## Notas

(lo que salió mal, qué hay que regrabar)
