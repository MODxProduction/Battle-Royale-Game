using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds a playable character prefab for another humanoid model out of the working
/// player prefab, so the new character gets every script, weapon, holster, armour piece
/// and ragdoll hitbox exactly as it is set up there.
///
/// The existing bone objects are kept and turned into the new model's skeleton (renamed,
/// re-parented, moved to its bind pose) instead of being replaced. Everything that hangs
/// off a bone or points at one - the guns in the hands, JUInventory's item lists, the
/// ragdoll joints, the footstep feet - therefore stays wired without any remapping.
///
/// Two rigs never share bone axes or proportions, so both are first put in the same
/// humanoid pose; each prop keeps its place relative to its bone in that pose, and the
/// hitboxes are converted into the new bone's axes and stretched to its length. The
/// holstered props on the hips and back are then leaned and slid until they rest on the
/// new body's skin, so a slimmer or heavier model does not leave them floating or buried.
///
/// Characters wear no armour, and every hitbox is fitted to the new body's skin, so a
/// shot that misses the body misses the hitbox too.
///
/// Select a model in Assets/Prefabs/Character Models and run
/// Tools > Last Stand > Build Character Prefab From Selected Model.
/// Tools > Last Stand > Strip Armour And Fit Hitboxes redoes that last step on every
/// character prefab already built.
/// </summary>
public static class CharacterModelPrefabBuilder
{
    private const string SourcePrefab = "Assets/Prefabs/Character.prefab";
    private const string OutputFolder = "Assets/Prefabs";
    private const string OldSkeletonRoot = "Armature";
    private const string MenuPath = "Tools/Last Stand/Build Character Prefab From Selected Model";
    private const string FinishMenuPath = "Tools/Last Stand/Strip Armour And Fit Hitboxes";
    private const string ModelsFolder = "Assets/Prefabs/Character Models";

    private const int GapBands = 12;
    private const float SkinSearchMargin = 0.15f;
    private const float MaxHolsterTilt = 20f;
    private const float BearingStep = 10f;
    private const int CellsPerBand = 1000;
    private const float MinGapRadius = 0.02f;
    private const int SlidePasses = 4;

    private static readonly HumanBodyBones[] TorsoBones =
    {
        HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest
    };

    private struct Holster
    {
        public Transform prop;
        public Transform bone;
        public float gap;
        public float lean;
    }

    private struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    [MenuItem(MenuPath)]
    private static void BuildSelected()
    {
        foreach (GameObject model in Selection.GetFiltered<GameObject>(SelectionMode.Assets))
            Debug.Log("Built " + Build(AssetDatabase.GetAssetPath(model)));
    }

    [MenuItem(MenuPath, true)]
    private static bool CanBuildSelected()
    {
        GameObject model = Selection.activeObject as GameObject;
        return model != null && PrefabUtility.GetPrefabAssetType(model) == PrefabAssetType.Model;
    }

    [MenuItem(FinishMenuPath)]
    private static void FinishAll()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { ModelsFolder }))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(guid));
            string prefabPath = OutputFolder + "/" + name + ".prefab";

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                continue;

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);

            try
            {
                StripArmour(root);
                FitHitboxes(root);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            KeepAssetId(prefabPath);
            Debug.Log("Stripped armour and fitted hitboxes on " + prefabPath);
        }
    }

    /// <summary>Returns the path of the prefab it wrote.</summary>
    public static string Build(string modelPath)
    {
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        Animator modelAnimator = model != null ? model.GetComponent<Animator>() : null;

        if (modelAnimator == null || modelAnimator.avatar == null || !modelAnimator.avatar.isHuman)
            throw new System.InvalidOperationException(modelPath + " is not a model with a Humanoid rig.");

        string outputPath = OutputFolder + "/" + model.name + ".prefab";
        GameObject root = PrefabUtility.LoadPrefabContents(SourcePrefab);

        try
        {
            // Work at the origin so world space and character space are the same thing.
            Vector3 rootPosition = root.transform.localPosition;
            Quaternion rootRotation = root.transform.localRotation;
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            Reskin(root, model);
            StripArmour(root);
            FitHitboxes(root);

            root.transform.localPosition = rootPosition;
            root.transform.localRotation = rootRotation;
            PrefabUtility.SaveAsPrefabAsset(root, outputPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        KeepAssetId(outputPath);
        return outputPath;
    }

    /// <summary>
    /// Mirror zeroes the asset id while a prefab is open for editing and afterwards only
    /// works it out in memory (reading it does that); a build needs it on disk.
    /// </summary>
    private static void KeepAssetId(string prefabPath)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        Mirror.NetworkIdentity identity = prefab.GetComponent<Mirror.NetworkIdentity>();

        if (identity != null && identity.assetId != 0)
        {
            EditorUtility.SetDirty(identity);
            PrefabUtility.SavePrefabAsset(prefab);
        }
    }

    /// <summary>Removes every armour piece. JUInventory collects its items at runtime, so nothing is left pointing at them.</summary>
    private static void StripArmour(GameObject root)
    {
        var pieces = new List<GameObject>();

        foreach (JUTPS.ArmorSystem.Armor armour in root.GetComponentsInChildren<JUTPS.ArmorSystem.Armor>(true))
        {
            if (armour.Parts != null)
                pieces.AddRange(armour.Parts);

            pieces.Add(armour.gameObject);
        }

        foreach (GameObject piece in pieces)
        {
            if (piece != null)
                Object.DestroyImmediate(piece);
        }
    }

    /// <summary>
    /// Sizes each body part's hitbox to the skin its bone moves. Every vertex counts for
    /// the nearest bone up its chain that has a hitbox (a hand for the forearm, the neck
    /// for the chest), and a few stray vertices at the edges are left out.
    /// </summary>
    private static void FitHitboxes(GameObject root)
    {
        var skinOf = new Dictionary<Transform, List<Vector3>>();

        foreach (JUTPS.ArmorSystem.DamageableBodyPart part in root.GetComponentsInChildren<JUTPS.ArmorSystem.DamageableBodyPart>(true))
            skinOf[part.transform] = new List<Vector3>();

        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Mesh mesh = skin.sharedMesh;
            Transform[] bones = skin.bones;

            if (mesh == null || bones.Length == 0)
                continue;

            Matrix4x4[] bindposes = mesh.bindposes;
            var skinning = new Matrix4x4[bones.Length];
            var owner = new Transform[bones.Length];

            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null)
                    continue;

                skinning[b] = bones[b].localToWorldMatrix * bindposes[b];

                for (Transform t = bones[b]; t != null && owner[b] == null; t = t.parent)
                {
                    if (skinOf.ContainsKey(t))
                        owner[b] = t;
                }
            }

            Vector3[] vertices = mesh.vertices;
            BoneWeight[] weights = mesh.boneWeights;

            for (int i = 0; i < vertices.Length && i < weights.Length; i++)
            {
                BoneWeight w = weights[i];
                int heaviest = w.boneIndex0;
                float most = w.weight0;
                if (w.weight1 > most) { heaviest = w.boneIndex1; most = w.weight1; }
                if (w.weight2 > most) { heaviest = w.boneIndex2; most = w.weight2; }
                if (w.weight3 > most) { heaviest = w.boneIndex3; }

                Transform part = owner[heaviest];

                if (part == null)
                    continue;

                Vector3 world = skinning[w.boneIndex0].MultiplyPoint3x4(vertices[i]) * w.weight0
                              + skinning[w.boneIndex1].MultiplyPoint3x4(vertices[i]) * w.weight1
                              + skinning[w.boneIndex2].MultiplyPoint3x4(vertices[i]) * w.weight2
                              + skinning[w.boneIndex3].MultiplyPoint3x4(vertices[i]) * w.weight3;

                skinOf[part].Add(part.InverseTransformPoint(world));
            }
        }

        foreach (KeyValuePair<Transform, List<Vector3>> pair in skinOf)
        {
            List<Vector3> points = pair.Value;
            Collider collider = pair.Key.GetComponent<Collider>();

            if (points.Count < 20 || collider == null)
                continue;

            Vector3 mid = new Vector3(Middle(points, 0), Middle(points, 1), Middle(points, 2));

            if (collider is CapsuleCollider capsule)
            {
                int a = capsule.direction;
                int u = (a + 1) % 3;
                int v = (a + 2) % 3;
                float start = Quantile(points.ConvertAll(p => p[a]), 0.02f);
                float end = Quantile(points.ConvertAll(p => p[a]), 0.98f);
                // Thickness from the middle of the limb only: a foot or a hand at the end
                // sticks out sideways and would fatten the whole capsule.
                float shaftStart = Mathf.Lerp(start, end, 0.15f);
                float shaftEnd = Mathf.Lerp(start, end, 0.85f);
                List<Vector3> shaft = points.FindAll(p => p[a] >= shaftStart && p[a] <= shaftEnd);
                Vector2 axisMid = new Vector2(Middle(shaft, u), Middle(shaft, v));
                float radius = Quantile(shaft.ConvertAll(p => (new Vector2(p[u], p[v]) - axisMid).magnitude), 0.8f);

                Vector3 center = mid;
                center[a] = (start + end) * 0.5f;
                center[u] = axisMid.x;
                center[v] = axisMid.y;
                capsule.center = center;
                capsule.radius = radius;
                capsule.height = Mathf.Max(end - start, 2f * radius);
            }
            else if (collider is BoxCollider box)
            {
                Vector3 size = Vector3.zero;

                for (int i = 0; i < 3; i++)
                {
                    int axis = i;
                    size[i] = Quantile(points.ConvertAll(p => p[axis]), 0.95f) - Quantile(points.ConvertAll(p => p[axis]), 0.05f);
                }

                box.center = mid;
                box.size = size;
            }
            else if (collider is SphereCollider sphere)
            {
                sphere.center = mid;
                sphere.radius = Quantile(points.ConvertAll(p => (p - mid).magnitude), 0.8f);
            }
        }
    }

    /// <summary>Halfway between the 5th and 95th percentile on one axis.</summary>
    private static float Middle(List<Vector3> points, int axis)
    {
        List<float> values = points.ConvertAll(p => p[axis]);
        return (Quantile(values, 0.05f) + Quantile(values, 0.95f)) * 0.5f;
    }

    private static float Quantile(List<float> values, float q)
    {
        values.Sort();
        return values[Mathf.Clamp(Mathf.RoundToInt(q * (values.Count - 1)), 0, values.Count - 1)];
    }

    private static void Reskin(GameObject root, GameObject model)
    {
        Animator animator = root.GetComponent<Animator>();
        Transform oldSkeleton = root.transform.Find(OldSkeletonRoot);
        SkinnedMeshRenderer[] oldSkins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        GameObject donor = Object.Instantiate(model, root.transform);
        donor.transform.localPosition = Vector3.zero;
        donor.transform.localRotation = Quaternion.identity;
        Avatar newAvatar = donor.GetComponent<Animator>().avatar;

        // The donor's bones, parents first, with the pose they are imported in.
        var donorBones = new List<Transform>();
        var bindPose = new Dictionary<Transform, Pose>();

        foreach (Transform t in donor.GetComponentsInChildren<Transform>(true))
        {
            if (t == donor.transform || t.GetComponent<Renderer>() != null)
                continue;

            donorBones.Add(t);
            bindPose[t] = new Pose { position = t.localPosition, rotation = t.localRotation, scale = t.localScale };
        }

        var oldByName = new Dictionary<string, Transform>();

        foreach (Transform t in oldSkeleton.GetComponentsInChildren<Transform>(true))
        {
            if (!oldByName.ContainsKey(t.name))
                oldByName[t.name] = t;
        }

        // Model bones are "mixamorig4:Hips", the player prefab's are plain "Hips".
        var oldOf = new Dictionary<Transform, Transform>();
        var isOldBone = new HashSet<Transform>();

        foreach (Transform bone in donorBones)
        {
            string plainName = bone.name.Substring(bone.name.LastIndexOf(':') + 1);

            if (oldByName.TryGetValue(plainName, out Transform old))
            {
                oldOf[bone] = old;
                isOldBone.Add(old);
            }
        }

        Transform oldShoulder = FindHumanBone(animator.avatar, HumanBodyBones.LeftUpperArm, oldSkeleton);
        float oldShoulderHeight = oldShoulder != null ? oldShoulder.position.y : 0f;

        // How far each holstered prop sits off the old body, before anything moves.
        var holsters = new List<Holster>();
        var bodyVertices = new List<Vector3>();
        var bodyTriangles = new List<int>();
        BakeBody(oldSkins, bodyVertices, bodyTriangles);

        foreach (HumanBodyBones torsoBone in TorsoBones)
        {
            Transform bone = FindHumanBone(animator.avatar, torsoBone, oldSkeleton);

            if (bone == null)
                continue;

            foreach (Transform prop in bone)
            {
                // Armour is a shell around the body, not something resting against it.
                if (isOldBone.Contains(prop) || prop.GetComponent<JUTPS.ArmorSystem.Armor>() != null)
                    continue;

                CollectHolsters(prop, bone, bodyVertices, bodyTriangles, holsters);
            }
        }

        // Same humanoid pose on both rigs, so "relative to the hand" means the same thing.
        HumanPose humanPose = new HumanPose();

        using (var oldHandler = new HumanPoseHandler(animator.avatar, root.transform))
            oldHandler.GetHumanPose(ref humanPose);

        using (var newHandler = new HumanPoseHandler(newAvatar, donor.transform))
            newHandler.SetHumanPose(ref humanPose);

        // Measure everything while both skeletons still stand in that pose...
        var propPoses = new Dictionary<Transform, Pose>();
        var fixups = new List<System.Action>();

        foreach (KeyValuePair<Transform, Transform> pair in oldOf)
        {
            Transform bone = pair.Key;
            Transform old = pair.Value;
            Quaternion toBone = Quaternion.Inverse(bone.rotation);

            foreach (Transform child in old)
            {
                if (isOldBone.Contains(child))
                    continue;

                propPoses[child] = new Pose
                {
                    position = Divide(toBone * (child.position - old.position), bone.lossyScale),
                    rotation = toBone * child.rotation,
                    scale = Divide(child.lossyScale, bone.lossyScale)
                };
            }

            CollectHitboxFixups(old, bone, LengthRatio(old, bone, oldOf), fixups);
        }

        // Swapping the avatar makes the Animator put every bone it had bound back where it
        // found it, so it has to happen before the bones are moved, not after.
        animator.avatar = newAvatar;

        // ...then turn the old bones into the new skeleton.
        var finalOf = new Dictionary<Transform, Transform>();

        foreach (Transform bone in donorBones)
        {
            Transform parent = bone.parent == donor.transform ? root.transform : finalOf[bone.parent];

            if (!oldOf.TryGetValue(bone, out Transform target))
            {
                target = new GameObject().transform;
                target.gameObject.layer = parent.gameObject.layer;
            }

            target.SetParent(parent, false);
            target.name = bone.name;
            target.localPosition = bindPose[bone].position;
            target.localRotation = bindPose[bone].rotation;
            target.localScale = bindPose[bone].scale;
            finalOf[bone] = target;
        }

        foreach (KeyValuePair<Transform, Pose> prop in propPoses)
        {
            prop.Key.localPosition = prop.Value.position;
            prop.Key.localRotation = prop.Value.rotation;
            prop.Key.localScale = prop.Value.scale;
        }

        foreach (System.Action fixup in fixups)
            fixup();

        if (oldSkeleton.GetComponentsInChildren<Transform>(true).Length > CountEmptyChain(oldSkeleton))
            throw new System.InvalidOperationException("Objects were left behind under " + OldSkeletonRoot + "; the model's skeleton does not cover the player prefab's.");

        int siblingIndex = oldSkeleton.GetSiblingIndex();
        Object.DestroyImmediate(oldSkeleton.gameObject);

        foreach (Transform bone in donorBones)
        {
            if (bone.parent == donor.transform)
                finalOf[bone].SetSiblingIndex(siblingIndex);
        }

        // Body: the model's skinned meshes, bound to the converted skeleton.
        SkinnedMeshRenderer[] newSkins = donor.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        foreach (SkinnedMeshRenderer skin in newSkins)
        {
            Transform[] bones = skin.bones;

            for (int i = 0; i < bones.Length; i++)
                bones[i] = bones[i] != null ? finalOf[bones[i]] : null;

            skin.bones = bones;
            skin.rootBone = skin.rootBone != null ? finalOf[skin.rootBone] : null;
            skin.transform.SetParent(root.transform, false);
            skin.transform.SetSiblingIndex(oldSkins[0].transform.GetSiblingIndex());
            skin.gameObject.layer = oldSkins[0].gameObject.layer;
            skin.updateWhenOffscreen = oldSkins[0].updateWhenOffscreen;
            skin.skinnedMotionVectors = oldSkins[0].skinnedMotionVectors;
        }

        foreach (SkinnedMeshRenderer skin in oldSkins)
            Object.DestroyImmediate(skin.gameObject);

        bodyVertices.Clear();
        bodyTriangles.Clear();
        BakeBody(newSkins, bodyVertices, bodyTriangles);

        // Parents are listed before their children.
        foreach (Holster holster in holsters)
            RestOnBody(holster, bodyVertices, bodyTriangles);

        Object.DestroyImmediate(donor);

        // The guns are held around a pivot tuned to the old shoulder height.
        Transform newShoulder = FindHumanBone(newAvatar, HumanBodyBones.LeftUpperArm, root.transform);
        JUTPS.JUCharacterController character = root.GetComponent<JUTPS.JUCharacterController>();

        if (oldShoulder != null && newShoulder != null && character != null && character.PivotItemRotation != null)
            character.PivotItemRotation.transform.position += Vector3.up * (newShoulder.position.y - oldShoulderHeight);
    }

    /// <summary>
    /// Ragdoll hitboxes are defined in bone space: turn them into the new bone's axes and
    /// units, and stretch the limb capsules to the new bone's length.
    /// </summary>
    private static void CollectHitboxFixups(Transform old, Transform bone, float lengthRatio, List<System.Action> fixups)
    {
        Quaternion toBone = Quaternion.Inverse(bone.rotation);
        Quaternion turn = toBone * old.rotation;
        Vector3 oldScale = old.lossyScale;
        Vector3 boneScale = bone.lossyScale;

        System.Func<Vector3, Vector3> point = v => Divide(toBone * old.TransformVector(v), boneScale) * lengthRatio;

        foreach (Collider collider in old.GetComponents<Collider>())
        {
            if (collider is CapsuleCollider capsule)
            {
                Vector3 center = point(capsule.center);
                int direction = DominantAxis(turn * Axis(capsule.direction));
                float height = capsule.height * oldScale[capsule.direction] / boneScale[direction] * lengthRatio;
                float radius = capsule.radius * oldScale[(capsule.direction + 1) % 3] / boneScale[(direction + 1) % 3];

                fixups.Add(() =>
                {
                    capsule.center = center;
                    capsule.direction = direction;
                    capsule.height = height;
                    capsule.radius = radius;
                });
            }
            else if (collider is BoxCollider box)
            {
                Vector3 center = point(box.center);
                Vector3 size = Vector3.zero;

                for (int i = 0; i < 3; i++)
                    size += Abs(turn * Axis(i)) * (box.size[i] * oldScale[i]);

                size = Divide(size, boneScale);
                fixups.Add(() => { box.center = center; box.size = size; });
            }
            else if (collider is SphereCollider sphere)
            {
                Vector3 center = point(sphere.center);
                float radius = sphere.radius * Mathf.Max(oldScale.x, oldScale.y, oldScale.z) / boneScale.x;
                fixups.Add(() => { sphere.center = center; sphere.radius = radius; });
            }
        }

        foreach (CharacterJoint joint in old.GetComponents<CharacterJoint>())
        {
            Vector3 anchor = point(joint.anchor);
            Vector3 axis = (turn * joint.axis).normalized;
            Vector3 swingAxis = (turn * joint.swingAxis).normalized;
            fixups.Add(() => { joint.anchor = anchor; joint.axis = axis; joint.swingAxis = swingAxis; });
        }
    }

    /// <summary>The skinned meshes as one world-space triangle soup, in the pose they are in now.</summary>
    private static void BakeBody(SkinnedMeshRenderer[] skins, List<Vector3> vertices, List<int> triangles)
    {
        Mesh baked = new Mesh();

        foreach (SkinnedMeshRenderer skin in skins)
        {
            skin.BakeMesh(baked, true);
            int offset = vertices.Count;

            foreach (Vector3 vertex in baked.vertices)
                vertices.Add(skin.transform.TransformPoint(vertex));

            foreach (int index in baked.triangles)
                triangles.Add(offset + index);
        }

        Object.DestroyImmediate(baked);
    }

    /// <summary>
    /// A prop with a mesh of its own is one piece, and any meshes below it are further
    /// pieces (the second katana hangs under the first, on the other hip). A prop without
    /// one is a group that moves together (a gun made of several parts).
    /// </summary>
    private static void CollectHolsters(Transform prop, Transform bone, List<Vector3> bodyVertices, List<int> bodyTriangles, List<Holster> holsters)
    {
        if (TryMeasureGap(prop, bone, bodyVertices, bodyTriangles, out _, out _, out _, out float gap, out float lean))
            holsters.Add(new Holster { prop = prop, bone = bone, gap = gap, lean = lean });

        if (prop.GetComponent<MeshFilter>() == null)
            return;

        foreach (Transform child in prop)
            CollectHolsters(child, bone, bodyVertices, bodyTriangles, holsters);
    }

    /// <summary>
    /// Leans the prop until it lies along the new skin the way it lay along the old one
    /// (a waist that narrows more leaves the top of an upright torch in the air), then
    /// slides it until it touches. One that was sunk into the old body stays sunk by as
    /// much.
    /// </summary>
    private static void RestOnBody(Holster holster, List<Vector3> bodyVertices, List<int> bodyTriangles)
    {
        if (!TryMeasureGap(holster.prop, holster.bone, bodyVertices, bodyTriangles, out Vector3 outward, out _, out Vector3 center, out _, out float lean))
            return;

        // Turning about this axis moves what is above the centre outward.
        float tilt = Mathf.Clamp(holster.lean - lean, -MaxHolsterTilt, MaxHolsterTilt);
        holster.prop.RotateAround(center, Vector3.Cross(Vector3.up, outward), tilt);

        // Sliding along the nearest point's bearing can leave another point nearest.
        float target = Mathf.Min(holster.gap, 0f);

        for (int pass = 0; pass < SlidePasses; pass++)
        {
            if (!TryMeasureGap(holster.prop, holster.bone, bodyVertices, bodyTriangles, out _, out Vector3 contact, out _, out float gap, out _) ||
                Mathf.Abs(target - gap) < 0.001f)
                break;

            holster.prop.position += contact * (target - gap);
        }
    }

    /// <summary>
    /// Compares a prop with the skin around the bone it hangs on, over a grid of height
    /// bands and compass bearings from that bone: in each cell, the prop's point nearest
    /// the bone against the skin on the level line through it. A straight "towards the
    /// prop's middle" comparison misreads anything long that runs past the body, like a
    /// sword slung fore and aft.
    /// <paramref name="gap"/> is the smallest distance found (zero is resting on the body,
    /// negative is sunk into it) and <paramref name="contact"/> the bearing it was found on.
    /// <paramref name="lean"/> is how many degrees the prop's top stands off the skin
    /// compared with its bottom.
    /// </summary>
    private static bool TryMeasureGap(Transform prop, Transform bone, List<Vector3> bodyVertices, List<int> bodyTriangles, out Vector3 outward, out Vector3 contact, out Vector3 center, out float gap, out float lean)
    {
        outward = Vector3.zero;
        contact = Vector3.zero;
        center = Vector3.zero;
        gap = float.MaxValue;
        lean = 0f;

        MeshFilter own = prop.GetComponent<MeshFilter>();
        var points = new List<Vector3>();

        foreach (MeshFilter filter in own != null ? new[] { own } : prop.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null)
                continue;

            foreach (Vector3 vertex in filter.sharedMesh.vertices)
                points.Add(filter.transform.TransformPoint(vertex));
        }

        if (points.Count == 0)
            return false;

        Vector3 min = points[0];
        Vector3 max = points[0];

        foreach (Vector3 point in points)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        Vector3 axis = bone.position;
        center = (min + max) * 0.5f;
        outward = center - axis;
        outward.y = 0f;

        if (outward.sqrMagnitude < 0.000001f)
            return false;

        outward.Normalize();

        // The prop's point nearest the bone in each cell, as an offset from the bone.
        float bandHeight = Mathf.Max(max.y - min.y, 0.0001f) / GapBands;
        var nearest = new Dictionary<int, Vector3>();

        foreach (Vector3 point in points)
        {
            Vector3 offset = point - axis;
            float radius = LevelLength(offset);

            if (radius < MinGapRadius)
                continue;

            int band = Mathf.Min((int)((point.y - min.y) / bandHeight), GapBands - 1);
            int bearing = Mathf.FloorToInt(Mathf.Atan2(offset.z, offset.x) * Mathf.Rad2Deg / BearingStep);
            int cell = band * CellsPerBand + bearing + CellsPerBand / 2;

            if (!nearest.TryGetValue(cell, out Vector3 best) || radius < LevelLength(best))
                nearest[cell] = offset;
        }

        float[] bandGap = new float[GapBands];

        for (int band = 0; band < GapBands; band++)
            bandGap[band] = float.MaxValue;

        foreach (KeyValuePair<int, Vector3> cell in nearest)
        {
            Vector3 offset = cell.Value;
            float radius = LevelLength(offset);
            Vector3 bearing = new Vector3(offset.x, 0f, offset.z) / radius;

            // Skin well beyond the prop is an arm, not what the prop rests on.
            Vector3 origin = new Vector3(axis.x, axis.y + offset.y, axis.z);
            float skin = FarthestHit(origin, bearing, radius + SkinSearchMargin, bodyVertices, bodyTriangles);

            if (skin == float.MinValue)
                continue;

            int band = cell.Key / CellsPerBand;
            bandGap[band] = Mathf.Min(bandGap[band], radius - skin);

            if (radius - skin < gap)
            {
                gap = radius - skin;
                contact = bearing;
            }
        }

        // Least squares line through the gap at each height; its slope is the lean.
        int count = 0;
        float sumHeight = 0f, sumGap = 0f, sumHeightGap = 0f, sumHeightSquared = 0f;

        for (int band = 0; band < GapBands; band++)
        {
            if (bandGap[band] == float.MaxValue)
                continue;

            float height = (band + 0.5f) * bandHeight;
            count++;
            sumHeight += height;
            sumGap += bandGap[band];
            sumHeightGap += height * bandGap[band];
            sumHeightSquared += height * height;
        }

        float spread = count * sumHeightSquared - sumHeight * sumHeight;

        if (count >= 3 && spread > 0.000001f)
            lean = Mathf.Atan((count * sumHeightGap - sumHeight * sumGap) / spread) * Mathf.Rad2Deg;

        return count > 0;
    }

    /// <summary>
    /// Distance along a level ray to the last triangle it passes through within
    /// <paramref name="maxDistance"/>, or float.MinValue.
    /// </summary>
    private static float FarthestHit(Vector3 origin, Vector3 direction, float maxDistance, List<Vector3> vertices, List<int> triangles)
    {
        float farthest = float.MinValue;

        for (int i = 0; i < triangles.Count; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];

            // A level ray cannot touch a triangle that is wholly above or below it.
            if ((a.y > origin.y) == (b.y > origin.y) && (a.y > origin.y) == (c.y > origin.y))
                continue;

            // Moller-Trumbore, both faces.
            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 p = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, p);

            if (Mathf.Abs(determinant) < 1e-9f)
                continue;

            Vector3 toOrigin = origin - a;
            float u = Vector3.Dot(toOrigin, p) / determinant;

            if (u < 0f || u > 1f)
                continue;

            Vector3 q = Vector3.Cross(toOrigin, edge1);
            float v = Vector3.Dot(direction, q) / determinant;

            if (v < 0f || u + v > 1f)
                continue;

            float distance = Vector3.Dot(edge2, q) / determinant;

            if (distance > 0f && distance <= maxDistance && distance > farthest)
                farthest = distance;
        }

        return farthest;
    }

    private static float LevelLength(Vector3 v)
    {
        return Mathf.Sqrt(v.x * v.x + v.z * v.z);
    }

    /// <summary>How much longer the new bone is, measured to the next bone down the limb.</summary>
    private static float LengthRatio(Transform old, Transform bone, Dictionary<Transform, Transform> oldOf)
    {
        Transform next = null;

        foreach (Transform child in bone)
        {
            if (!oldOf.ContainsKey(child))
                continue;

            // A bone that branches (hips, chest, hand) has no single length.
            if (next != null)
                return 1f;

            next = child;
        }

        if (next == null)
            return 1f;

        float oldLength = Vector3.Distance(old.position, oldOf[next].position);
        return oldLength > 0.0001f ? Vector3.Distance(bone.position, next.position) / oldLength : 1f;
    }

    private static Transform FindHumanBone(Avatar avatar, HumanBodyBones humanBone, Transform root)
    {
        string humanName = HumanTrait.BoneName[(int)humanBone];

        foreach (HumanBone bone in avatar.humanDescription.human)
        {
            if (bone.humanName != humanName)
                continue;

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == bone.boneName)
                    return t;
            }
        }

        return null;
    }

    /// <summary>Transforms under <paramref name="t"/> (itself included) down a childless chain of empties.</summary>
    private static int CountEmptyChain(Transform t)
    {
        int count = 1;

        while (t.childCount == 1 && t.GetChild(0).GetComponents<Component>().Length == 1)
        {
            t = t.GetChild(0);
            count++;
        }

        return count;
    }

    private static Vector3 Axis(int index)
    {
        Vector3 axis = Vector3.zero;
        axis[index] = 1f;
        return axis;
    }

    private static int DominantAxis(Vector3 v)
    {
        v = Abs(v);
        return v.x >= v.y && v.x >= v.z ? 0 : (v.y >= v.z ? 1 : 2);
    }

    private static Vector3 Abs(Vector3 v)
    {
        return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }

    private static Vector3 Divide(Vector3 a, Vector3 b)
    {
        return new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);
    }
}
