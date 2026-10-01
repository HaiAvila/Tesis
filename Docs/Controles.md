# Controles

Este documento se generó revisando directamente el código (`Assets/Scripts/*.cs`), no de memoria.
Cada control indica el script exacto donde vive, para que sea fácil de re-verificar si el
comportamiento cambia.

## Cómo probar en el Editor (XR Device Simulator)

Todos los controles de mano de este documento se prueban con el **XR Device Simulator**, ya
presente en las 3 escenas de contenido. Antes de presionar cualquier tecla de control, hay que
seleccionar qué mano está "sosteniendo" el control virtual:

- Mantén **`T`** para que las teclas siguientes controlen el **control izquierdo**.
- Mantén **`Y`** para que las teclas siguientes controlen el **control derecho**.

Sin mantener `T` o `Y`, `WASD` + mouse mueven el HMD simulado directamente (cabeza/cuerpo, no
un control) — así es como se prueba de forma indirecta el modo de vuelo libre, ya que ese modo
no lee el HMD sino el joystick de la mano izquierda.

---

## Resumen rápido

| Control | Qué hace | Dónde está activo | Tecla en el Simulador | Botón físico Quest | Estado |
|---|---|---|---|---|---|
| Menu (control izquierdo) | Abre/cierra el menú de pausa | Las 3 escenas de contenido — Ensayo Remoto **e** Inspección de Escenas, actor y director | Mantener `T` + `M` | Botón Menu (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Secondary Button "B" (control derecho) | Inicia/detiene la grabación de la toma | Las 3 escenas de contenido — **técnicamente activo también durante Inspección de Escenas** (ver hallazgo abajo) | Mantener `Y` + `N` | Botón B (control derecho) | ⚠️ Implementado, alcance más amplio de lo previsto — ver hallazgos |
| Joystick izquierdo | Mueve la cámara libremente (vuelo) en la dirección donde mira la cabeza, sin gravedad ni colisión | Solo mientras hay una reproducción activa en Inspección de Escenas | Mantener `T` + `W,A,S,D` | Joystick del control izquierdo | ✅ Implementado, sin probar en hardware real |
| Primary Button "A" (control izquierdo) | Play/Pause del fantasma en reproducción | Solo mientras hay una reproducción activa en Inspección de Escenas | Mantener `T` + `B` | Botón A (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Reiniciar (↺) | Salta al inicio de la reproducción y reanuda | Solo Inspección de Escenas | — (solo UI, clic con mouse) | Ninguno — decisión explícita | ✅ Decidido: solo UI por ahora |
| Repetir (∞) | Activa/desactiva loop automático al llegar al final | Solo Inspección de Escenas | — (solo UI, clic con mouse) | Ninguno — decisión explícita | ✅ Decidido: solo UI por ahora |
| Slider de scrubbing | Salta a cualquier punto de la reproducción, arrastrando | Solo Inspección de Escenas | — (solo UI, clic+arrastre con mouse) | No aplica (es un control de arrastre, no un botón) | ✅ Decidido: solo UI |
| Casillas "Marcar como preferida" / "Marcar como favorita" | Al tildarlas antes de "Guardar", la nueva sesión se guarda con `es_preferida`/`es_favorita` ya en el mismo INSERT (preferida desmarca cualquier otra de la misma toma) | Panel de decisión (Ver replay/Guardar/Descartar) al detener una grabación — solo director | — (solo UI, clic con mouse) | Ninguno — decisión explícita | ✅ Decidido: solo UI por ahora |
| Estrella (★/☆) y corazón (♥/♡) por sesión | Marca/desmarca preferida (exclusiva por toma) y favorita (no exclusiva, bookmark) de una sesión ya guardada | Lista de sesiones en Selección de Escenas — solo aparece cuando un shot tiene más de una sesión guardada, durante Inspección de Escenas | — (solo UI, clic con mouse) | Ninguno — decisión explícita | ✅ Decidido: solo UI por ahora |
| "Ver" en Favoritos | Salta directo a reproducir esa sesión en Inspección de Escenas, interrumpiendo cualquier Ensayo Remoto activo | Pantalla Favoritos (ver entrada "Favoritos" del menú de pausa) | — (solo UI, clic con mouse) | Ninguno — decisión explícita | ✅ Decidido: solo UI por ahora |
| Joystick izquierdo (locomoción normal) | Caminar | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto o el vuelo libre está activo | Mantener `T`, mover joystick | Joystick del control izquierdo | ✅ De fábrica (XRI Starter Assets) |
| Joystick derecho | Girar (turn/snap-turn) | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto **o el visor de la cámara virtual del director está visible** | Mantener `Y`, mover joystick | Joystick del control derecho | ✅ De fábrica (XRI Starter Assets) |
| Primary Button "A" (control derecho) | Jump | Cualquier escena de contenido | Mantener `Y` + `B` | Botón A (control derecho) | ✅ De fábrica (XRI Starter Assets) — **este es el motivo por el que el atajo de Play/Pause se puso en el control izquierdo y no en el derecho** |
| Grip (control derecho) | Muestra/oculta el panel de la cámara virtual del director ("visor", como levantar un celular para mirarlo) | Solo Ensayo Remoto (`#if DIRECTOR_BUILD`, escena de contenido, `IsInspecting == false`) | Mantener `Y` + `G` | Grip (control derecho) | ✅ Implementado (sin encuadre ni guardado de configuración todavía), sin probar en hardware real |
| Joystick derecho (zoom del visor) | Arriba acerca (reduce el Field of View hacia 20°), abajo aleja (lo aumenta hacia 80°) | Solo mientras el panel del visor está visible (ver Grip arriba) | Mantener `Y` + `W/S` | Joystick del control derecho | ✅ Implementado, sin probar en hardware real |

---

## Detalle por control

### Menú de pausa — botón Menu (izquierdo)
**Script:** `PauseMenuController.cs`
**Binding real:** `<XRController>{LeftHand}/menu`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/menuButton` (nombre distinto al real — por eso hace falta un binding aparte solo para el simulador)
**Contexto:** activo en `Scene1_mountain`, `Scene2_spaceship`, `Scene3_apartment` — sin distinguir si es Ensayo Remoto o Inspección de Escenas, ni si es build de actor o de director. Ambos ven el panel; solo el director ve las opciones "Selección de Escenas" y "Favoritos" dentro de él (gateadas por `DIRECTOR_BUILD`).
**Efecto adicional:** pausa las GameObjects "Move" y "Turn" (locomoción por joystick) mientras está abierto; nunca toca tracking de cabeza ni gravedad. Dispara un pulso háptico al abrir y al cerrar.
**"Favoritos" (nueva entrada, solo director):** abre `FavoritesPanelController.cs`, un panel aparte (no navega a otra escena) con la lista de toda sesión marcada como favorita en cualquier escena/shot. Disponible desde cualquier escena de contenido, incluso en medio de una sesión de Ensayo Remoto activa — al elegir "Ver" en una entrada, salta directo a Inspección de Escenas para esa sesión exacta, interrumpiendo el ensayo en curso si había uno.

### Grabación — Secondary Button "B" (derecho)
**Script:** `RehearsalRecorder.cs`
**Binding real:** `<XRController>{RightHand}/{secondaryButton}`
**Binding en el Simulador:** `<XRSimulatedController>{RightHand}/secondaryButton`
**Contexto:** `#if DIRECTOR_BUILD` (solo existe en el build de director) y `Bootstrap.IsContentScene(...)`.
**⚠️ Hallazgo:** el gate solo revisa "¿es una escena de contenido?", no revisa `Bootstrap.IsInspecting`. Esto significa que, tal como está el código hoy, presionar B durante una sesión de **Inspección de Escenas** también inicia/detiene una "grabación" — aunque no tiene mucho sentido ahí, porque no hay un actor real transmitiendo posición en ese contexto (`ActorPositionSync` solo se mueve del lado del actor real). No estaba pedido explícitamente que se restringiera a Ensayo Remoto, así que lo dejo señalado aquí como pendiente en vez de decidir por mi cuenta si hay que bloquearlo.

### Vuelo libre del director — joystick izquierdo
**Script:** `DirectorFreeFlyCamera.cs`, activado/desactivado por `RecordingPlaybackController.cs`
**Binding real:** `<XRController>{LeftHand}/{Primary2DAxis}`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/primary2DAxis`
**Contexto:** `EnableFly()`/`DisableFly()` solo se llaman desde `RecordingPlaybackController.LoadAndStart()`/`CleanUp()` — es decir, **el vuelo libre solo existe durante Inspección de Escenas**. No está implementado para Ensayo Remoto.
**📌 Aclaración explícita pedida:** si en algún momento se planeó (o se quiere a futuro) que el director también pueda volar libremente durante Ensayo Remoto mientras dirige en vivo, **eso no está construido** — es un pendiente separado, no algo que ya funcione ahí solo porque funciona en Inspección de Escenas.
**Comportamiento:** mientras está activo, desactiva el `CharacterController` y el GameObject "Gravity" (sin colisión de piso, sin gravedad) pero deja "Turn" intacto. El movimiento sigue la dirección real hacia donde mira la cabeza (adelante/atrás/lateral, incluyendo arriba/abajo), no un plano horizontal fijo.
**Cómo probarlo en el Simulador:** mantén `T` (selecciona el control izquierdo) y usa `W,A,S,D` — mientras `T` está sostenido, esas mismas teclas alimentan el eje 2D ("Axis 2D") de ese control en vez de mover el HMD. Para ver el efecto del vuelo (movimiento en la dirección donde miras, incluyendo arriba/abajo) hace falta soltar `T` momentáneamente y usar el movimiento del HMD (`W,S,A,D,Q,E` sin mantener `T`/`Y`) para apuntar la cabeza hacia arriba o abajo antes de volver a sostener `T` y mover.

### Play/Pause de la reproducción — Primary Button "A" (izquierdo)
**Script:** `RecordingPlaybackController.cs`
**Binding real:** `<XRController>{LeftHand}/{primaryButton}`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/primaryButton`
**Contexto:** `#if DIRECTOR_BUILD` y solo mientras `panelObject != null` (una sesión de reproducción está cargada). Inerte en cualquier otra pantalla.
**Por qué el control izquierdo y no el derecho:** el Primary Button del control **derecho** ya está tomado por "Jump" (de fábrica, XRI Starter Assets) — se descubrió ese conflicto al implementar el atajo de grabación y por eso, desde entonces, todo atajo nuevo se verifica primero contra los bindings de fábrica antes de asignarse.
**Sin botón físico aparte para Reiniciar, Repetir o el slider** — decisión explícita: esos tres quedan solo en la UI del panel por ahora (ver tabla resumen).

### Locomoción de fábrica (XRI Starter Assets, sin modificar por el proyecto)
**Dónde:** prefab `XR Origin (XR Rig)` (`Assets/Samples/XR Interaction Toolkit/3.6.1/Starter Assets/Prefabs/`), usado en las 3 escenas de contenido.
- **Joystick izquierdo → Move** (caminar), GameObject "Move" bajo "Locomotion".
- **Joystick derecho → Turn/Snap Turn**, GameObject "Turn" bajo "Locomotion".
- **Primary Button derecho ("A") → Jump**.
Estos tres son responsabilidad de Unity/XRI, no de scripts propios del proyecto — se listan aquí
solo para que el cuadro de controles esté completo y no haya sorpresas de "¿por qué el joystick
ya hace algo?". `PauseMenuController` desactiva Move y Turn mientras el menú de pausa está
abierto; `DirectorFreeFlyCamera` desactiva Move (y Gravity) mientras el vuelo libre está activo;
`DirectorViewfinderController` desactiva solo Turn mientras el panel del visor está visible (ver
abajo).

### Cámara virtual del director — Grip (derecho) para mostrar/ocultar, joystick derecho para zoom
**Script:** `DirectorViewfinderController.cs`
**Binding real (Grip):** `<XRController>{RightHand}/{gripButton}`; **binding en el Simulador:** `<XRSimulatedController>{RightHand}/gripButton`
**Binding real (zoom):** `<XRController>{RightHand}/{Primary2DAxis}`; **binding en el Simulador:** `<XRSimulatedController>{RightHand}/primary2DAxis`
**Contexto:** `#if DIRECTOR_BUILD`, solo Ensayo Remoto (`Bootstrap.IsContentScene(...)` y `Bootstrap.IsInspecting == false`). Nunca en Inspección de Escenas ni en ningún menú.
**Comportamiento:** una cámara parentada rígidamente al control derecho (sin offset local) renderiza a un RenderTexture de 640×480, mostrado en un panel World Space Canvas pequeño también parentado al control derecho (posición fija respecto a la mano, pero la rotación se recalcula cada frame para mirar siempre hacia la cabeza del director — ver el comentario de `UpdatePanelFacing()`). Grip alterna la visibilidad del panel (oculto por defecto); mientras está visible, el joystick derecho ajusta el Field of View de esa cámara entre 20° (muy acercado) y 80° (campo amplio), a 30°/s por unidad de deflexión del joystick, con una zona muerta de 0.15 para evitar deriva. Como el joystick derecho es el mismo físico que Turn/snap-turn de fábrica, `Turn` se desactiva mientras el panel está visible y se reactiva al ocultarlo — mismo patrón que `PauseMenuController` ya usa para Move/Turn.
**Capa dedicada (panel):** el panel vive en la capa de proyecto `DirectorViewfinderPanel` (índice 8), excluida del Culling Mask de la cámara del visor — sin esto, la cámara se ve a sí misma mostrando su propio feed indefinidamente (ver commit que agregó esta capa).
**Capa dedicada (rayo del control):** el rayo de ambos controles (`Near-Far Interactor/LineVisual`, de fábrica XRI) vive por defecto en la capa `Default`, visible para toda cámara — incluyendo la del visor, donde aparecía como un destello palpitante. `AssignRayVisualLayer()` lo reasigna a su propia capa de proyecto `ControllerRayVisual` (índice 9, independiente de `DirectorViewfinderPanel`) apenas se construye el visor, y esa capa también se excluye del Culling Mask de la cámara del visor. Solo afecta qué cámaras lo renderizan — no toca el raycasting de interacción de XRI (un sistema de capas aparte), y la cámara principal del director (Culling Mask = Everything) lo sigue viendo con total normalidad fuera del visor.
**Pendiente:** sin guardado de posición/configuración de cámara todavía, y sin otros controles de encuadre más allá del zoom.

---

## Otros hallazgos relevantes (no son controles en sí, pero afectan cómo probarlos)

- **Los menús (Home, Selección de Escenas, panel de reproducción) solo responden a clic de mouse**, vía `StandaloneInputModule` — no hay interacción por rayo/poke de control VR implementada todavía en ninguno de los paneles de este proyecto (Home menus, SceneSelect, menú de pausa, panel de Inspección de Escenas). Esto es una limitación pre-existente, no algo nuevo de esta revisión — queda pendiente si se necesita interacción real por rayo en el headset.
- **Quedan `Debug.Log` temporales** (marcados `// TEMPORARY diagnostic`) en `ActorPositionSync.cs`, `RehearsalSessionSync.cs`, `ActorHomeMenuController.cs` y `RehearsalRecorder.cs`, de rondas de debugging anteriores. No son controles, pero conviene limpiarlos en algún momento — no los toqué porque no fue parte de lo pedido aquí.
- **Ningún control físico de los que agregó este proyecto (Menu, B, joystick izquierdo para vuelo, A izquierdo) se ha probado todavía en un Quest real** — todo lo confirmado hasta ahora fue vía el XR Device Simulator en el Editor.
