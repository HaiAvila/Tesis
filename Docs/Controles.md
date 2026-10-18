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
| Tecla **C** (teclado, **temporal**) | Envía el comando `Calibrate` por el canal de comandos; en el actor solo se registra en el log (`[SessionSync] OnCommand(Calibrate)`) | Build de director, en cualquier momento | `C` | — (solo teclado, para probar) | 🧪 Debug temporal |
| "Tutorial" (menú de pausa) | Repite el tutorial de controles del modo actual (ver "Tutorial de controles" abajo) | Las 3 escenas de contenido, actor y director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado |
| Secondary Button "B" (control derecho) | Inicia/detiene la grabación de la toma | Solo Ensayo Remoto (escena de contenido, no Inspección de Escenas) | Mantener `Y` + `N` | Botón B (control derecho) | ✅ Implementado |
| Secondary Button "B" (control derecho) — **actor** | **Iniciar / Parar** el ensayo de la toma en **Ensayar Solo**: envía el comando `Iniciar` o `Parar` por `SoloSession` y muestra "Iniciando..." / "Detenido" en el mundo (no pegado a la cabeza), que se desvanece en ~1.5 s. No graba nada en el Quest. Si se abre el menú de pausa o se sale de la toma mientras está iniciado, envía `Parar` solo | Solo Ensayar Solo, dentro de una toma, con el menú de pausa cerrado | Mantener `Y` + `N` | Botón B (control derecho) | ✅ Implementado, sin probar en hardware real |
| Joystick izquierdo | Mueve la cámara libremente (vuelo) en la dirección donde mira la cabeza, sin gravedad ni colisión | Solo mientras hay una reproducción activa en Inspección de Escenas | Mantener `T` + `W,A,S,D` | Joystick del control izquierdo | ✅ Implementado, sin probar en hardware real |
| Primary Button izquierdo (botón **X** en Quest) | Play/Pause del fantasma en reproducción (lo mismo que el botón ▶/❚❚ del panel) | Solo mientras hay un panel de reproducción abierto (Inspección de Escenas o "Ver replay" en Ensayo Remoto) | Mantener `T` + `B` | Botón X (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Play/Pause (▶ / ❚❚) | Pausa o reanuda la reproducción; el ícono muestra siempre el estado real (▶ = en pausa, ❚❚ = reproduciendo), venga el cambio del botón, de X izquierdo, del slider o del final de la toma | Panel de reproducción, primer botón | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| Reiniciar (↺) | Salta al inicio de la reproducción y reanuda | Panel de reproducción (Inspección de Escenas o "Ver replay") | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Repetir (∞) | Activa/desactiva loop automático al llegar al final | Panel de reproducción (Inspección de Escenas o "Ver replay") | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Slider de scrubbing | Salta a cualquier punto de la reproducción, arrastrando. Al soltarlo queda **en pausa** en ese punto, aunque antes estuviera reproduciendo; se reanuda con ▶ o X izquierdo | Panel de reproducción (Inspección de Escenas o "Ver replay") | Clic + arrastre con mouse (Game view) | Apuntar con el rayo, mantener gatillo y arrastrar | ✅ Decidido: solo UI |
| Casillas "Marcar como preferida" / "Marcar como favorita" | Al marcarlas antes de "Guardar", la nueva sesión se guarda con `es_preferida`/`es_favorita` ya en el mismo INSERT (preferida desmarca cualquier otra de la misma toma) | Panel de decisión (Ver replay/Guardar/Descartar) al detener una grabación — solo director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Estrella (★/☆) y corazón (♥/♡) por sesión | Marca/desmarca preferida (exclusiva por toma) y favorita (no exclusiva, bookmark) de una sesión ya guardada | Lista de sesiones en Selección de Escenas — solo aparece cuando un shot tiene más de una sesión guardada, durante Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| "Ver" en Favoritos | Salta directo a reproducir esa sesión en Inspección de Escenas, interrumpiendo cualquier Ensayo Remoto activo | Pantalla Favoritos (ver entrada "Favoritos" del menú de pausa) | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Joystick izquierdo (locomoción normal) | Caminar | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto o el vuelo libre está activo | Mantener `T`, mover joystick | Joystick del control izquierdo | ✅ De fábrica (XRI Starter Assets) |
| Joystick derecho | Girar (turn/snap-turn) | Cualquier escena de contenido, excepto mientras el menú de pausa está abierto **o el visor de la cámara virtual del director está visible** | Mantener `Y`, mover joystick | Joystick del control derecho | ✅ De fábrica (XRI Starter Assets) |
| Primary Button "A" (control derecho) | Jump | Cualquier escena de contenido | Mantener `Y` + `B` | Botón A (control derecho) | ✅ De fábrica (XRI Starter Assets) — **este es el motivo por el que el atajo de Play/Pause se puso en el control izquierdo y no en el derecho** |
| Grip (control derecho) — toque | Muestra/oculta el panel de la cámara virtual del director ("visor", como levantar un celular para mirarlo) | Solo Ensayo Remoto (`#if DIRECTOR_BUILD`, escena de contenido, `Bootstrap.Mode == Remote`) | Mantener `Y` + `G` (toque, sin mover el joystick) | Grip (control derecho) | ✅ Implementado, sin probar en hardware real |
| Joystick derecho izq/der (sin Grip) | Cicla entre distancias focales con nombre: 24/35/50/85mm/135mm, de más ancho a más acercado | Solo mientras el panel del visor está visible | Mantener `Y` + `A/D` | Joystick del control derecho | ✅ Implementado, sin probar en hardware real |
| Grip (control derecho) — mantener + joystick arriba/abajo | Ajuste fino continuo de la distancia focal alrededor del valor actual (no cicla entre los nombrados): **arriba = más zoom**, abajo = menos zoom | Solo mientras el panel del visor está visible | Mantener `Y` + `G` + `W/S` | Grip + joystick del control derecho | ✅ Implementado, sin probar en hardware real |

---

## Detalle por control

### Menú de pausa — botón Menu (izquierdo)
**Script:** `PauseMenuController.cs`
**Binding real:** `<XRController>{LeftHand}/menu`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/menuButton` (nombre distinto al real — por eso hace falta un binding aparte solo para el simulador)
**Contexto:** activo en `Scene1_mountain`, `Scene2_spaceship`, `Scene3_apartment` — sin distinguir si es Ensayo Remoto o Inspección de Escenas, ni si es build de actor o de director. Ambos ven el panel; solo el director ve las opciones "Selección de Escenas" y "Favoritos" dentro de él (gateadas por `DIRECTOR_BUILD`).
**Efecto adicional:** pausa las GameObjects "Move" y "Turn" (locomoción por joystick) mientras está abierto; nunca toca tracking de cabeza ni gravedad. Dispara un pulso háptico al abrir y al cerrar.
**Como pausa (PanelFocusManager):** mientras el menú de pausa (o Favoritos, o el panel de fin de grabación) está abierto, el mundo se **oscurece** con una caja oscura semitransparente de 2 m alrededor de la cabeza: el menú queda adelante y brillante, y el set se ve atenuado detrás. Además, **se ocultan los paneles persistentes**: el panel de reproducción, el monitor de la cámara grabada y el visor. No se pueden tocar con el rayo, y al cerrar vuelven exactamente como estaban (si el visor estaba bajado, sigue bajado). Si había una **reproducción corriendo, se pausa**, y al cerrar se reanuda solo si estaba corriendo. Mientras tanto se ignoran X izquierdo (play/pausa), el vuelo libre y el Grip y el zoom del visor. El tutorial no se oculta: se acomoda al costado o arriba del menú. El mundo no se congela: en Ensayo Remoto el otro dispositivo sigue en movimiento.
**"Favoritos" (nueva entrada, solo director):** abre `FavoritesPanelController.cs`, un panel aparte (no navega a otra escena) con la lista de toda sesión marcada como favorita en cualquier escena/shot. Disponible desde cualquier escena de contenido, incluso en medio de una sesión de Ensayo Remoto activa — al elegir "Ver" en una entrada, salta directo a Inspección de Escenas para esa sesión exacta, interrumpiendo el ensayo en curso si había uno.

### Grabación — Secondary Button "B" (derecho)
**Script:** `RehearsalRecorder.cs`
**Binding real:** `<XRController>{RightHand}/{secondaryButton}`
**Binding en el Simulador:** `<XRSimulatedController>{RightHand}/secondaryButton`
**Contexto:** `#if DIRECTOR_BUILD` (solo existe en el build de director) y `Bootstrap.IsContentScene(...)`.
**Solo Ensayo Remoto:** el gate revisa que sea una escena de contenido **y** que el modo sea `Remote` (`Bootstrap.Mode`). Antes solo revisaba lo primero, así que B también "grababa" en Inspección de Escenas, donde no hay un actor en vivo.

### Vuelo libre del director — joystick izquierdo
**Script:** `DirectorFreeFlyCamera.cs`, activado/desactivado por `RecordingPlaybackController.cs`
**Binding real:** `<XRController>{LeftHand}/{Primary2DAxis}`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/primary2DAxis`
**Contexto:** `EnableFly()`/`DisableFly()` solo se llaman desde `RecordingPlaybackController.LoadAndStart()`/`CleanUp()` — es decir, **el vuelo libre solo existe durante Inspección de Escenas**. No está implementado para Ensayo Remoto.
**📌 Aclaración explícita pedida:** si en algún momento se planeó (o se quiere a futuro) que el director también pueda volar libremente durante Ensayo Remoto mientras dirige en vivo, **eso no está construido** — es un pendiente separado, no algo que ya funcione ahí solo porque funciona en Inspección de Escenas.
**Comportamiento:** mientras está activo, desactiva el `CharacterController` y el GameObject "Gravity" (sin colisión de piso, sin gravedad) pero deja "Turn" intacto. El movimiento sigue la dirección real hacia donde mira la cabeza (adelante/atrás/lateral, incluyendo arriba/abajo), no un plano horizontal fijo.
**Cómo probarlo en el Simulador:** mantén `T` (selecciona el control izquierdo) y usa `W,A,S,D` — mientras `T` está sostenido, esas mismas teclas alimentan el eje 2D ("Axis 2D") de ese control en vez de mover el HMD. Para ver el efecto del vuelo (movimiento en la dirección donde miras, incluyendo arriba/abajo) hace falta soltar `T` momentáneamente y usar el movimiento del HMD (`W,S,A,D,Q,E` sin mantener `T`/`Y`) para apuntar la cabeza hacia arriba o abajo antes de volver a sostener `T` y mover.

### Play/Pause de la reproducción — Primary Button izquierdo (botón X en Quest)
**Nombre del botón:** en el Quest, los botones del control izquierdo son **X** (primary) e **Y** (secondary), y los del derecho **A** (primary) y **B** (secondary). Versiones anteriores de este documento llamaban "A izquierdo" a este botón; físicamente es la **X**.
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
**Contexto:** `#if DIRECTOR_BUILD`, solo Ensayo Remoto (`Bootstrap.IsContentScene(...)` y `Bootstrap.Mode == Remote`). Nunca en Inspección de Escenas ni en ningún menú.
**Comportamiento general:** una cámara parentada rígidamente al control derecho (sin offset local) renderiza a un RenderTexture de 640×480, mostrado en un panel World Space Canvas pequeño también parentado al control derecho (posición fija respecto a la mano, pero la rotación se recalcula cada frame para mirar siempre hacia la cabeza del director — ver el comentario de `UpdatePanelFacing()`). `Turn` se desactiva mientras el panel está visible y se reactiva al ocultarlo (mismo joystick físico que el encuadre) — mismo patrón que `PauseMenuController` ya usa para Move/Turn.
**Grip — toque vs. mantener:** Grip ahora es de doble propósito. Un toque simple (sin mover el joystick mientras está presionado) alterna la visibilidad del panel, igual que antes — pero la decisión se toma recién al **soltar** Grip (`OnGripReleased`), no al presionarlo, para poder distinguir un toque de un ajuste fino. Si el joystick se movió en el eje Y mientras Grip estaba sostenido, ese gesto se interpreta como ajuste fino (ver abajo) y soltar Grip **no** alterna la visibilidad.
**Encuadre — vocabulario real de distancias focales:** el zoom ya no es un FOV crudo, es `focalLength` (mm) sobre el modelo de cámara física de Unity (`usePhysicalProperties`, sensor 36×24mm tipo full-frame 35mm, `gateFit = Horizontal`) — el Field of View resultante lo calcula Unity mismo a partir del focal length, no una fórmula propia.
- **Joystick izq/der, sin Grip:** cicla entre distancias con nombre — 24/35/50/85/135mm — con disparo por umbral e histéresis (dispara pasando 0.6, no vuelve a armarse hasta volver bajo 0.25) para que un solo golpe de joystick sea un solo paso. El paso siguiente/anterior se calcula desde el nombrado más cercano al valor actual (que puede estar ajustado fino), y se detiene en los extremos (no cicla de 135 a 24).
- **Grip sostenido + joystick arriba/abajo:** ajuste fino continuo alrededor del valor actual — arriba alarga la distancia focal (más zoom, como el lado "tele" de un zoom), abajo la acorta (menos zoom) — a 40mm/s por unidad de deflexión, zona muerta 0.15. Mientras Grip está sostenido el eje X se ignora por completo (todo el joystick queda reservado para el ajuste fino); al soltar Grip sin haber usado el eje Y, se interpreta como el toque normal de arriba.
- El valor actual se muestra siempre en un chip en la esquina inferior izquierda del feed (p. ej. "50mm" o "47mm" si está ajustado fino), actualizado por `UpdateFocalLengthLabel()` cada vez que cambia.
**Líneas de encuadre:** superpuestas directo en el Canvas sobre el feed (no quedan grabadas en la imagen que renderiza la cámara) — guía de tercios (2 líneas horizontales + 2 verticales, blancas semitransparentes) y dos marcas horizontales amarillas indicando dónde caerían los bordes superior/inferior de un recorte 16:9 centrado (el feed en sí es 4:3). Todo con `raycastTarget = false` para no interferir con los clics de la barra de botones.
**Capa dedicada (panel):** el panel vive en la capa de proyecto `DirectorViewfinderPanel` (índice 8), excluida del Culling Mask de la cámara del visor — sin esto, la cámara se ve a sí misma mostrando su propio feed indefinidamente (ver commit que agregó esta capa).
**Capa dedicada (rayo del control):** los rayos de ambos controles (de fábrica XRI) viven por defecto en la capa `Default`, visible para toda cámara — incluyendo la del visor, donde aparecían en el feed. Cada control tiene **dos**: el de `Near-Far Interactor/LineVisual` y el del `Teleport Interactor` (aparece al empujar el joystick hacia arriba; ese era el que seguía viéndose en el feed al hacer zoom). `XRUISetup.MoveControllerRaysToOwnLayer()` pasa todos los `LineRenderer` de ambos controles a su propia capa de proyecto `ControllerRayVisual` (índice 9, independiente de `DirectorViewfinderPanel`) apenas se construye el visor, y esa capa se excluye del Culling Mask de la cámara del visor (y de la cámara que reproduce el track grabado). Solo afecta qué cámaras lo renderizan — no toca el raycasting de interacción de XRI (un sistema de capas aparte), y la cámara principal del director (Culling Mask = Everything) lo sigue viendo con total normalidad fuera del visor.
**Sin botones:** el visor es solo para mirar (sin `GraphicRaycaster`). Tenía una barra con 💾 (guardar framing) y 📂 (lista de framings con "Cargar"), que se quitó: el *poke interactor* del control derecho (la punta del control, que aprieta botones por contacto) quedaba justo debajo de la barra y la apretaba solo con mover la mano. La tabla `CameraFramings` sigue en la base de datos (no se borró nada guardado), pero ya nada la lee ni la escribe.
**Pendiente:** sin otros controles de encuadre más allá del zoom (p. ej. roll, offset lateral).
**El feed muestra solo la escena:** además del propio panel y los rayos, la cámara del visor excluye la capa `UI` (donde quedan, vía `XRUISetup.MoveToUILayer`, el menú de pausa, el panel de reproducción, el popup de detener grabación, Favoritos y el indicador REC que sigue a la cabeza) y la capa `PlaybackMonitor`. Antes se veía, por ejemplo, el panel de reproducción dentro del feed (panel dentro del panel). La cámara que reproduce el track grabado excluye lo mismo.
**Badge "● REC":** mientras se graba, el visor muestra un badge rojo "● REC" arriba a la izquierda del feed, dibujado sobre el panel (como la pantalla de una cámara real), así que no queda en la imagen. Reemplaza al indicador REC que antes aparecía flotando dentro del feed.
**Teleport pausado mientras el visor está visible:** en el control derecho, joystick arriba también es el "Teleport Mode" de fábrica, así que hacer zoom mostraba el rayo de teleport. Mientras el visor está visible se desactiva el `ControllerInputActionManager` del control derecho (igual que se pausa Turn); al ocultar el visor vuelve todo a la normalidad. Las escenas no tienen zonas de teleport, así que de todos modos nunca teletransportaba; solo aparecía el rayo.

### Interacción con los paneles — rayo del control
**Scripts:** `XRUISetup.cs` (helper compartido), usado por cada panel al construirse.
**Cómo presionar:** apunta el rayo del control (izquierdo o derecho) al botón y presiona el **gatillo**. Para el slider de reproducción y las listas con scroll (Selección de Escenas, Favoritos): apunta, mantén el gatillo y arrastra.
**En el Editor:** el clic con mouse en la vista Game sigue funcionando. Con el XR Device Simulator, el gatillo es el clic izquierdo del mouse mientras se mantiene `T` (izquierdo) o `Y` (derecho) para elegir el control.
**Cómo está armado:**
- El EventSystem usa `XRUIInputModule` (de XRI) en vez de `StandaloneInputModule`. Es uno solo y persistente (`DontDestroyOnLoad`); los Near-Far Interactor del rig (que ya traen la interacción con UI activada de fábrica) se registran solos. Con la configuración del proyecto (input "Both"), el módulo usa sus acciones de mouse integradas, por eso el clic con mouse sigue andando.
- Cada canvas con botones tiene `TrackedDeviceGraphicRaycaster` además de su `GraphicRaycaster`. Conviven a propósito: XRI manda los eventos del rayo con una posición fuera de pantalla para que el `GraphicRaycaster` los ignore, y el raycaster de tracked devices ignora el mouse.
- Paneles con rayo: home de actor, home de director, Selección de Escenas, menú de pausa, panel de reproducción, popup de detener grabación y Favoritos. Sin rayo (solo muestran, no tienen botones): el visor, el monitor de la cámara grabada y el indicador REC.
- **Selección de Escenas** antes era Screen Space Overlay (no se ve en el headset) y su escena no tenía rig XR. Ahora se carga encima de `MenuBackdrop` (como los home menus, vía `Bootstrap.LoadSceneSelect()`) y es World Space, a 2 m delante de la cabeza.
- **Capas:** el rayo solo detecta UI en las capas de su raycast mask (Default + UI en el rig). Los paneles con botones están en esas capas. Los rayos visuales están en `ControllerRayVisual`, excluida de la cámara del visor y de la que reproduce el track grabado.
- **Íconos:** la estrella, el corazón y ▶/❚❚ son imágenes generadas en código (`UIIcons.cs`), no caracteres de la fuente: con la fuente, el ♥ relleno no aparecía y el ❚❚ se veía como un solo bloque.

### Ensayar Solo (actor) — modo propio
**Scripts:** `SoloRehearsalController.cs`, `SoloSession.cs` / `SoloSessionModel.cs`, `ApprovedReferences.cs`, `SoloPods.cs`.
**Modo:** `Bootstrap.Mode == Solo`, que se activa con el botón "Ensayar Solo" y vuelve a `None` con el menú de pausa → Menú Principal. Es un modo propio: ya no comparte la bandera de Inspección. En solo el actor ve **solo el set**: no hay panel de reproducción, ni fantasma, ni cámara grabada, ni lista de sesiones con estrella o corazón. Y **el director no lo afecta**: los cambios de toma, el fin del ensayo, la desconexión del director y los comandos del director se ignoran.
**Selección de Escenas:** solo tomas sueltas, y solo las que `ApprovedReferences.HasApprovedReference(escena, toma)` acepta. Por ahora acepta todas; más adelante le preguntará al servidor.
**Marca de inicio:** una "T" de cinta amarilla en el piso, en `Shot{N}_Start`. La barra va donde quedan las puntas de los pies; el palo va hacia atrás, entre los pies.
**Iniciar / Parar:** botón **B** del control derecho. La **A** derecha está tomada por el salto de fábrica, y B es el mismo botón con el que el director graba. Ver la fila "Secondary Button B — actor" en la tabla.
**Sincronización:** el actor es dueño de `SoloSessionModel` mientras está en solo. Al cargar una toma escribe modo, escena y toma; Iniciar y Parar viajan como comandos (contador + baseline). Al salir de solo lo deja vacío y suelta la propiedad. Todavía nada reacciona a esos comandos: es para el servidor de PC. Logs con el prefijo `[Solo]`.
**Pods:** `SoloPods.GuideToMark` y `SoloPods.OnPodStatus` son ganchos vacíos, sin interfaz, para cuando existan los pods y el servidor.

### Tutorial de controles
**Script:** `TutorialController.cs` (persistente, objeto "Tutorial" bajo Bootstrap).
**Cuándo aparece:** la primera vez que se entra a una escena de contenido en cada modo, un panel pregunta "¿Quieres ver un tutorial de los controles?" (Sí / No). La respuesta se guarda en el headset (`PlayerPrefs`, una clave por modo: `Tutorial.Seen.DirectorRemote`, `DirectorInspection`, `ActorRemote`, `ActorSolo`), así que no vuelve a preguntar. Se puede repetir siempre desde el menú de pausa → **Tutorial** (entra directo a los pasos, sin preguntar).
**Cómo funciona:** un paso por vez. Los pasos que piden una acción avanzan solos cuando se detecta (muestran "¡Muy bien!" y un pulso háptico); los pasos informativos tienen **Siguiente**. **Saltar tutorial** está siempre. En el Editor, cada paso muestra también las teclas del XR Device Simulator.
**Pasos por modo:**
- **Director — Ensayo Remoto (10):** rayo + gatillo · caminar · girar · Grip para levantar el visor · cambiar de lente · zoom fino · B para grabar · B para detener · elegir Ver replay / Guardar / Descartar (espera a que se guarde o descarte, aunque se pase por Ver replay) · Menu.
- **Director — Inspección de Escenas (7):** rayo + gatillo · pausar/reanudar (botón o X izquierdo) · arrastrar la barra · ↺ / ∞ / ▣ · estrella y corazón · volar con el joystick izquierdo · Menu. Los pasos del panel de reproducción solo aparecen si el shot tiene una grabación.
- **Actor — Ensayo Remoto (5):** rayo + gatillo · caminar · girar · el director elige la toma · Menu.
- **Actor — Ensayar Solo (7):** rayo + gatillo · caminar · girar · la marca de cinta en el piso · B para iniciar · B para detener · Menu.
**Texto:** en español de México (tú). En cada paso, el control que hay que presionar está resaltado en **ámbar** y lo que hace en **celeste** (negritas).
**Panel:** arriba a la izquierda de la vista, sigue a la cabeza con retraso (como el panel de reproducción). Nunca queda encima de otro panel con botones (menú de pausa, panel de fin de grabación, Favoritos, panel de reproducción): si lo taparía, se mueve a la izquierda o arriba de ese panel, lo que implique moverse menos (arriba solo si no queda más alto de ~22°, porque mirar hacia arriba cansa más). En la práctica, el panel de fin de grabación (ancho y bajo) lo manda arriba y el menú de pausa (alto) lo manda a la izquierda. Al cerrarse ese panel, vuelve a su lugar. Solo sus botones atrapan el rayo (fondo y texto no). Está en la capa `UI`, así que no aparece en el feed del visor. Posición provisoria, sin ajustar.
**Para volver a ver la pregunta en el Editor:** borrar esas claves de `PlayerPrefs` (Edit → Clear All PlayerPrefs borra todas).

---

## Otros hallazgos relevantes (no son controles en sí, pero afectan cómo probarlos)

- **Probado en un Quest 3 real solo el build de actor** (rayo con los paneles, menú de pausa, caminar, seguir al director a la toma). Los controles del director (B, Grip, zoom, X izquierdo, vuelo) solo se probaron en el Editor con el XR Device Simulator.
