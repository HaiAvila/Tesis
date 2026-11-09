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
| Secondary Button izquierdo (botón **Y**) — **actor** | **Calibrar**: la máquina que tiene los pods inicia la calibración del avatar — pose T, brazos abajo con sentadilla ligera, brazos al frente con las palmas juntas — y el panel muestra cada pose. No funciona durante la toma ("No puedes calibrar durante la toma"); sin pods avisa "No hay pods conectados" | Actor, en una escena de contenido (Ensayo Remoto y Ensayar Solo), sin un menú abierto | Mantener `T` + `N` | Botón Y (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| "Calibrar" (menú de pausa) — **director** | Envía el comando `Calibrate`: la máquina que tiene los pods (la misma del director, o la del actor en Ensayo Remoto) inicia la calibración del avatar — pose T, brazos abajo con sentadilla ligera, brazos al frente con las palmas juntas — y cada visor muestra la pose que sigue. Sin pods solo se registra en el log | Director, en Ensayo Remoto, sin grabar. Sin pods avisa "No hay pods conectados" | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| "Micrófono" (menú de pausa; la descripción dice si está encendido o apagado) | Enciende o apaga tu micrófono. Se recuerda en el dispositivo. Ver "Voz" abajo | Ensayo Remoto, actor y director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| "Notas del shot" (menú de pausa) — **director** | Abre las notas de voz de la toma abierta y las generales del shot, aunque el shot no tenga tomas | Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| "Exportar tomas a CSV" (menú de pausa) — **director** | Guarda las tomas nuevas en archivos CSV. Ver "Exportar tomas" abajo | Inspección de Escenas | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| Botón **Calibrar** (Inspector de `Bootstrap → ActorAvatar`) | Inicia la misma calibración en esta máquina, si tiene pods | Editor en Play mode, cualquier rol | — | — | 🧪 Para probar |
| "Estado de pods" (menú de pausa) | Muestra cada pod por parte del cuerpo: conexión, batería y señal (ver "Estado de los pods" abajo) | Las 3 escenas de contenido, actor y director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| "Tutorial" (menú de pausa) | Repite el tutorial de controles del modo actual (ver "Tutorial de controles" abajo) | Las 3 escenas de contenido, actor y director | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado |
| Secondary Button "B" (control derecho) | Inicia/detiene la grabación de la toma | Solo Ensayo Remoto (escena de contenido, no Inspección de Escenas) | Mantener `Y` + `N` | Botón B (control derecho) | ✅ Implementado |
| Secondary Button izquierdo (botón **Y**) — **director** | **Siguiente toma**: cierra el shot actual y abre el siguiente dentro de la grabación de una escena completa (`ShotBoundaries`), manda el comando `SiguienteToma` a todos y muestra "Siguiente toma: shot N". Es opcional. En el último shot no hace nada y muestra "Última toma de la escena" | Solo mientras se graba una **escena completa** en Ensayo Remoto, con el menú de pausa cerrado | Mantener `T` + `N` | Botón Y (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Secondary Button "B" (control derecho) — **actor** | **Iniciar / Parar** el ensayo de la toma en **Ensayar Solo**: envía el comando `Iniciar` o `Parar` por `SoloSession` y muestra "Iniciando..." / "Detenido" en el mundo (no pegado a la cabeza), que se desvanece en ~1.5 s. No graba nada en el Quest. Si se abre el menú de pausa o se sale de la toma mientras está iniciado, envía `Parar` solo | Solo Ensayar Solo, dentro de una toma, con el menú de pausa cerrado | Mantener `Y` + `N` | Botón B (control derecho) | ✅ Implementado, sin probar en hardware real |
| Primary Button izquierdo (botón **X**) — **actor** | **Fijar inicio aquí**: mueve el inicio de la toma (la marca) justo debajo de donde estás parado, mirando hacia donde miras. Párate en un extremo de tu espacio real, mirando hacia el espacio libre, y presiónalo: todo el cuarto queda adelante para caminar la toma. Muestra "Inicio fijado aquí" y vibra. También se hace solo al cargar cada toma | Ensayo Remoto y Ensayar Solo, dentro de una toma, con el menú de pausa cerrado | Mantener `T` + `B` | Botón X (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Joystick izquierdo | Mueve la cámara libremente (vuelo) en la dirección donde mira la cabeza, sin gravedad ni colisión | Solo mientras hay una reproducción activa en Inspección de Escenas | Mantener `T` + `W,A,S,D` | Joystick del control izquierdo | ✅ Implementado, sin probar en hardware real |
| Primary Button izquierdo (botón **X** en Quest) | Play/Pause del avatar en reproducción (lo mismo que el botón ▶/❚❚ del panel) | Solo mientras hay un panel de reproducción abierto (Inspección de Escenas o "Ver replay" en Ensayo Remoto) | Mantener `T` + `B` | Botón X (control izquierdo) | ✅ Implementado, sin probar en hardware real |
| Play/Pause (▶ / ❚❚) | Pausa o reanuda la reproducción; el ícono muestra siempre el estado real (▶ = en pausa, ❚❚ = reproduciendo), venga el cambio del botón, de X izquierdo, del slider o del final de la toma | Panel de reproducción, primer botón | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| Reiniciar (↺) | Salta al inicio de la reproducción y reanuda | Panel de reproducción (Inspección de Escenas o "Ver replay") | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Decidido: solo UI por ahora |
| Conversación (Conv.) / Notas | Silencian o vuelven a activar, en la reproducción, lo que se dijeron el director y el actor durante la toma, y las notas de voz dichas en un momento de ella. Activos por defecto (resaltados). Solo aparecen si la toma tiene ese audio | Panel de reproducción | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
| Pistas hápticas (fila bajo la barra) | No es un botón: muestra las pistas hápticas del shot, generadas automáticamente de la toma **preferida**. Rayitas para cada pista (Giro, Marca, Aviso, Acción, Corte) y barras para los tramos de Pasos, con su leyenda de colores. Si la toma no es la preferida, dice "marca esta toma como preferida para generarlas"; al presionar la estrella se generan y aparecen al instante (reemplazan las automáticas de la preferida anterior) | Panel de reproducción de una toma guardada de un shot (no escena completa) | — | — | ✅ Implementado, sin probar en hardware real |
| Velocidad (1×) | Cambia la velocidad de la reproducción en este orden: 1×, 1.5×, 2×, 0.25×, 0.5× y de vuelta a 1×. El botón se resalta si no está en 1×. Cada reproducción nueva empieza en 1×. Las notas de voz siempre se oyen a velocidad normal | Panel de reproducción (Inspección de Escenas o "Ver replay") | Clic con mouse (Game view) | Apuntar con el rayo + gatillo | ✅ Implementado, sin probar en hardware real |
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
**Escena completa:** B graba igual, pero la sesión se guarda como `tipo = "escena"` (shot `0`, con su escena), separada de los shots sueltos (`tipo = "toma"`). Toda sesión nueva lleva un `session_id` (GUID) creado al empezar a grabar. En el panel de fin de grabación de una escena completa **no aparece "Marcar como preferida"**: aprobar una escena necesita cortarla en referencias por shot, y eso viene después. "Marcar como favorita" sí.

### Siguiente toma — Secondary Button izquierdo (botón Y en Quest), director
**Script:** `RehearsalRecorder.cs` (`NextShot`)
**Binding real:** `<XRController>{LeftHand}/{secondaryButton}`
**Binding en el Simulador:** `<XRSimulatedController>{LeftHand}/secondaryButton` (mantener `T` + `N`)
**Contexto:** `#if DIRECTOR_BUILD`, solo mientras se graba una **escena completa** (con B) y el menú de pausa está cerrado. En un shot suelto, o sin grabar, no hace nada. La Y izquierda no la usa nada más en el director (la X izquierda es Play/Pause de la reproducción, la A derecha es el salto de fábrica y la B derecha graba).
**Qué hace:** al empezar a grabar la escena se abre el primer shot en `0 s`. Cada vez que presionas Y se cierra el shot actual y se abre el siguiente (en orden de número de shot), se manda `SessionCommand.SiguienteToma` por el canal de comandos (el actor solo lo registra en el log, `[Escena] OnCommand(SiguienteToma)`), aparece "Siguiente toma: shot N" y vibra el control izquierdo. Al detener se cierra el último. **Es opcional:** si grabas sin presionarlo, queda un solo tramo (el primer shot, de principio a fin).
**Dónde queda:** los tramos (`ShotBoundaries`: shot, `t_in`, `t_out`, `source = "manual"`) se guardan con **Guardar**, junto con la sesión. **Descartar** los borra.

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
- **Íconos:** casi todos son de la fuente **Material Symbols Rounded** (`GlassUI.Icon`); el texto es **Inter**. El corazón relleno de Favoritos sigue siendo una imagen generada en código (`UIIcons.cs`).
- **Indicadores en las esquinas** (fijos a la vista, chips de vidrio pequeños): arriba a la izquierda **REC** (director) o **Grabando** (actor) con un punto rojo que late, y debajo **Pods 9/9**; arriba a la derecha, para el actor, la toma ("Escena 1 · Shot 3"); abajo a la izquierda **Nota de voz**; arriba al centro, solo si se pierde la conexión, "Sin conexión con el servidor". Posiciones provisionales: revísalas en el visor.

### Escena completa (director) — ensayar y grabar toda la escena en una sola toma
**Scripts:** `SceneSelectController.cs`, `RehearsalSessionSync.cs` / `RehearsalSessionState.cs`, `Bootstrap.SelectTake`.
**Dónde:** en Selección de Escenas, durante **Ensayo Remoto** e **Inspección de Escenas**, cada escena empieza con un botón **"Escena completa — shots X a Y, en una sola toma"** (de otro color), arriba de sus shots sueltos. Solo lo ve el director. **No aparece en Ensayar Solo**, que sigue siendo solo de shots sueltos.
**Qué hace en Ensayo Remoto:** el director manda la escena y el shot `0` (= escena completa) por el modelo sincronizado. El actor carga esa escena y empieza en la marca de inicio del **primer shot** de la escena (el de número más bajo: 1, 6 u 11). Se graba con B y se marcan los shots con la Y izquierda (ver "Siguiente toma"). Los shots sueltos funcionan igual que antes.
**Qué hace en Inspección:** muestra las grabaciones de escena completa de esa escena, aparte de las de shots sueltos. Igual que con un shot: con 0 o 1 grabaciones carga directo; con más, abre una lista (fecha, duración y cuántos shots se marcaron), solo con **corazón**: la **estrella no aparece** en grabaciones de escena, ni en la lista ni en el panel de reproducción, porque aprobarlas necesita cortarlas en referencias por shot. La reproducción es la misma (fantasma + cámara grabada). El **slider** muestra una marca amarilla en el inicio de cada shot, con su número abajo ("T6", "T7"…). "Ver replay" de una escena recién grabada (sin guardar) también las muestra.
**Favoritos:** una grabación de escena aparece como "Escena N — Escena completa" y "Ver" la abre como escena completa.

### Ensayar Solo (actor) — modo propio
**Scripts:** `SoloRehearsalController.cs`, `SoloSession.cs` / `SoloSessionModel.cs`, `ApprovedReferences.cs`, `SoloPods.cs`.
**Modo:** `Bootstrap.Mode == Solo`, que se activa con el botón "Ensayar Solo" y vuelve a `None` con el menú de pausa → Menú Principal. Es un modo propio: ya no comparte la bandera de Inspección. En solo el actor ve **solo el set**: no hay panel de reproducción, ni fantasma, ni cámara grabada, ni lista de sesiones con estrella o corazón. Y **el director no lo afecta**: los cambios de toma, el fin del ensayo, la desconexión del director y los comandos del director se ignoran.
**Selección de Escenas:** solo tomas sueltas, y solo las que `ApprovedReferences.HasApprovedReference(escena, toma)` acepta. Por ahora acepta todas; más adelante le preguntará al servidor.
**Marca de inicio:** una "T" de cinta amarilla en el piso, en `Shot{N}_Start` (`StartMarkTape.cs`). La barra va donde quedan las puntas de los pies; el palo va hacia atrás, entre los pies. Aparece en toda toma, no solo en Ensayar Solo: también en Ensayo Remoto e Inspección, para el actor y para el director. Como el inicio se fija justo debajo del actor al cargar la toma, la marca empieza a sus pies: hay que mirar hacia abajo para verla. No sale en el visor ni en las grabaciones del director (capa `UI`).
**Iniciar / Parar:** botón **B** del control derecho. La **A** derecha está tomada por el salto de fábrica, y B es el mismo botón con el que el director graba. Ver la fila "Secondary Button B — actor" en la tabla.
**Sincronización:** el actor es dueño de `SoloSessionModel` mientras está en solo. Al cargar una toma escribe modo, escena y toma; Iniciar y Parar viajan como comandos (contador + baseline). Al salir de solo lo deja vacío y suelta la propiedad. Todavía nada reacciona a esos comandos: es para el servidor de PC. Logs con el prefijo `[Solo]`.
**Pods:** `SoloPods.GuideToMark` y `SoloPods.OnPodStatus` son ganchos vacíos, sin interfaz, para cuando existan los pods y el servidor.

### Espacio real del actor — límite del Quest y "Fijar inicio aquí"
**Scripts:** `StartMarkAligner.cs`, `PlayAreaOutline.cs` (solo build de actor; el director no cambia).
**El límite:** el Quest muestra tu cuarto real (passthrough) cuando cruzas el límite. Una app de VR no lo puede evitar: es una regla de seguridad de Meta. Por eso la toma tiene que caber en tu espacio real, y la app ayuda a aprovecharlo entero.
**Crear el límite (cada vez que abres la app):** la app empieza con un límite muy chico, aunque antes hayas dibujado uno grande. Camina hacia afuera de él y, cuando el Quest te lo ofrezca, crea un límite nuevo tan grande como el espacio libre. Hazlo antes de calibrar. Para estas escenas, alrededor de **4 × 5 m** está bien. Mientras el límite sea muy chico (menos de 1.5 m de lado), el panel "Antes de ensayar" te lo recuerda.
**Fijar inicio aquí (X izquierdo):** el inicio de la toma ya no cae en el centro de tu cuarto sino donde estás parado, mirando hacia donde miras. Al cargar una toma se hace solo; con X lo vuelves a hacer cuando quieras. Lo práctico: ve a un extremo del espacio, mira hacia el lado libre y presiona X. Solo se mueve el rig, nunca la cámara ni tu vista. El director ve al avatar saltar al inicio, en la misma dirección, y un mensaje: "El actor fijó su inicio". No funciona mientras se graba o durante el ensayo solo: "No puedes fijar el inicio durante la toma".
**Panel "Antes de ensayar":** en cada toma, si hace falta, te dice primero que crees un límite más grande, luego si hay un problema con los pods y al final que presiones **Y** para calibrar. No aparece durante la toma. **Ahora no** lo pospone hasta la siguiente toma, y desaparece solo cuando ya está resuelto. El aviso de calibrar solo aparece si hay pods conectados. Durante la calibración, este panel muestra cada pose.

### Notas de voz (director)
**Scripts:** `VoiceNoteRecorder.cs`, `VoiceNotesPanel.cs`, `WavFile.cs`. Para no escribir en el visor: hablas y queda guardado.
**Grabar:** presiona el **joystick izquierdo hacia adentro**, o el botón **Nota de voz** en la esquina inferior izquierda de la vista (siempre visible en Ensayo Remoto e Inspección de Escenas; posición provisional, sin ajustar). Mientras graba, el ícono se pone rojo y el botón muestra el tiempo y "Parar". Vuelve a presionar para terminar. Se detiene sola a los 2 minutos. **El actor no te escucha** mientras grabas. Con el panel de Guardar y Descartar abierto, el botón se oculta, pero el joystick funciona.
**A qué queda ligada:** a la toma que se está grabando, o a la que acabas de grabar y aún no decides, o a la que estás viendo, con el momento exacto. Si no hay ninguna, queda como nota general del shot. Si descartas una toma, sus notas se quedan como notas del shot.
**En la reproducción** cada nota dicha en un momento de la toma se oye sola en ese momento, a 1× (botón **Notas** para silenciarlas). **Notas del shot** (menú de pausa en Inspección) muestra las notas del shot aunque no tenga tomas. **Escuchar:** en el panel de reproducción, el botón del **micrófono** abre la lista: reproducir, **Ir** (salta a ese momento de la toma) y **Borrar**. Las notas con momento también se ven como marcas ámbar en la barra.
También funciona sin conexión. **Exportar:** van con cada toma (`notas.csv` y la carpeta `notas`, en WAV). Las notas generales van en `notas_sin_toma`.

### Audio de la toma
Mientras el director graba, se guarda también **la conversación**: lo que dice el director (solo cuando el actor lo escucha; las notas de voz no) y lo que dice el actor, en dos pistas. Se guarda con **Guardar** y se borra con **Descartar**. Mientras tienes una reproducción abierta, **la voz en vivo del actor se silencia** (solo de tu lado; el actor te sigue escuchando) para que oigas solo lo grabado, y vuelve al cerrarla. En la reproducción se oye en su momento, solo a **1×**: a otra velocidad se silencia y el tiempo dice "audio solo a 1×". **Conv.** la silencia. Se exporta como `conversacion_director.wav` y `conversacion_actor.wav`. El actor debe saber que la conversación se graba (ve "Grabando").

### Selección de Escenas: qué tiene cada shot
**En Inspección y en Ensayo Remoto**, cada escena dice cuántas tomas tiene y cada shot, a la derecha: tomas, ★ si una es preferida, notas y la fecha de la última ("3 tomas ★ · 2 notas · 7 nov"). **En Inspección**, los shots sin tomas se ven apagados.
**Al elegir un shot en Inspección:** si tiene tomas, siempre aparece la lista primero, con lo que tiene cada una (fecha, duración, **Con cuerpo / Sin cuerpo**, **Con audio**, notas), ★, ♥ y el **bote de basura** para borrarla (pide confirmación). Al final, **Ver el set sin toma**. Si no tiene tomas, entra directo al set: puedes recorrerlo y dejar notas de voz. Igual para "Escena completa".

### Borrar una toma
En el panel de reproducción, el botón del **bote de basura** (solo tomas guardadas), o en la lista de tomas de Selección de Escenas. Pide confirmación y borra también sus notas. No se puede deshacer. Si ya la habías exportado, los archivos exportados se quedan. Después regresa a la Selección de Escenas.

### Conexión
**Sin conexión con el servidor:** aparece un aviso rojo arriba mientras el dispositivo reintenta, también al abrir la app. En Inspección de Escenas no aparece, porque funciona sin conexión. Al volver: "Conexión recuperada".
**Si se corta durante una toma (director):** la grabación se detiene y aparece "Se perdió la conexión: la toma está a salvo", con **Ver replay** resaltado. **Guardar** te deja en la escena revisando esa toma, como en Inspección; **Descartar** te regresa al inicio. Si no estabas grabando, regresas al inicio de inmediato, como antes.
**Si se pierde el actor (director):** aparece "Se perdió la conexión con el actor". La grabación sigue, pero el actor se queda quieto en ella hasta que regrese.

### Qué toma es (actor)
Al cargar una toma, la escena, el shot y su descripción aparecen unos segundos frente a ti. Después se queda "Escena 1 · Shot 3" chico arriba a la derecha. Posición provisional, sin ajustar.

### Líneas en el piso (director)
En Ensayo Remoto ves en el piso del set **en rojo el límite real del actor** y **en azul el tuyo**. La roja se mueve cuando el actor presiona **X** o se mueve con el joystick, porque su cuarto real cambia de lugar dentro del set. No salen en la cámara virtual. El actor no ve líneas: el Quest ya le muestra su límite. Si la línea roja está muy chica, el actor todavía no ha creado su límite grande.

### Menú de pausa (rueda)
**Script:** `RadialMenu.cs`. Una rueda de vidrio con un ícono por opción. Al apuntar con el rayo, o al empujar el **joystick izquierdo** hacia una opción, se ilumina en cian y en el centro aparecen su nombre y qué hace. Se elige con el **gatillo** (rayo) o con el **clic del joystick izquierdo**. Presionar el **centro** o el botón **Menu** la cierra. Las opciones son las mismas de antes, según el modo.

### Voz
**Script:** `VoiceChat.cs` (usa la voz de Normcore). El director y el actor se escuchan durante Ensayo Remoto, también mientras esperan en el menú. La primera vez, el Quest pide permiso para usar el micrófono: sin él, no te escuchan, pero tú sí los escuchas. No se graba.
**Si están en el mismo cuarto:** apaga un micrófono desde el menú de pausa, o habrá eco. La voz no se usa en Ensayar Solo ni en Inspección de Escenas.

### "Grabando" en el actor
Mientras el director graba en Ensayo Remoto, el actor ve el punto rojo y la palabra "Grabando" arriba a la izquierda. Posición provisional, sin ajustar.

### Exportar tomas
**Script:** `TakeExporter.cs`. En Inspección de Escenas, menú de pausa → **Exportar tomas a CSV**. Cada toma queda en su propia carpeta, y las que ya se exportaron no se repiten: `sesion.csv` (escena, shot, fecha, duración…), `cabeza.csv`, `camara.csv`, `cuerpo.csv` (raíz, cabeza y 16 huesos, con rotaciones locales) y, en escena completa, `tomas.csv`.
**Dónde quedan:** en la carpeta `Exports` de los datos de la app. En la PC, `%USERPROFILE%\AppData\LocalLow\DefaultCompany\Tesis\Exports`. En el Quest, `/sdcard/Android/data/com.DefaultCompany.Tesis/files/Exports` (se copian a la PC con `adb pull`).

### Estado de los pods
**Scripts:** `PodStatusController.cs`, `PodStatus.cs`, `AvatarPoseSync.cs`. Los datos los manda la máquina que tiene los pods; si ninguna los tiene, no aparece el indicador.
**Pantalla (menú de pausa → Estado de pods):** una fila por pod, con su parte del cuerpo, la batería en % ("baja" en ámbar, "Cargando" mientras carga) y la señal (buena, regular o mala). "Sin conexión" en rojo si no se oye, "Sin sensor" en ámbar si está conectado pero su sensor no reporta. El % es una estimación: sube mientras carga y baja durante una vibración.
**Actor:** el panel "Antes de ensayar" avisa si falta un pod o si alguno tiene batería baja (menos de 3.6 V). Durante la toma no aparece nada, para no sacarlo de la escena. En Ensayar Solo, si se perdió un pod, se lo dice al terminar la toma.
**Director (Ensayo Remoto):** "Pods 9/9" abajo del punto de REC: normal si todo está bien, ámbar con batería baja, rojo si falta un pod. No sale en el visor. Si se pierde un pod aparece un mensaje, "Se perdió el pod 3". Se puede grabar con pods faltantes: solo avisa, "Grabando con 8 de 9 pods". Posición provisional, sin ajustar.

### El avatar en las reproducciones
**Scripts:** `PlaybackAvatar.cs`, `RecordingPlaybackController.cs`, `RehearsalRecorder.cs`.
**Qué ves:** en **Ver replay** y en **Inspección de Escenas**, el avatar del actor hace los movimientos de la toma, del tamaño del actor. Ya no aparece la cápsula amarilla.
**El avatar en vivo se oculta** mientras el panel de reproducción está abierto, para que no haya dos cuerpos en el mismo lugar, y vuelve al cerrarlo.
**Tomas sin datos del cuerpo** (grabadas antes de este cambio o sin pods): solo se guardó el recorrido de la cabeza, así que el avatar sigue ese recorrido parado, con los brazos abajo, y aparece el mensaje "Toma sin datos del cuerpo".

### Tutorial de controles
**Script:** `TutorialController.cs` (persistente, objeto "Tutorial" bajo Bootstrap).
**Cuándo aparece:** la primera vez que se entra a una escena de contenido en cada modo, un panel pregunta si quieres ver el tutorial (Sí / No); en Ensayo Remoto, "¿Quieres ver un tutorial de cómo calibrar y de los controles?". La respuesta se guarda en el headset (`PlayerPrefs`, una clave por modo: `Tutorial.Seen.DirectorRemote`, `DirectorInspection`, `ActorRemote`, `ActorSolo`), así que no vuelve a preguntar. Se puede repetir siempre desde el menú de pausa → **Tutorial** (entra directo a los pasos, sin preguntar).
**Cómo funciona:** un paso por vez. Los pasos que piden una acción avanzan solos cuando se detecta (muestran "¡Muy bien!" y un pulso háptico); los pasos informativos tienen **Siguiente**. **Saltar tutorial** está siempre. El panel aparece **centrado frente a ti**, un poco abajo de los ojos, y se queda quieto mientras lo lees; solo vuelve a ponerse enfrente si giras mucho (unos 45°), y se hace a un lado mientras está abierto un menú que te está enseñando. En el Editor, cada paso muestra también las teclas del XR Device Simulator.
**Pasos por modo:**
- **Director — Ensayo Remoto (14, o 15 en escena completa):** rayo + gatillo · el actor calibra con Y, o tú con Calibrar en el menú de pausa · la voz · las notas de voz · consejo de los joysticks · las líneas roja y azul del piso · el indicador Pods 9/9 · Grip para levantar el visor · cambiar de lente · zoom fino · B para grabar · *(solo en escena completa)* Y izquierda para la siguiente toma (opcional: también avanza al detener la grabación) · B para detener · elegir Ver replay / Guardar / Descartar (espera a que se guarde o descarte, aunque se pase por Ver replay; en escena completa solo menciona favorita) · Menu. El tutorial se ofrece una vez por modo, así que para ver el paso de la siguiente toma, abre una escena completa y elige **Tutorial** en el menú de pausa.
- **Director — Inspección de Escenas (8):** rayo + gatillo · pausar/reanudar (botón o X izquierdo) · arrastrar la barra · ↺ / velocidad / ∞ / ▣ · estrella y corazón · volar con el joystick izquierdo · Menu. Los pasos del panel de reproducción solo aparecen si el shot tiene una grabación.
- **Actor — Ensayo Remoto (9):** rayo + gatillo · la voz · consejo de los joysticks · el límite (y cómo crear uno más grande) · X para fijar el inicio · la marca de cinta en el piso (mira hacia abajo) · el director elige la toma y el aviso "Grabando" · Menu (y Estado de pods) · Y para calibrar (solo lo explica, no lo pide).
- **Actor — Ensayar Solo (9):** rayo + gatillo · consejo de los joysticks · el límite (y cómo crear uno más grande) · X para fijar el inicio · la marca de cinta en el piso · B para iniciar · B para detener · Menu (y Estado de pods) · Y para calibrar (solo lo explica, no lo pide).
**Consejo de los joysticks:** solo informa que puedes moverte con el izquierdo y girar con el derecho; no pide hacerlo, porque caminar y voltear la cabeza no hace falta enseñarlo.
**Texto:** en español de México (tú). En cada paso, el control que hay que presionar está resaltado en **ámbar** y lo que hace en **celeste** (negritas).
**Panel:** arriba a la izquierda de la vista, sigue a la cabeza con retraso (como el panel de reproducción). Nunca queda encima de otro panel con botones (menú de pausa, panel de fin de grabación, Favoritos, panel de reproducción): si lo taparía, se mueve a la izquierda o arriba de ese panel, lo que implique moverse menos (arriba solo si no queda más alto de ~22°, porque mirar hacia arriba cansa más). En la práctica, el panel de fin de grabación (ancho y bajo) lo manda arriba y el menú de pausa (alto) lo manda a la izquierda. Al cerrarse ese panel, vuelve a su lugar. Solo sus botones atrapan el rayo (fondo y texto no). Está en la capa `UI`, así que no aparece en el feed del visor. Posición provisional, sin ajustar.
**Para volver a ver la pregunta en el Editor:** borrar esas claves de `PlayerPrefs` (Edit → Clear All PlayerPrefs borra todas).

---

## Otros hallazgos relevantes (no son controles en sí, pero afectan cómo probarlos)

- **Probado en un Quest 3 real solo el build de actor** (rayo con los paneles, menú de pausa, caminar, seguir al director a la toma). Los controles del director (B, Grip, zoom, X izquierdo, vuelo) solo se probaron en el Editor con el XR Device Simulator.
