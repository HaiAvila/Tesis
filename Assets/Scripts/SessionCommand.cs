// Director → everyone commands sent through RehearsalSessionState's command channel. 0 is
// reserved for "no command yet", so a fresh model's commandId never decodes as a real one.
// None of them does anything yet beyond logging on the receiver.
public enum SessionCommand
{
    None = 0,
    Calibrate = 1,
    Grabar = 2,
    SiguienteToma = 3,
    Accion = 4,
    Corte = 5,
    Guardar = 6,
    Descartar = 7,
    Aprobar = 8,
}
