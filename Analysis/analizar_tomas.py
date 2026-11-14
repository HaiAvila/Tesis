#!/usr/bin/env python3
"""
analizar_tomas.py - compares each take with its shot's reference (the preferida take) and
writes the results for the thesis: CSV tables, SVG charts and an HTML report.

Standard Python only (3.8+), no installs.

    python analizar_tomas.py                         the server's database, every shot
    python analizar_tomas.py --desde 2026-11-13      only takes recorded from that day (the pilot)
    python analizar_tomas.py --escena 2 --shot 6     one shot
    python analizar_tomas.py --db ruta\\shotlist.db   another database (a Quest backup, the Editor's)
    python analizar_tomas.py --salida carpeta        where to write (default: resultados/<date>)

What it reads (the app's own SQLite database, read only - it never writes to it):
  Sesiones   the takes: scene, shot, date, duration, preferida, and (when the app has it) the
             condition "pistas" = 1 with haptic cues, 0 without.
  Metricas   the actor's head during each take, 10 Hz: position and rotation, in the set's
             coordinates (every take starts on the shot's start mark).

By default it reads the server's database (the store of record, with every device's takes),
or the Editor's if there's no server one on this PC.

What it measures, per take, against the reference (the shot's preferida):
  - the marks: the places where the reference stopped (still for at least 1 s after moving
    at least 0.5 m). For each one, whether the take stopped there too (within 1.5 m, in
    order), how far from it (m), how early or late (s, + = late) and how far off the facing
    was (degrees, the head's direction while stopped).
  - the path: the average distance from the take's path to the reference's (m, where it
    walked, whatever the timing) and to where the reference was at the same moment (m,
    "sincronia": path and timing together).
  - the duration difference and how far apart the two starts were (a start more than 1 m
    away usually means the start marks moved between the two takes: compare with care).
Only single-shot takes; full-scene takes are left out (they have no reference of their own).

What it writes (in the output folder):
  resumen_tomas.csv        one row per take
  marcas.csv               one row per take and mark
  condiciones.csv          by condition, per shot and all together (medians and means)
  recorrido_e<E>_s<S>.svg  each shot's paths seen from above, the reference in black
  error_marca.svg, retraso_marca.svg, orientacion_marca.svg, error_recorrido.svg
                           each metric by condition, one dot per mark (or per take)
  informe.html             all of it on one page
"""

import argparse
import csv
import datetime as dt
import html
import math
import os
import sqlite3
import statistics
import sys

# --- what counts as a stop, a mark, a hit -------------------------------------------------------
STILL_SPEED = 0.12        # m/s: slower than this is standing
SPEED_WINDOW = 0.6        # s: speed measured over this span
MIN_STOP = 1.0            # s: still at least this long = a stop
MIN_MARK_SPACING = 0.5    # m: a mark is at least this far from the previous one (or the start)
HIT_RADIUS = 1.5          # m: the take's stop must be this close to count as reaching the mark
START_WARNING = 1.0       # m: starts further apart than this get flagged

CONDITIONS = {1: "Con pistas", 0: "Sin pistas", None: "Sin dato"}
CONDITION_ORDER = ["Con pistas", "Sin pistas", "Sin dato"]

# Light chart surface and the first three categorical slots (validated together, all pairs).
SURFACE = "#fcfcfb"
INK = "#0b0b0b"
INK_2 = "#52514e"
INK_3 = "#8a8984"
GRID = "#e6e5e1"
REFERENCE = "#2b2a28"
COLORS = {"Con pistas": "#2a78d6", "Sin pistas": "#eb6834", "Sin dato": "#1baf7a"}
FONT = "Inter, 'Segoe UI', Arial, sans-serif"


# =============================================================================================
# reading
# =============================================================================================

def default_db():
    base = os.path.join(os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "Tesis")
    for candidate in (os.path.join(base, "Servidor", "shotlist.db"), os.path.join(base, "shotlist.db")):
        if os.path.exists(candidate):
            return candidate
    return None


class Sample:
    __slots__ = ("t", "x", "y", "z", "yaw")

    def __init__(self, t, x, y, z, yaw):
        self.t, self.x, self.y, self.z, self.yaw = t, x, y, z, yaw


class Take:
    def __init__(self, row, condition):
        self.id = row["id"]
        self.guid = row["session_id"] or ""
        self.scene = row["scene_number"]
        self.shot = row["shot_number"]
        self.date = row["fecha_hora"] or ""
        self.duration = row["duracion"] or 0.0
        self.preferida = bool(row["es_preferida"])
        self.condition = condition
        self.samples = []
        self.stops = []
        self.result = None

    @property
    def label(self):
        return f"Toma {self.id} ({self.date[:16].replace('T', ' ')})"


def head_yaw(qx, qy, qz, qw):
    """The direction the head faces, in degrees about the vertical (0 = +Z, 90 = +X)."""
    fx = 2.0 * (qx * qz + qw * qy)
    fz = 1.0 - 2.0 * (qx * qx + qy * qy)
    return math.degrees(math.atan2(fx, fz))


def load(db_path, scene=None, shot=None, since=None):
    connection = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    columns = {r["name"] for r in connection.execute("PRAGMA table_info(Sesiones)")}
    has_condition = "pistas" in columns

    query = "SELECT * FROM Sesiones WHERE (tipo IS NULL OR tipo = 'toma') AND shot_number > 0"
    takes = []
    for row in connection.execute(query + " ORDER BY scene_number, shot_number, id"):
        if scene is not None and row["scene_number"] != scene:
            continue
        if shot is not None and row["shot_number"] != shot:
            continue
        condition = CONDITIONS.get(row["pistas"] if has_condition else None, "Sin dato")
        take = Take(row, condition)
        for m in connection.execute(
                "SELECT tiempo_relativo, pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w FROM Metricas "
                "WHERE sesion_id = ? ORDER BY tiempo_relativo", (take.id,)):
            take.samples.append(Sample(m[0], m[1], m[2], m[3], head_yaw(m[4], m[5], m[6], m[7])))
        takes.append(take)
    connection.close()

    # Takes from before --desde stay as references, never as takes to analyze.
    for take in takes:
        take.in_range = since is None or take.date[:10] >= since
    return takes, has_condition


# =============================================================================================
# measuring
# =============================================================================================

def speeds(samples):
    """Horizontal speed at each sample, over SPEED_WINDOW."""
    result = []
    n = len(samples)
    j0 = j1 = 0
    for i, s in enumerate(samples):
        while j0 < i and s.t - samples[j0].t > SPEED_WINDOW / 2:
            j0 += 1
        while j1 < n - 1 and samples[j1 + 1].t - s.t <= SPEED_WINDOW / 2:
            j1 += 1
        a, b = samples[j0], samples[j1]
        span = b.t - a.t
        result.append(math.hypot(b.x - a.x, b.z - a.z) / span if span > 1e-6 else 0.0)
    return result


def circular_mean(angles):
    sx = sum(math.sin(math.radians(a)) for a in angles)
    sz = sum(math.cos(math.radians(a)) for a in angles)
    return math.degrees(math.atan2(sx, sz))


def angle_difference(a, b):
    return (a - b + 180.0) % 360.0 - 180.0


class Stop:
    def __init__(self, samples):
        self.start = samples[0].t
        self.end = samples[-1].t
        self.x = statistics.fmean(s.x for s in samples)
        self.z = statistics.fmean(s.z for s in samples)
        self.yaw = circular_mean([s.yaw for s in samples])


def find_stops(take):
    """Where the take stood still for MIN_STOP or more, after moving MIN_MARK_SPACING away from
    the previous stop (or the start). Standing at the start isn't a stop."""
    samples = take.samples
    if len(samples) < 3:
        return []
    v = speeds(samples)
    runs, current = [], []
    for s, speed in zip(samples, v):
        if speed < STILL_SPEED:
            current.append(s)
        else:
            if current:
                runs.append(current)
            current = []
    if current:
        runs.append(current)

    stops = []
    anchor = (samples[0].x, samples[0].z)
    for run in runs:
        if run[-1].t - run[0].t < MIN_STOP:
            continue
        stop = Stop(run)
        if math.hypot(stop.x - anchor[0], stop.z - anchor[1]) < MIN_MARK_SPACING:
            # The same spot as the previous stop (or the start): one longer stop, not a new mark.
            if stops and math.hypot(stop.x - stops[-1].x, stop.z - stops[-1].z) < MIN_MARK_SPACING:
                stops[-1].end = stop.end
            continue
        stops.append(stop)
        anchor = (stop.x, stop.z)
    return stops


def position_at(samples, t):
    """The take's horizontal position at time t (linear between samples, clamped at the ends)."""
    if t <= samples[0].t:
        return samples[0].x, samples[0].z
    if t >= samples[-1].t:
        return samples[-1].x, samples[-1].z
    lo, hi = 0, len(samples) - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if samples[mid].t <= t:
            lo = mid
        else:
            hi = mid
    a, b = samples[lo], samples[hi]
    k = (t - a.t) / (b.t - a.t) if b.t > a.t else 0.0
    return a.x + (b.x - a.x) * k, a.z + (b.z - a.z) * k


def distance_to_path(px, pz, path):
    best = float("inf")
    for a, b in zip(path, path[1:]):
        dx, dz = b.x - a.x, b.z - a.z
        length2 = dx * dx + dz * dz
        k = 0.0 if length2 < 1e-12 else max(0.0, min(1.0, ((px - a.x) * dx + (pz - a.z) * dz) / length2))
        best = min(best, math.hypot(px - (a.x + dx * k), pz - (a.z + dz * k)))
    return best


def compare(take, reference):
    s, r = take.samples, reference.samples
    result = {
        "duracion_s": take.duration,
        "dif_duracion_s": take.duration - reference.duration,
        "desfase_inicio_m": math.hypot(s[0].x - r[0].x, s[0].z - r[0].z),
        "error_recorrido_m": statistics.fmean(distance_to_path(p.x, p.z, r) for p in s),
        "error_sincronia_m": statistics.fmean(
            math.hypot(p.x - q[0], p.z - q[1]) for p in s for q in [position_at(r, p.t)]),
        "error_final_m": math.hypot(s[-1].x - r[-1].x, s[-1].z - r[-1].z),
        "marcas": [],
    }

    next_stop = 0
    for index, mark in enumerate(reference.stops, start=1):
        entry = {"marca": index, "t_ref": mark.start, "x": mark.x, "z": mark.z, "alcanzada": False,
                 "error_m": None, "retraso_s": None, "orientacion_deg": None}
        best = None
        for k in range(next_stop, len(take.stops)):
            stop = take.stops[k]
            d = math.hypot(stop.x - mark.x, stop.z - mark.z)
            if d <= HIT_RADIUS and (best is None or d < best[1]):
                best = (k, d)
        if best is not None:
            stop = take.stops[best[0]]
            next_stop = best[0] + 1
            entry.update(alcanzada=True, error_m=best[1], retraso_s=stop.start - mark.start,
                         orientacion_deg=abs(angle_difference(stop.yaw, mark.yaw)))
        # How close the take's path ever came to the mark, reached or not.
        entry["distancia_minima_m"] = min(math.hypot(p.x - mark.x, p.z - mark.z) for p in s)
        result["marcas"].append(entry)

    reached = [m for m in result["marcas"] if m["alcanzada"]]
    result["marcas_alcanzadas"] = len(reached)
    result["error_marca_m"] = mean_or_none([m["error_m"] for m in reached])
    result["retraso_marca_s"] = mean_or_none([m["retraso_s"] for m in reached])
    result["retraso_abs_s"] = mean_or_none([abs(m["retraso_s"]) for m in reached])
    result["error_orientacion_deg"] = mean_or_none([m["orientacion_deg"] for m in reached])
    return result


def mean_or_none(values):
    values = [v for v in values if v is not None]
    return statistics.fmean(values) if values else None


def median_or_none(values):
    values = [v for v in values if v is not None]
    return statistics.median(values) if values else None


# =============================================================================================
# writing: tables
# =============================================================================================

def fmt(value, digits=3):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, float):
        return f"{value:.{digits}f}"
    return str(value)


def write_csv(path, header, rows):
    with open(path, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.writer(f)
        writer.writerow(header)
        for row in rows:
            writer.writerow([fmt(v) for v in row])


TAKE_COLUMNS = ["escena", "shot", "toma_id", "guid", "fecha", "condicion", "referencia_id", "duracion_s",
                "dif_duracion_s", "desfase_inicio_m", "error_recorrido_m", "error_sincronia_m",
                "marcas_ref", "marcas_alcanzadas", "error_marca_m", "retraso_marca_s", "retraso_abs_s",
                "error_orientacion_deg", "error_final_m", "aviso"]

MARK_COLUMNS = ["escena", "shot", "toma_id", "condicion", "marca", "t_ref_s", "alcanzada", "error_m",
                "retraso_s", "orientacion_deg", "distancia_minima_m"]

SUMMARY_METRICS = [
    ("error_marca_m", "Error en la marca (m)"),
    ("retraso_abs_s", "Desfase de tiempo en la marca (s)"),
    ("error_orientacion_deg", "Error de orientación en la marca (°)"),
    ("error_recorrido_m", "Error de recorrido (m)"),
    ("error_sincronia_m", "Error de sincronía (m)"),
]


def take_row(take, reference):
    r = take.result
    return [take.scene, take.shot, take.id, take.guid, take.date, take.condition, reference.id,
            r["duracion_s"], r["dif_duracion_s"], r["desfase_inicio_m"], r["error_recorrido_m"],
            r["error_sincronia_m"], len(reference.stops), r["marcas_alcanzadas"], r["error_marca_m"],
            r["retraso_marca_s"], r["retraso_abs_s"], r["error_orientacion_deg"], r["error_final_m"],
            take.warning]


def summarize(takes, scope):
    """Per condition: how many takes and marks, and each metric's median and mean."""
    rows = []
    for condition in CONDITION_ORDER:
        group = [t for t in takes if t.condition == condition]
        if not group:
            continue
        marks = [m for t in group for m in t.result["marcas"]]
        reached = [m for m in marks if m["alcanzada"]]
        row = {"alcance": scope, "condicion": condition, "n_tomas": len(group), "n_marcas": len(marks),
               "marcas_alcanzadas_pct": 100.0 * len(reached) / len(marks) if marks else None}
        per_mark = {
            "error_marca_m": [m["error_m"] for m in reached],
            "retraso_abs_s": [abs(m["retraso_s"]) for m in reached],
            "error_orientacion_deg": [m["orientacion_deg"] for m in reached],
        }
        for key, _ in SUMMARY_METRICS:
            values = per_mark.get(key, [t.result[key] for t in group])
            row[key + "_mediana"] = median_or_none(values)
            row[key + "_media"] = mean_or_none(values)
        rows.append(row)
    return rows


SUMMARY_COLUMNS = ["alcance", "condicion", "n_tomas", "n_marcas", "marcas_alcanzadas_pct"] + \
    [key + suffix for key, _ in SUMMARY_METRICS for suffix in ("_mediana", "_media")]


# =============================================================================================
# writing: charts (SVG by hand)
# =============================================================================================

def nice_ticks(lo, hi, count=5):
    if hi - lo < 1e-9:
        hi = lo + 1.0
    raw = (hi - lo) / count
    magnitude = 10 ** math.floor(math.log10(raw))
    step = min((m * magnitude for m in (1, 2, 2.5, 5, 10) if m * magnitude >= raw), default=raw)
    start = math.floor(lo / step) * step
    ticks = []
    v = start
    while True:
        ticks.append(round(v, 10))
        if v >= hi - step * 0.001:
            break
        v += step
    return ticks


def tick_label(v):
    text = f"{v:.2f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text.replace("-", "−")


def svg_open(width, height, title, subtitle):
    return [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" '
        f'font-family="{FONT}" role="img" aria-label="{html.escape(title)}">',
        f'<rect width="{width}" height="{height}" fill="{SURFACE}"/>',
        f'<text x="24" y="32" font-size="17" font-weight="600" fill="{INK}">{html.escape(title)}</text>',
        f'<text x="24" y="54" font-size="13" fill="{INK_2}">{html.escape(subtitle)}</text>',
    ]


def legend(parts, x, y, entries):
    """entries: (label, color, kind) with kind 'dot' or 'line'."""
    for label, color, kind in entries:
        if kind == "line":
            parts.append(f'<line x1="{x}" y1="{y}" x2="{x + 18}" y2="{y}" stroke="{color}" stroke-width="3" stroke-linecap="round"/>')
            x += 26
        else:
            parts.append(f'<circle cx="{x + 5}" cy="{y}" r="5" fill="{color}"/>')
            x += 16
        parts.append(f'<text x="{x}" y="{y + 4.5}" font-size="13" fill="{INK_2}">{html.escape(label)}</text>')
        x += 8 * len(label) + 22


OTHER_TAKE = "#b3b1ab"    # the takes that aren't the closest: recessive, named by a label at their end


def closest_take(takes):
    """The take most like the reference: the one that reached the most marks, then the one that was
    nearest to where the reference was at the same moment (path and timing together; the path
    alone would favour a take that walked the same line but stopped late and far from the mark)."""
    return min(takes, key=lambda t: (-t.result["marcas_alcanzadas"], t.result["error_sincronia_m"]))


def cm(meters):
    return f"{meters * 100:.0f} cm"


def plain_summary(take, reference):
    """One take against the reference in plain words, for people who don't read tables."""
    r = take.result
    marks = len(reference.stops)
    parts = []
    if marks:
        if r["marcas_alcanzadas"] == marks:
            parts.append("llegó a la marca" if marks == 1 else f"llegó a las {marks} marcas")
        elif r["marcas_alcanzadas"] == 0:
            parts.append("no llegó a la marca" if marks == 1 else f"no llegó a ninguna de las {marks} marcas")
        else:
            parts.append(f"llegó a {r['marcas_alcanzadas']} de las {marks} marcas")
        if r["error_marca_m"] is not None:
            delay = r["retraso_marca_s"]
            when = ("casi al mismo tiempo que la preferida" if abs(delay) < 0.15 else
                    f"{abs(delay):.1f} s {'más tarde' if delay > 0 else 'antes'} que la preferida")
            parts.append(f"se detuvo a {cm(r['error_marca_m'])} de ella, {when}")
    parts.append(f"momento a momento, estuvo en promedio a {cm(r['error_sincronia_m'])} de donde iba el actor en la preferida")
    d = r["dif_duracion_s"]
    parts.append("duró lo mismo" if abs(d) < 0.15 else f"duró {abs(d):.1f} s {'más' if d > 0 else 'menos'}")
    text = "; ".join(parts) + "."
    return text[0].upper() + text[1:]


def path_chart(reference, takes, scene, shot, best=None):
    """The paths seen from above, x to the right and z up (the set's own top view), same scale
    on both axes so distances read true. The closest take is drawn strong; the others thin and
    gray, each named at the end of its line."""
    width, height = 760, 560
    left, right, top, bottom = 64, 24, 96, 56
    points = [(p.x, p.z) for t in [reference] + takes for p in t.samples]
    xs, zs = [p[0] for p in points], [p[1] for p in points]
    cx, cz = (max(xs) + min(xs)) / 2, (max(zs) + min(zs)) / 2
    plot_w, plot_h = width - left - right, height - top - bottom
    # The same scale on both axes, as large as the paths allow (a walk along x fills the width).
    scale = min(plot_w / (max(max(xs) - min(xs), 1.0) * 1.15), plot_h / (max(max(zs) - min(zs), 1.0) * 1.15))
    x0, z0 = cx - plot_w / scale / 2, cz - plot_h / scale / 2

    def X(x):
        return left + (x - x0) * scale

    def Z(z):
        return top + plot_h - (z - z0) * scale

    parts = svg_open(width, height, f"Recorridos vistos desde arriba · Escena {scene}, shot {shot}",
                     f"Preferida: toma {reference.id} · {len(takes)} {'toma comparada' if len(takes) == 1 else 'tomas comparadas'} · metros")
    best = best or closest_take(takes)
    entries = [("Preferida", REFERENCE, "line"),
               (f"Toma {best.id}: la más parecida" if len(takes) > 1 else f"Toma {best.id}", COLORS[best.condition], "line")]
    if len(takes) > 1:
        entries.append(("Otras tomas", OTHER_TAKE, "line"))
    entries.append(("Marca", REFERENCE, "dot"))
    legend(parts, 24, 78, entries)

    for v in nice_ticks(x0, x0 + plot_w / scale, 6):
        if left <= X(v) <= left + plot_w:
            parts.append(f'<line x1="{X(v):.1f}" y1="{top}" x2="{X(v):.1f}" y2="{top + plot_h}" stroke="{GRID}" stroke-width="1"/>')
            parts.append(f'<text x="{X(v):.1f}" y="{top + plot_h + 20}" font-size="12" fill="{INK_3}" text-anchor="middle">{tick_label(v)}</text>')
    for v in nice_ticks(z0, z0 + plot_h / scale, 6):
        if top <= Z(v) <= top + plot_h:
            parts.append(f'<line x1="{left}" y1="{Z(v):.1f}" x2="{left + plot_w}" y2="{Z(v):.1f}" stroke="{GRID}" stroke-width="1"/>')
            parts.append(f'<text x="{left - 8}" y="{Z(v) + 4:.1f}" font-size="12" fill="{INK_3}" text-anchor="end">{tick_label(v)}</text>')
    parts.append(f'<text x="{left + plot_w / 2:.1f}" y="{height - 14}" font-size="12" fill="{INK_2}" text-anchor="middle">x (m)</text>')
    parts.append(f'<text x="18" y="{top + plot_h / 2:.1f}" font-size="12" fill="{INK_2}" text-anchor="middle" '
                 f'transform="rotate(-90 18 {top + plot_h / 2:.1f})">z (m)</text>')

    # Paths and mark circles stay inside the plot area.
    parts.append(f'<defs><clipPath id="plot"><rect x="{left}" y="{top}" width="{plot_w}" height="{plot_h}"/></clipPath></defs><g clip-path="url(#plot)">')

    def polyline(samples, color, w, opacity, tooltip):
        pts = " ".join(f"{X(p.x):.1f},{Z(p.z):.1f}" for p in samples)
        parts.append(f'<polyline points="{pts}" fill="none" stroke="{color}" stroke-width="{w}" stroke-opacity="{opacity}" '
                     f'stroke-linejoin="round" stroke-linecap="round"><title>{html.escape(tooltip)}</title></polyline>')

    for take in takes:
        if take is not best:
            polyline(take.samples, OTHER_TAKE, 2, 1,
                     f"{take.label} · {take.condition} · a {cm(take.result['error_recorrido_m'])} del camino de la preferida")
    polyline(reference.samples, SURFACE, 7, 1, "")
    polyline(reference.samples, REFERENCE, 3, 1, f"Preferida: {reference.label}")
    polyline(best.samples, SURFACE, 7, 1, "")
    polyline(best.samples, COLORS[best.condition], 3.5, 1,
             f"{best.label} · {best.condition} · la más parecida: a {cm(best.result['error_recorrido_m'])} del camino de la preferida")

    start = reference.samples[0]
    parts.append(f'<circle cx="{X(start.x):.1f}" cy="{Z(start.z):.1f}" r="6" fill="{SURFACE}" stroke="{REFERENCE}" stroke-width="2">'
                 f'<title>Inicio de la referencia</title></circle>')
    parts.append(f'<text x="{X(start.x) + 10:.1f}" y="{Z(start.z) + 4:.1f}" font-size="12" fill="{INK_2}">Inicio</text>')
    for index, mark in enumerate(reference.stops, start=1):
        parts.append(f'<circle cx="{X(mark.x):.1f}" cy="{Z(mark.z):.1f}" r="6" fill="{REFERENCE}" stroke="{SURFACE}" stroke-width="2">'
                     f'<title>Marca {index}: en la preferida llega a los {mark.start:.1f} s</title></circle>')
        parts.append(f'<text x="{X(mark.x):.1f}" y="{Z(mark.z) + 24:.1f}" font-size="13" font-weight="600" fill="{INK}" '
                     f'text-anchor="middle" stroke="{SURFACE}" stroke-width="3" paint-order="stroke">Marca {index}</text>')
    parts.append("</g>")

    # Each take named where its line ends (the closest one in bold), nudged apart if two ends meet.
    used = []
    for take in sorted(takes, key=lambda t: t is best):
        end = take.samples[-1]
        x = min(max(X(end.x) + 8, left + 4), left + plot_w - 70)
        y = min(max(Z(end.z) + 4, top + 12), top + plot_h - 4)
        while any(abs(y - u) < 15 and abs(x - ux) < 80 for ux, u in used):
            y += 15
        used.append((x, y))
        strong = take is best
        parts.append(f'<text x="{x:.1f}" y="{y:.1f}" font-size="{13 if strong else 12}" font-weight="{600 if strong else 400}" '
                     f'fill="{INK if strong else INK_2}" stroke="{SURFACE}" stroke-width="3" paint-order="stroke">Toma {take.id}</text>')
    parts.append("</svg>")
    return "\n".join(parts)


def strip_chart(title, subtitle, unit, groups, signed=False):
    """One column per condition, a dot per value (jittered), a line at the median.
    groups: list of (condition, [(value, tooltip)])."""
    groups = [(c, v) for c, v in groups if v]
    width = max(540, 140 + 170 * len(groups))
    height = 420
    left, right, top, bottom = 72, 24, 76, 56
    plot_w, plot_h = width - left - right, height - top - bottom
    values = [v for _, vals in groups for v, _ in vals]
    if not values:
        return None
    lo = min(values + [0.0]) if signed else 0.0
    hi = max(values + [0.0])
    ticks = nice_ticks(lo, hi * 1.05 if hi > 0 else 1.0, 5)
    lo, hi = ticks[0], ticks[-1]

    def Y(v):
        return top + plot_h - (v - lo) / (hi - lo) * plot_h

    parts = svg_open(width, height, title, subtitle)
    for v in ticks:
        stroke = INK_3 if (signed and abs(v) < 1e-9) else GRID
        parts.append(f'<line x1="{left}" y1="{Y(v):.1f}" x2="{left + plot_w}" y2="{Y(v):.1f}" stroke="{stroke}" stroke-width="1"/>')
        parts.append(f'<text x="{left - 8}" y="{Y(v) + 4:.1f}" font-size="12" fill="{INK_3}" text-anchor="end">{tick_label(v)}</text>')
    parts.append(f'<text x="18" y="{top + plot_h / 2:.1f}" font-size="12" fill="{INK_2}" text-anchor="middle" '
                 f'transform="rotate(-90 18 {top + plot_h / 2:.1f})">{html.escape(unit)}</text>')

    band = plot_w / len(groups)
    for i, (condition, vals) in enumerate(groups):
        center = left + band * (i + 0.5)
        color = COLORS[condition]
        for k, (v, tooltip) in enumerate(vals):
            jitter = ((k * 0.618034) % 1.0 - 0.5) * min(band * 0.5, 90)
            parts.append(f'<circle cx="{center + jitter:.1f}" cy="{Y(v):.1f}" r="5" fill="{color}" fill-opacity="0.85" '
                         f'stroke="{SURFACE}" stroke-width="2"><title>{html.escape(tooltip)}</title></circle>')
        median = statistics.median(v for v, _ in vals)
        half = min(band * 0.32, 60)
        parts.append(f'<line x1="{center - half:.1f}" y1="{Y(median):.1f}" x2="{center + half:.1f}" y2="{Y(median):.1f}" '
                     f'stroke="{INK}" stroke-width="2" stroke-linecap="round"><title>Mediana: {median:.2f}</title></line>')
        parts.append(f'<text x="{center + half + 6:.1f}" y="{Y(median) + 4:.1f}" font-size="12" fill="{INK}">{median:.2f}</text>')
        parts.append(f'<circle cx="{center - 52:.1f}" cy="{top + plot_h + 22}" r="5" fill="{color}"/>')
        parts.append(f'<text x="{center - 42:.1f}" y="{top + plot_h + 26}" font-size="13" fill="{INK_2}">'
                     f'{html.escape(condition)} (n={len(vals)})</text>')
    parts.append("</svg>")
    return "\n".join(parts)


# =============================================================================================
# the report
# =============================================================================================

def html_table(header, rows):
    out = ["<table><thead><tr>"] + [f"<th>{html.escape(h)}</th>" for h in header] + ["</tr></thead><tbody>"]
    for row in rows:
        out.append("<tr>" + "".join(f"<td>{html.escape(fmt(v, 2))}</td>" for v in row) + "</tr>")
    out.append("</tbody></table>")
    return "".join(out)


PAGE_STYLE = f"""
:root {{ color-scheme: light; }}
body {{ background: {SURFACE}; color: {INK}; font-family: {FONT}; margin: 0; padding: 24px 16px 64px; }}
main {{ max-width: 1100px; margin: 0 auto; }}
h1 {{ font-size: 26px; margin: 0 0 4px; }} h2 {{ font-size: 20px; margin: 40px 0 8px; }}
p, li {{ color: {INK_2}; line-height: 1.5; }}
.charts {{ display: flex; flex-wrap: wrap; gap: 16px; }}
svg {{ max-width: 100%; height: auto; border: 1px solid {GRID}; border-radius: 8px; }}
.scroll {{ overflow-x: auto; }}
table {{ border-collapse: collapse; font-size: 13px; margin: 8px 0 16px; }}
th, td {{ border-bottom: 1px solid {GRID}; padding: 4px 8px; text-align: right; white-space: nowrap; }}
th {{ color: {INK_2}; font-weight: 600; position: sticky; top: 0; background: {SURFACE}; }}
td:first-child, th:first-child {{ text-align: left; }}
.warn {{ color: #9a4a00; }}
.callout {{ border-left: 4px solid {COLORS["Con pistas"]}; background: #eef4fb; border-radius: 8px; padding: 12px 16px; margin: 12px 0 16px; max-width: 760px; }}
.callout p {{ margin: 4px 0; color: {INK}; }}
.callout .big {{ font-size: 19px; }}
ul.takes {{ max-width: 760px; padding-left: 20px; }}
ul.takes li {{ margin: 6px 0; }}
details {{ margin: 8px 0 16px; }} summary {{ cursor: pointer; color: {INK_2}; }}
"""


def main():
    parser = argparse.ArgumentParser(description="Compara cada toma con la referencia (la preferida) de su shot.")
    parser.add_argument("--db", help="la base de datos shotlist.db (por omisión, la del servidor)")
    parser.add_argument("--salida", help="carpeta de resultados (por omisión, resultados/<fecha>)")
    parser.add_argument("--escena", type=int)
    parser.add_argument("--shot", type=int)
    parser.add_argument("--desde", help="solo tomas grabadas desde este día, AAAA-MM-DD (las referencias pueden ser anteriores)")
    args = parser.parse_args()

    db_path = args.db or default_db()
    if not db_path or not os.path.exists(db_path):
        sys.exit("No encuentro shotlist.db. Indica la ruta con --db.")
    if args.desde:
        try:
            dt.date.fromisoformat(args.desde)
        except ValueError:
            sys.exit("--desde debe ser una fecha AAAA-MM-DD")

    takes, has_condition = load(db_path, args.escena, args.shot, args.desde)
    out_dir = args.salida or os.path.join(os.path.dirname(os.path.abspath(__file__)), "resultados",
                                          dt.datetime.now().strftime("%Y%m%d_%H%M%S"))
    os.makedirs(out_dir, exist_ok=True)

    shots = {}
    for take in takes:
        shots.setdefault((take.scene, take.shot), []).append(take)

    analyzed, notes, shot_sections = [], [], []
    take_rows, mark_rows = [], []
    for (scene, shot), group in sorted(shots.items()):
        references = [t for t in group if t.preferida]
        reference = references[0] if references else None
        if reference is None or len(reference.samples) < 5:
            notes.append(f"Escena {scene}, shot {shot}: sin toma preferida con recorrido, no se compara ({len(group)} tomas).")
            continue
        reference.stops = find_stops(reference)
        others = [t for t in group if t is not reference and t.in_range and len(t.samples) >= 5]
        if not others:
            notes.append(f"Escena {scene}, shot {shot}: solo la referencia, nada que comparar.")
            continue
        for take in others:
            take.stops = find_stops(take)
            take.result = compare(take, reference)
            take.warning = ""
            if take.result["desfase_inicio_m"] > START_WARNING:
                take.warning = f"inicio a {take.result['desfase_inicio_m']:.1f} m de la referencia (¿se movió la marca de inicio?)"
            take.reference = reference
            take_rows.append(take_row(take, reference))
            for m in take.result["marcas"]:
                mark_rows.append([scene, shot, take.id, take.condition, m["marca"], m["t_ref"], m["alcanzada"],
                                  m["error_m"], m["retraso_s"], m["orientacion_deg"], m["distancia_minima_m"]])
        analyzed.extend(others)

        best = closest_take(others)
        svg = path_chart(reference, others, scene, shot, best)
        name = f"recorrido_e{scene}_s{shot}.svg"
        with open(os.path.join(out_dir, name), "w", encoding="utf-8") as f:
            f.write(svg)
        rows = [[t.id, t.date[:16].replace("T", " "), t.condition, t.result["duracion_s"], t.result["dif_duracion_s"],
                 f"{t.result['marcas_alcanzadas']}/{len(reference.stops)}", t.result["error_marca_m"],
                 t.result["retraso_marca_s"], t.result["error_orientacion_deg"], t.result["error_recorrido_m"],
                 t.result["error_sincronia_m"], t.warning] for t in others]
        ordered = [best] + sorted((t for t in others if t is not best), key=lambda t: t.id)
        items = "".join(
            f"<li><b>Toma {t.id}</b> ({t.condition.lower()}, {html.escape(t.date[:16].replace('T', ' '))})"
            f"{' · <b>la más parecida</b>' if t is best and len(others) > 1 else ''}: {html.escape(plain_summary(t, reference))}</li>"
            for t in ordered)
        headline = (f"La toma más parecida a la preferida es la <b>toma {best.id}</b>." if len(others) > 1 else
                    f"La <b>toma {best.id}</b> comparada con la preferida.")
        callout = (
            f"<div class='callout'><p class='big'>{headline}</p>"
            f"<p>{html.escape(plain_summary(best, reference))}</p></div>")
        shot_sections.append(
            f"<h2>Escena {scene}, shot {shot}</h2>"
            f"<p>La <b>preferida</b> (la toma que el director eligió como la buena) es la toma {reference.id} "
            f"({html.escape(reference.date[:16].replace('T', ' '))}), de {reference.duration:.1f} s. Cada toma se compara con ella.</p>"
            + callout + svg
            + "<p>Cómo leerlo: es el set visto desde arriba. La línea negra es por donde caminó el actor en la preferida; "
              f"la de color, la toma {best.id}" + (", y las grises, las demás tomas" if len(others) > 1 else "")
            + ". El círculo blanco es el inicio y el punto negro, la marca donde se detuvo en la preferida. "
              "Mientras más encimadas estén las líneas, más se parece la toma a la preferida.</p>"
            + f"<ul class='takes'>{items}</ul>"
            + "<details><summary>Números exactos</summary><div class='scroll'>" + html_table(
                ["Toma", "Fecha", "Condición", "Duración (s)", "Dif. duración (s)", "Marcas", "Error marca (m)",
                 "Retraso marca (s)", "Orientación (°)", "Error recorrido (m)", "Error sincronía (m)", "Aviso"], rows)
            + "</div></details>")

    write_csv(os.path.join(out_dir, "resumen_tomas.csv"), TAKE_COLUMNS, take_rows)
    write_csv(os.path.join(out_dir, "marcas.csv"), MARK_COLUMNS, mark_rows)

    summary = summarize(analyzed, "todos")
    for (scene, shot) in sorted({(t.scene, t.shot) for t in analyzed}):
        summary += summarize([t for t in analyzed if (t.scene, t.shot) == (scene, shot)], f"escena {scene} shot {shot}")
    write_csv(os.path.join(out_dir, "condiciones.csv"), SUMMARY_COLUMNS, [[row.get(c) for c in SUMMARY_COLUMNS] for row in summary])

    # The metric charts, all shots together, by condition.
    def per_mark(field, transform=lambda v: v):
        groups = []
        for condition in CONDITION_ORDER:
            vals = []
            for t in analyzed:
                if t.condition != condition:
                    continue
                for m in t.result["marcas"]:
                    if m["alcanzada"]:
                        vals.append((transform(m[field]), f"E{t.scene} S{t.shot} · toma {t.id} · M{m['marca']}: {m[field]:.2f}"))
            groups.append((condition, vals))
        return groups

    scope = "Todas las tomas" + (f" desde {args.desde}" if args.desde else "") + " · línea = mediana"
    charts = {
        "error_marca.svg": strip_chart("Qué tan lejos de la marca se detuvo", f"{scope} · un punto por marca alcanzada",
                                       "metros", per_mark("error_m")),
        "retraso_marca.svg": strip_chart("Cuándo llegó a la marca", f"{scope} · respecto a la referencia: + tarde, − temprano",
                                         "segundos", per_mark("retraso_s"), signed=True),
        "orientacion_marca.svg": strip_chart("Error de orientación en la marca", f"{scope} · la cabeza al detenerse",
                                             "grados", per_mark("orientacion_deg")),
        "error_recorrido.svg": strip_chart("Distancia al recorrido de la referencia", f"{scope} · un punto por toma (media)", "metros",
                                           [(c, [(t.result["error_recorrido_m"], f"E{t.scene} S{t.shot} · toma {t.id}: {t.result['error_recorrido_m']:.2f} m")
                                                 for t in analyzed if t.condition == c]) for c in CONDITION_ORDER]),
    }
    chart_html = []
    for name, svg in charts.items():
        if svg:
            with open(os.path.join(out_dir, name), "w", encoding="utf-8") as f:
                f.write(svg)
            chart_html.append(svg)

    summary_rows = [[row["alcance"], row["condicion"], row["n_tomas"], row["n_marcas"], row["marcas_alcanzadas_pct"],
                     row["error_marca_m_mediana"], row["retraso_abs_s_mediana"], row["error_orientacion_deg_mediana"],
                     row["error_recorrido_m_mediana"], row["error_sincronia_m_mediana"]] for row in summary]

    condition_note = "" if has_condition else (
        "<p class='warn'>Esta base de datos todavía no guarda la condición de cada toma (con o sin pistas): "
        "todas aparecen como «Sin dato». Las tomas grabadas con la versión de la app que tiene el interruptor de pistas sí la traen.</p>")
    page = [
        "<!doctype html><html lang='es'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width, initial-scale=1'>",
        "<title>Análisis de tomas</title>", f"<style>{PAGE_STYLE}</style></head><body><main>",
        "<h1>Análisis de tomas</h1>",
        f"<p>Cada toma comparada con la referencia de su shot (la toma preferida). Base de datos: {html.escape(db_path)} · "
        f"generado {dt.datetime.now().strftime('%Y-%m-%d %H:%M')} · {len(analyzed)} tomas en {len(shot_sections)} shots"
        + (f" · desde {html.escape(args.desde)}" if args.desde else "") + ".</p>",
        condition_note,
    ]
    # One shot: its plain comparison first; the summary by condition (made for the pilot) after it.
    if len(shot_sections) == 1:
        page += shot_sections
    page += [
        "<p>Una <b>marca</b> es un lugar donde la referencia se detuvo al menos 1 s tras moverse al menos 0.5 m. "
        f"Una toma la <b>alcanza</b> si se detiene a menos de {HIT_RADIUS} m de ella, en orden. "
        "<b>Error de recorrido</b>: distancia media del camino de la toma al de la referencia, sin importar el tiempo. "
        "<b>Error de sincronía</b>: distancia media a donde estaba la referencia en el mismo instante (camino y tiempo juntos). "
        "Pasa el cursor sobre un punto o una línea para ver su toma.</p>",
        "<h2>Resumen por condición</h2><div class='scroll'>",
        html_table(["Alcance", "Condición", "Tomas", "Marcas", "Marcas alcanzadas (%)", "Error marca (m)", "Desfase tiempo (s)",
                    "Orientación (°)", "Error recorrido (m)", "Error sincronía (m)"], summary_rows),
        "</div><p>Valores: medianas. Las medias están en condiciones.csv.</p>",
        "<div class='charts'>" + "".join(chart_html) + "</div>",
    ]
    if notes:
        page.append("<h2>No comparados</h2><ul>" + "".join(f"<li>{html.escape(n)}</li>" for n in notes) + "</ul>")
    if len(shot_sections) != 1:
        page += shot_sections
    page.append("</main></body></html>")
    with open(os.path.join(out_dir, "informe.html"), "w", encoding="utf-8") as f:
        f.write("\n".join(page))

    print(f"{len(analyzed)} tomas comparadas en {len(shot_sections)} shots; {len(notes)} shots sin comparar.")
    print(f"Resultados en {out_dir}")
    print(f"Abre {os.path.join(out_dir, 'informe.html')}")


if __name__ == "__main__":
    main()
