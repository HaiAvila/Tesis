# Controles

Este documento se generó revisando directamente el código (`Assets/Scripts/*.cs`), no de memoria.
Cada control indica el script exacto donde vive, para que sea fácil de re-verificar si el
comportamiento cambia.

## Cómo probar en el Editor (XR Device Simulator)

Todos los controles de mano de este documento se prueban con el **XR Device Simulator**, ya
presente en las 3 escenas de contenido. Tiene el tag `EditorOnly`, así que Unity lo quita de
los builds: en el Quest no existe (si estuviera, reemplazaría el headset real por uno simulado
quieto y la vista dejaría de seguir la cabeza). Antes de presionar cualquier tecla de control, hay que
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
| Reiniciar (↺) | Salta al inicio de la reproducción y reanuda | Solo Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Repetir (∞) | Activa/desactiva loop automático al llegar al final | Solo Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Slider de scrubbing | Salta a cualquier punto de la reproducción, arrastrando | Solo Inspección de Escenas | Clic + arrastre con mouse (Game view) | Apuntar con el rayo, mantener gatillo y arrastrar | ✅ Decidido: solo UI |
| Casillas "Marcar como preferida" / "Marcar como favorita" | Al tildarlas antes de "Guardar", la nueva sesión se guarda con `es_preferida`/`es_favorita` ya en el mismo INSERT (preferida desmarca cualquier otra de la misma toma) | Panel de decisión (Ver replay/Guardar/Descartar) al detener una grabación — solo director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Estrella (★/☆) y corazón (♥/♡) por sesión | Marca/desmarca preferida (exclusiva por toma) y favorita (no exclusiva, bookmark) de una sesión ya guardada | Lista de sesiones en Selección de Escenas — solo aparece cuando un shot tiene más de una sesión guardada, durante Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| "Ver" en Favoritos | Salta directo a reproducir esa sesión en Inspección de Escenas, interrumpiendo cualquier Ensayo Remoto activo | Pantalla Favoritos (ver entrada "Favoritos" del menú de pausa) | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Joystick izquierdo (locomoción normal) | Caminar | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto o el vuelo libre está activo | Mantener `T`, mover joystick | Joystick del control izquierdo | ✅ De fábrica (XRI Starter Assets) |
| Joystick derecho | Girar (turn/snap-turn) | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto **o el visor de la cámara virtual del director está visible** | Mantener `Y`, mover joystick | Joystick del control derecho | ✅ De fábrica (XRI Starter Assets) |
| Primary Button "A" (control derecho) | Jump | Cualquier escena de contenido | Mantener `Y` + `B` | Botón A (control derecho) | ✅ De fábrica (XRI Starter Assets) — **este es el motivo por el que el atajo de Play/Pause se puso en el control izquierdo y no en el derecho** |
| Grip (control derecho) — toque | Muestra/oculta el panel de la cámara virtual del director ("visor", como levantar un celular para mirarlo) | Solo Ensayo Remoto (`#if DIRECTOR_BUILD`, escena de contenido, `IsInspecting == false`) | Mantener `Y` + `G` (toque, sin mover el joystick) | Grip (control derecho) | ✅ Implementado, sin probar en hardware real |
| Joystick derecho izq/der (sin Grip) | Cicla entre distancias focales con nombre: 24/35/50/85mm/135mm, de más ancho a más acercado | Solo mientras el panel del visor está visible | Mantener `Y` + `A/D` | Joystick del control derecho | ✅ Implementado, sin probar en hardware real |
| Grip (control derecho) — mantener + joystick arriba/abajo | Ajuste fino continuo de la distancia focal alrededor del valor actual (no cicla entre los nombrados) | Solo mientras el panel del visor está visible | Mantener `Y` + `G` + `W/S` | Grip + joystick del control derecho | ✅ Implementado, sin probar en hardware real |
| 💾 Guardar framing | Guarda posición/rotación/distancia focal actuales de la cámara virtual (en espacio de mundo) para el shot activo | Panel del visor, botón en la barra inferior | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| 📂 Framings | Muestra/oculta la lista corta de framings guardados del shot activo, con botón "Cargar" por fila | Panel del visor, botón en la barra inferior | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |

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

### Cámara virtual del director — Grip (derecho) para mostrar/ocultar y para ajuste fino, joystick derecho para encuadre
**Script:** `DirectorViewfinderController.cs`
**Binding real (Grip):** `<XRController>{RightHand}/{gripButton}`; **binding en el Simulador:** `<XRSimulatedController>{RightHand}/gripButton`
**Binding real (joystick):** `<XRController>{RightHand}/{Primary2DAxis}`; **binding en el Simulador:** `<XRSimulatedController>{RightHand}/primary2DAxis`
**Contexto:** `#if DIRECTOR_BUILD`, solo Ensayo Remoto (`Bootstrap.IsContentScene(...)` y `Bootstrap.IsInspecting == false`). Nunca en Inspección de Escenas ni en ningún menú.
**Comportamiento general:** una cámara parentada rígidamente al control derecho (sin offset local) renderiza a un RenderTexture de 640×480, mostrado en un panel World Space Canvas pequeño también parentado al control derecho (posición fija respecto a la mano, pero la rotación se recalcula cada frame para mirar siempre hacia la cabeza del director — ver el comentario de `UpdatePanelFacing()`). `Turn` se desactiva mientras el panel está visible y se reactiva al ocultarlo (mismo joystick físico que el encuadre) — mismo patrón que `PauseMenuController` ya usa para Move/Turn.
**Grip — toque vs. mantener:** Grip ahora es de doble propósito. Un toque simple (sin mover el joystick mientras está presionado) alterna la visibilidad del panel, igual que antes — pero la decisión se toma recién al **soltar** Grip (`OnGripReleased`), no al presionarlo, para poder distinguir un toque de un ajuste fino. Si el joystick se movió en el eje Y mientras Grip estaba sostenido, ese gesto se interpreta como ajuste fino (ver abajo) y soltar Grip **no** alterna la visibilidad.
**Encuadre — vocabulario real de distancias focales:** el zoom ya no es un FOV crudo, es `focalLength` (mm) sobre el modelo de cámara física de Unity (`usePhysicalProperties`, sensor 36×24mm tipo full-frame 35mm, `gateFit = Horizontal`) — el Field of View resultante lo calcula Unity mismo a partir del focal length, no una fórmula propia.
- **Joystick izq/der, sin Grip:** cicla entre distancias con nombre — 24/35/50/85/135mm — con disparo por umbral e histéresis (dispara pasando 0.6, no vuelve a armarse hasta volver bajo 0.25) para que un solo golpe de joystick sea un solo paso. El paso siguiente/anterior se calcula desde el nombrado más cercano al valor actual (que puede estar ajustado fino), y se detiene en los extremos (no cicla de 135 a 24).
- **Grip sostenido + joystick arriba/abajo:** ajuste fino continuo alrededor del valor actual, a 40mm/s por unidad de deflexión, zona muerta 0.15. Mientras Grip está sostenido el eje X se ignora por completo (todo el joystick queda reservado para el ajuste fino); al soltar Grip sin haber usado el eje Y, se interpreta como el toque normal de arriba.
- El valor actual se muestra siempre en un chip en la esquina inferior izquierda del feed (p. ej. "50mm" o "47mm" si está ajustado fino), actualizado por `UpdateFocalLengthLabel()` cada vez que cambia.
**Líneas de encuadre:** superpuestas directo en el Canvas sobre el feed (no quedan grabadas en la imagen que renderiza la cámara) — guía de tercios (2 líneas horizontales + 2 verticales, blancas semitransparentes) y dos marcas horizontales amarillas indicando dónde caerían los bordes superior/inferior de un recorte 16:9 centrado (el feed en sí es 4:3). Todo con `raycastTarget = false` para no interferir con los clics de la barra de botones.
**Capa dedicada (panel):** el panel vive en la capa de proyecto `DirectorViewfinderPanel` (índice 8), excluida del Culling Mask de la cámara del visor — sin esto, la cámara se ve a sí misma mostrando su propio feed indefinidamente (ver commit que agregó esta capa).
**Capa dedicada (rayo del control):** el rayo de ambos controles (`Near-Far Interactor/LineVisual`, de fábrica XRI) vive por defecto en la capa `Default`, visible para toda cámara — incluyendo la del visor, donde aparecía como un destello palpitante. `AssignRayVisualLayer()` lo reasigna a su propia capa de proyecto `ControllerRayVisual` (índice 9, independiente de `DirectorViewfinderPanel`) apenas se construye el visor, y esa capa también se excluye del Culling Mask de la cámara del visor. Solo afecta qué cámaras lo renderizan — no toca el raycasting de interacción de XRI (un sistema de capas aparte), y la cámara principal del director (Culling Mask = Everything) lo sigue viendo con total normalidad fuera del visor.
**Framings guardados (💾 / 📂):** debajo del feed, una barra con dos botones (rayo del control izquierdo + gatillo, o clic con mouse en el Editor — ver "Interacción con los paneles" más abajo). 💾 guarda `CameraFraming` (posición/rotación/distancia focal de la cámara virtual, **en espacio de mundo**, no relativos al control) asociado a `Bootstrap.CurrentShotNumber`, en la tabla `CameraFramings` (columna `focal_length_mm`, agregada vía `ALTER TABLE` seguro sobre la tabla que ya existía; `fov` se conserva solo como dato informativo/legado). 📂 abre/cierra una lista corta (sin límite ni scroll todavía) de los framings guardados del shot activo, más reciente primero, cada uno con fecha/hora y un botón "Cargar". Al cargar uno, la cámara salta a esa posición/rotación/distancia focal y **se bloquea ahí** (`isLocked`) ignorando el movimiento de la mano, hasta que el control derecho se mueva más de 3cm o gire más de 5° respecto a su pose en el momento de cargar — en ese punto se suelta el bloqueo y la cámara vuelve a pegarse a la mano desde su pose actual. La lista también sigue al control (como el panel principal) y su propia rotación se recalcula cada frame para mirar al director.
**Pendiente:** sin otros controles de encuadre más allá del zoom (p. ej. roll, offset lateral).
**Rayo mientras el visor está levantado:** el panel flota unos centímetros delante del control derecho, así que el rayo derecho siempre le estaría pegando (y el gatillo derecho apretaría 💾 sin querer). Mientras el visor está visible, la interacción con UI del control **derecho** se desactiva (`SetRightHandUIInteraction`), igual que se pausa Turn; los botones del visor y la lista de framings se aprietan con el rayo del control **izquierdo**. Al ocultar el visor, el rayo derecho vuelve a funcionar con los paneles.

### Interacción con los paneles — rayo del control
**Scripts:** `XRUISetup.cs` (helper compartido), usado por cada panel al construirse.
**Cómo apretar:** apuntá el rayo del control (izquierdo o derecho) al botón y presioná el **gatillo**. Para el slider de reproducción y las listas con scroll (Selección de Escenas, Favoritos): apuntá, mantené el gatillo y arrastrá.
**En el Editor:** el clic con mouse en la vista Game sigue funcionando. Con el XR Device Simulator, el gatillo es el clic izquierdo del mouse mientras se mantiene `T` (izquierdo) o `Y` (derecho) para elegir el control.
**Cómo está armado:**
- El EventSystem usa `XRUIInputModule` (de XRI) en vez de `StandaloneInputModule`. Es uno solo y persistente (`DontDestroyOnLoad`); los Near-Far Interactor del rig (que ya traen la interacción con UI activada de fábrica) se registran solos. Con la configuración del proyecto (input "Both"), el módulo usa sus acciones de mouse integradas, por eso el clic con mouse sigue andando.
- Cada canvas con botones tiene `TrackedDeviceGraphicRaycaster` además de su `GraphicRaycaster`. Conviven a propósito: XRI manda los eventos del rayo con una posición fuera de pantalla para que el `GraphicRaycaster` los ignore, y el raycaster de tracked devices ignora el mouse.
- Paneles con rayo: home de actor, home de director, Selección de Escenas, menú de pausa, panel de reproducción, popup de detener grabación, Favoritos, visor y lista de framings. Sin rayo (solo muestran, no tienen botones): el monitor de la cámara grabada y el indicador REC.
- **Selección de Escenas** antes era Screen Space Overlay (no se ve en el headset) y su escena no tenía rig XR. Ahora se carga encima de `MenuBackdrop` (como los home menus, vía `Bootstrap.LoadSceneSelect()`) y es World Space, a 2 m delante de la cabeza.
- **Capas:** el rayo solo detecta UI en las capas de su raycast mask (Default + UI en el rig). Los dos canvas del visor están en `DirectorViewfinderPanel`, así que esa capa se agrega a la mask de ambos controles al construirse el visor. El rayo visual (`LineVisual`) sigue en `ControllerRayVisual`, excluida de la cámara del visor — la interacción con UI no agrega objetos visuales nuevos (no hay retícula), así que el destello no vuelve.

---

## Otros hallazgos relevantes (no son controles en sí, pero afectan cómo probarlos)

- **Quedan `Debug.Log` temporales** (marcados `// TEMPORARY diagnostic`) en `ActorPositionSync.cs`, `RehearsalSessionSync.cs`, `ActorHomeMenuController.cs` y `RehearsalRecorder.cs`, de rondas de debugging anteriores. No son controles, pero conviene limpiarlos en algún momento — no los toqué porque no fue parte de lo pedido aquí.
- **Ningún control físico de los que agregó este proyecto (Menu, B, joystick izquierdo para vuelo, A izquierdo, Grip, ni el rayo con los paneles) se ha probado todavía en un Quest real** — todo lo confirmado hasta ahora fue vía el XR Device Simulator en el Editor.
