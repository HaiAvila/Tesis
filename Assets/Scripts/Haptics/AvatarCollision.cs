using System.Collections.Generic;
using UnityEngine;

// Keeps the avatar's limbs out of its body and out of each other. The pods give rotations
// only, each a few degrees off, and the Y Bot isn't the actor's shape - so an arm resting at
// the side, hands on the thighs or a clap would otherwise sink in. A real limb can't, so any
// overlap here is an error, and is removed until the two just touch.
//
// The body as capsules, sized from the avatar's own mesh (after the body measurements):
// - torso: a stack of horizontal capsules from the hip joints to the armpits, each as wide
//   and deep as the mesh at that height (a capsule along the body would be either too wide
//   front-to-back or too thin side-to-side);
// - head;
// - per side: upper arm, forearm, hand, thigh, shin. The upper arm and the thigh only from
//   ~40% of their length out: their other end is always inside the torso / the other thigh.
//
// Every frame, after PodAvatar has posed the bones, each pair that can touch is checked and
// an overlap is pushed out by turning the limb at its root - the arm at the shoulder joint,
// the leg at the hip - so the elbow and knee angles, from two pods each, stay as they were.
// Arm or hand against the torso, head or a thigh: only the arm moves. Two limbs against each
// other (a clap, crossed arms, knees together): each moves half. A few passes, since fixing
// one contact can cause another. Runs on the machine with the pods, so the synced (and later
// recorded) pose is the corrected one.
public class AvatarCollision
{
    class Part
    {
        public string name;
        public Transform frame;      // the capsule is fixed in this bone's space
        public Vector3 localA, localB;
        public float radius;         // world meters, at A...
        public float radiusB;        // ...and at B (limbs taper: a forearm is thinner at the wrist)
        public float RadiusAt(float t) => Mathf.Lerp(radius, radiusB, t);
        public Transform pivot;      // turned to push it out; null = never moves (torso, head)
        public Vector3 A => frame.TransformPoint(localA);
        public Vector3 B => frame.TransformPoint(localB);
    }

    struct Pair
    {
        public Part a, b;
        public bool both;            // false: only a moves
    }

    readonly List<Part> parts = new List<Part>();
    readonly List<Pair> pairs = new List<Pair>();

    public int Passes = 4;
    public float MaxAnglePerPush = 10f;   // degrees
    public float Skin = 0.003f;           // overlap allowed (m): flesh gives a little

    // Last frame, for the Inspector / tests.
    public int LastContacts { get; private set; }
    public float LastMaxPenetrationBefore { get; private set; }
    public float LastMaxPenetrationAfter { get; private set; }
    public string Summary { get; private set; } = "";
    public string LastWorstPair { get; private set; } = "";

    public AvatarCollision(Animator avatar)
    {
        Build(avatar);
    }

    // --- shapes, from the mesh in its rest pose --------------------------------------------

    void Build(Animator avatar)
    {
        Transform root = avatar.transform;
        Quaternion toAvatar = Quaternion.Inverse(root.rotation);

        // Every vertex of the posed mesh, world space, with the bone that moves it most.
        var verts = new List<Vector3>();
        var owner = new List<Transform>();
        var mesh = new Mesh();
        foreach (SkinnedMeshRenderer smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            smr.BakeMesh(mesh, true);
            Matrix4x4 m = smr.transform.localToWorldMatrix;
            Vector3[] v = mesh.vertices;
            BoneWeight[] w = smr.sharedMesh.boneWeights;
            Transform[] bones = smr.bones;
            for (int i = 0; i < v.Length && i < w.Length; i++)
            {
                verts.Add(m.MultiplyPoint3x4(v[i]));
                owner.Add(bones[w[i].boneIndex0]);
            }
        }
        Object.Destroy(mesh);

        Transform B(HumanBodyBones b) => avatar.GetBoneTransform(b);

        // Torso bones (not the neck, shoulders, arms or legs).
        var torsoBones = new List<Transform>();
        foreach (HumanBodyBones b in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            if (B(b) != null) torsoBones.Add(B(b));

        // Avatar frame (faces +Z, up +Y), world units, origin at the root.
        Vector3 Local(Vector3 p) => toAvatar * (p - root.position);

        float hipY = (Local(B(HumanBodyBones.LeftUpperLeg).position).y + Local(B(HumanBodyBones.RightUpperLeg).position).y) / 2f;
        float shoulderY = (Local(B(HumanBodyBones.LeftUpperArm).position).y + Local(B(HumanBodyBones.RightUpperArm).position).y) / 2f;
        float bottom = hipY - 0.03f, top = shoulderY - 0.06f;
        const int slices = 7;
        float band = (top - bottom) / slices;

        var xs = new List<float>();
        var zs = new List<float>();
        for (int s = 0; s < slices; s++)
        {
            float y0 = bottom + s * band, y1 = y0 + band, ym = (y0 + y1) / 2f;
            xs.Clear(); zs.Clear();
            for (int i = 0; i < verts.Count; i++)
            {
                if (!torsoBones.Contains(owner[i]))
                    continue;
                Vector3 p = Local(verts[i]);
                if (p.y < y0 || p.y >= y1)
                    continue;
                xs.Add(p.x); zs.Add(p.z);
            }
            if (xs.Count < 20)
                continue;

            float x0 = Percentile(xs, 0.03f), x1 = Percentile(xs, 0.97f);
            float z0 = Percentile(zs, 0.03f), z1 = Percentile(zs, 0.97f);
            float cx = (x0 + x1) / 2f, cz = (z0 + z1) / 2f, hw = (x1 - x0) / 2f, hd = (z1 - z0) / 2f;
            float half = Mathf.Max(0f, hw - hd);

            // Fixed in the torso bone just below it.
            Transform frame = torsoBones[0];
            foreach (Transform t in torsoBones)
                if (Local(t.position).y <= ym) frame = t;

            Vector3 WorldOf(Vector3 avatarLocal) => root.position + root.rotation * avatarLocal;
            parts.Add(new Part
            {
                name = $"torso {s + 1}",
                frame = frame,
                localA = frame.InverseTransformPoint(WorldOf(new Vector3(cx - half, ym, cz))),
                localB = frame.InverseTransformPoint(WorldOf(new Vector3(cx + half, ym, cz))),
                radius = hd, radiusB = hd,
            });
        }
        int torsoCount = parts.Count;

        // Head: a vertical capsule through the head's vertices.
        Transform head = B(HumanBodyBones.Head);
        Part headPart = null;
        if (head != null)
        {
            var hs = new List<Vector3>();
            for (int i = 0; i < verts.Count; i++)
                if (owner[i] == head || owner[i].IsChildOf(head)) hs.Add(Local(verts[i]));
            if (hs.Count > 20)
            {
                Vector3 min = hs[0], max = hs[0];
                foreach (Vector3 p in hs) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                Vector3 c = (min + max) / 2f;
                float r = Mathf.Min(max.x - min.x, max.z - min.z) / 2f;
                float half = Mathf.Max(0f, (max.y - min.y) / 2f - r);
                Vector3 WorldOf(Vector3 avatarLocal) => root.position + root.rotation * avatarLocal;
                headPart = new Part
                {
                    name = "head", frame = head, radius = r, radiusB = r,
                    localA = head.InverseTransformPoint(WorldOf(c - Vector3.up * half)),
                    localB = head.InverseTransformPoint(WorldOf(c + Vector3.up * half)),
                };
                parts.Add(headPart);
            }
        }

        // Limbs.
        Part Limb(string name, HumanBodyBones bone, HumanBodyBones child, float from, HumanBodyBones pivot, float percentile)
        {
            Transform t = B(bone), c = B(child);
            if (t == null || c == null) return null;
            Vector3 a = t.position, b = c.position;
            // Thickness near each end (not at the joint balls themselves), then the capsule's
            // radius at its start (`from` along the bone) and at the child joint.
            float near = AxisRadius(verts, owner, t, false, a, b, percentile, 0.15f, 0.35f);
            float far = AxisRadius(verts, owner, t, false, a, b, percentile, 0.65f, 0.85f);
            float RadiusAlong(float u) => Mathf.LerpUnclamped(near, far, (u - 0.25f) / 0.5f);
            var part = new Part
            {
                name = name, frame = t, pivot = B(pivot),
                radius = Mathf.Max(0.01f, RadiusAlong(from)), radiusB = Mathf.Max(0.01f, RadiusAlong(1f)),
                localA = t.InverseTransformPoint(Vector3.Lerp(a, b, from)),
                localB = t.InverseTransformPoint(b),
            };
            parts.Add(part);
            return part;
        }

        // A hand: from the wrist along the forearm's direction, as long as the hand's mesh.
        Part Hand(string name, HumanBodyBones hand, HumanBodyBones forearm, HumanBodyBones pivot)
        {
            Transform h = B(hand), f = B(forearm);
            if (h == null || f == null) return null;
            Vector3 dir = (h.position - f.position).normalized;
            float length = 0f;
            for (int i = 0; i < verts.Count; i++)
                if (owner[i] == h || owner[i].IsChildOf(h))
                    length = Mathf.Max(length, Vector3.Dot(verts[i] - h.position, dir));
            if (length < 0.03f) length = 0.15f;
            Vector3 a = h.position, b = h.position + dir * length;
            // A hand is flat: its radius is half its thickness, across the palm - the avatar's
            // up in the T-pose (palms down), not its width.
            var ys = new List<float>();
            for (int i = 0; i < verts.Count; i++)
                if (owner[i] == h || owner[i].IsChildOf(h)) ys.Add(Local(verts[i]).y);
            float r = ys.Count > 10 ? (Percentile(ys, 0.8f) - Percentile(ys, 0.2f)) / 2f : 0.015f;
            var part = new Part
            {
                name = name, frame = h, radius = r, radiusB = r, pivot = B(pivot),
                localA = h.InverseTransformPoint(a + dir * r),
                localB = h.InverseTransformPoint(b - dir * r),
            };
            parts.Add(part);
            return part;
        }

        Part[] ua = new Part[2], fa = new Part[2], hand = new Part[2], thigh = new Part[2], shin = new Part[2];
        for (int s = 0; s < 2; s++)
        {
            bool left = s == 0;
            string side = left ? "left" : "right";
            HumanBodyBones upperArm = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            HumanBodyBones lowerArm = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            HumanBodyBones handBone = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            HumanBodyBones upperLeg = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
            HumanBodyBones lowerLeg = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            HumanBodyBones foot = left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot;

            ua[s] = Limb($"{side} upper arm", upperArm, lowerArm, 0.4f, upperArm, 0.5f);
            fa[s] = Limb($"{side} forearm", lowerArm, handBone, 0f, upperArm, 0.5f);
            hand[s] = Hand($"{side} hand", handBone, lowerArm, upperArm);
            thigh[s] = Limb($"{side} thigh", upperLeg, lowerLeg, 0.35f, upperLeg, 0.5f);
            shin[s] = Limb($"{side} shin", lowerLeg, foot, 0f, upperLeg, 0.5f);
        }

        void Add(Part a, Part b, bool both)
        {
            if (a != null && b != null) pairs.Add(new Pair { a = a, b = b, both = both });
        }

        var body = parts.GetRange(0, torsoCount);
        if (headPart != null) body.Add(headPart);
        for (int s = 0; s < 2; s++)
        {
            int o = 1 - s;
            foreach (Part t in body)
            {
                Add(ua[s], t, false);
                Add(fa[s], t, false);
                Add(hand[s], t, false);
            }
            for (int l = 0; l < 2; l++)
            {
                Add(fa[s], thigh[l], false);
                Add(hand[s], thigh[l], false);
            }
            Add(hand[s], ua[o], true);
            Add(fa[s], ua[o], true);
            Add(hand[s], fa[o], true);
        }
        Add(fa[0], fa[1], true);
        Add(hand[0], hand[1], true);
        Add(thigh[0], thigh[1], true);
        Add(shin[0], shin[1], true);
        Add(shin[0], thigh[1], true);
        Add(shin[1], thigh[0], true);

        var sb = new System.Text.StringBuilder();
        foreach (Part p in parts)
            sb.Append(p.radius == p.radiusB ? $"{p.name} {p.radius * 100f:F1}, " : $"{p.name} {p.radius * 100f:F1}→{p.radiusB * 100f:F1}, ");
        Summary = $"{parts.Count} capsules (radius cm: {sb.ToString().TrimEnd(',', ' ')}), {pairs.Count} pairs checked";
    }

    // How thick a limb is: the given percentile of its vertices' distance from the bone axis,
    // over the middle of the segment (the joint balls at the ends don't count).
    static float AxisRadius(List<Vector3> verts, List<Transform> owner, Transform bone, bool withChildren, Vector3 a, Vector3 b, float percentile, float t0, float t1)
    {
        var d = new List<float>();
        Vector3 ab = b - a;
        float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
        for (int i = 0; i < verts.Count; i++)
        {
            if (owner[i] != bone && !(withChildren && owner[i].IsChildOf(bone)))
                continue;
            float t = Vector3.Dot(verts[i] - a, ab) / len2;
            if (t < t0 || t > t1)
                continue;
            d.Add(Vector3.Distance(verts[i], a + ab * t));
        }
        return d.Count > 10 ? Percentile(d, percentile) : 0.04f;
    }

    static float Percentile(List<float> values, float p)
    {
        values.Sort();
        return values[Mathf.Clamp(Mathf.RoundToInt(p * (values.Count - 1)), 0, values.Count - 1)];
    }

    // --- every frame ---------------------------------------------------------------------

    public void Resolve()
    {
        LastMaxPenetrationBefore = Measure(out int contacts);
        LastContacts = contacts;
        if (contacts == 0)
        {
            LastMaxPenetrationAfter = 0f;
            return;
        }

        for (int pass = 0; pass < Passes; pass++)
        {
            bool any = false;
            foreach (Pair pair in pairs)
            {
                if (!Penetration(pair, out Vector3 pa, out Vector3 pb, out Vector3 n, out float depth))
                    continue;
                any = true;
                if (pair.both)
                {
                    Push(pair.a, pa, n * (depth / 2f));
                    Push(pair.b, pb, -n * (depth / 2f));
                }
                else
                    Push(pair.a, pa, n * depth);
            }
            if (!any)
                break;
        }

        LastMaxPenetrationAfter = Measure(out _);
    }

    float Measure(out int contacts)
    {
        contacts = 0;
        float worst = 0f;
        foreach (Pair pair in pairs)
        {
            if (!Penetration(pair, out _, out _, out _, out float depth))
                continue;
            contacts++;
            if (depth > worst)
            {
                worst = depth;
                LastWorstPair = $"{pair.a.name} / {pair.b.name}";
            }
        }
        return worst;
    }

    bool Penetration(Pair pair, out Vector3 pa, out Vector3 pb, out Vector3 n, out float depth)
    {
        ClosestPoints(pair.a.A, pair.a.B, pair.b.A, pair.b.B, out pa, out pb, out float sa, out float sb);
        Vector3 d = pa - pb;
        float dist = d.magnitude;
        depth = pair.a.RadiusAt(sa) + pair.b.RadiusAt(sb) - dist - Skin;
        if (depth <= 0f)
        {
            n = Vector3.zero;
            return false;
        }

        if (dist > 1e-5f)
            n = d / dist;
        else
        {
            // Axes crossing: push away from the other part's middle, sideways.
            Vector3 away = pa - (pair.b.A + pair.b.B) / 2f;
            away = Vector3.ProjectOnPlane(away, pair.b.B - pair.b.A);
            n = away.sqrMagnitude > 1e-8f ? away.normalized : Vector3.up;
        }
        return true;
    }

    // Turns the part's pivot bone so the point p moves by delta (as far as a turn about the
    // pivot can move it), at most MaxAnglePerPush.
    void Push(Part part, Vector3 p, Vector3 delta)
    {
        if (part.pivot == null)
            return;
        Vector3 j = part.pivot.position;
        Vector3 from = p - j;
        if (from.sqrMagnitude < 1e-4f)
            return;
        Quaternion turn = Quaternion.FromToRotation(from, from + delta);
        turn.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) { angle = 360f - angle; axis = -axis; }
        if (angle < 0.01f)
            return;
        if (angle > MaxAnglePerPush)
            turn = Quaternion.AngleAxis(MaxAnglePerPush, axis);
        part.pivot.rotation = turn * part.pivot.rotation;
    }

    // Closest points between segments p1-q1 and p2-q2 (Ericson, Real-Time Collision Detection).
    static void ClosestPoints(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out Vector3 c1, out Vector3 c2, out float s, out float t)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        if (a <= 1e-8f && e <= 1e-8f) { c1 = p1; c2 = p2; s = t = 0f; return; }
        if (a <= 1e-8f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-8f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    // --- gizmos ----------------------------------------------------------------------------

    public void DrawGizmos()
    {
        foreach (Part p in parts)
        {
            Gizmos.color = p.pivot == null ? new Color(1f, 0.6f, 0.2f, 0.8f) : new Color(0.3f, 0.9f, 1f, 0.8f);
            Vector3 a = p.A, b = p.B;
            Gizmos.DrawWireSphere(a, p.radius);
            Gizmos.DrawWireSphere(b, p.radiusB);
            Vector3 axis = b - a;
            if (axis.sqrMagnitude < 1e-8f)
                continue;
            Vector3 side = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis.normalized, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 side2 = Vector3.Cross(axis.normalized, side);
            foreach (Vector3 dir in new[] { side, -side, side2, -side2 })
                Gizmos.DrawLine(a + dir * p.radius, b + dir * p.radiusB);
        }
    }
}
