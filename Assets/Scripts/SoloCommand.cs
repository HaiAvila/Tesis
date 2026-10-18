// Actor → everyone commands sent through SoloSession's command channel while in Ensayar
// Solo. 0 is reserved for "no command yet". Nothing reacts to them yet beyond logging; the
// PC server will start/stop the take's cues on them.
public enum SoloCommand
{
    None = 0,
    Iniciar = 1,
    Parar = 2,
}
