using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Bozo.ModularCharacters
{
    public class DecalBaker : MonoBehaviour
    {
        public OutfitSystem system;
        public Outfit outfit;
        public SkinnedMeshRenderer smr;

        public Transform anchorProjector;
        public Transform axisProjector;
        public Transform oriProjector;
        public Transform decalProjector;

        public Material material;

        public List<Mesh> bakedMeshes = new();

        public Vector3 postion;
        public Quaternion rotation;
        public Vector3 scale;

        public Dictionary<string, Transform> bones;

        public List<DecalData> decalDatas = new List<DecalData>();

        public bool debug = true;

        public UnityAction<Outfit> onDecalChanged;

        public bool initalized;

        public void Init()
        {
            system = GetComponentInParent<OutfitSystem>();
            outfit = GetComponent<Outfit>();
            bones = system.GetBones();

            smr = outfit.skinnedRenderer;

            if (outfit == null || system == null) return;

            anchorProjector = (new GameObject("Anchor" + decalProjector).transform);
            axisProjector = (new GameObject("Anchor Axis" + axisProjector).transform);

            oriProjector = (new GameObject("Projector Orientation" + oriProjector).transform);
            decalProjector = (new GameObject("Projector Decal" + decalProjector).transform);

            decalProjector.parent = oriProjector;
            oriProjector.parent = axisProjector;
            axisProjector.parent = anchorProjector;
            anchorProjector.parent = transform;

            material = new Material(Shader.Find("Hidden/BoZo_DecalProjector"));

            material.SetTexture("_SourceTex", outfit.material.mainTexture);

            while (bakedMeshes.Count < outfit.skinnedRenderers.Length)
            {
                Mesh mesh = new Mesh();
                mesh.name = "Runtime Decal Bake Mesh";
                bakedMeshes.Add(mesh);
            }

            initalized = true;

        }

        public void BakeDecal(RenderTexture rt, RenderTexture crt, DecalData data)
        {
            if (!initalized) { Init(); }

            SetPosition(data);

            Matrix4x4 decalWorldToLocal = decalProjector.worldToLocalMatrix;
            material.SetTexture("_BaseTex", rt);
            material.SetMatrix("_DecalWorldToLocal", decalWorldToLocal);
            material.SetTexture("_DecalTex", data.decalTexture);
            material.SetColor("_Color", data.color);

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = rt;

            material.SetPass(0);

            var smrs = outfit.skinnedRenderers;
            for (int i = 0; i < smrs.Length; i++)
            {
                SkinnedMeshRenderer smr = smrs[i];

                if (smr == null || smr.sharedMesh == null)
                {
                    continue;
                }

                Mesh bakedMesh = bakedMeshes[i];

                smr.BakeMesh(bakedMesh);

                Graphics.DrawMeshNow(
                    bakedMesh,
                    smr.transform.localToWorldMatrix
                );
            }

            Graphics.Blit(rt, crt);
            RenderTexture.active = previousActive;
            
        }

        private void OnDestroy()
        {
            if (anchorProjector == null) return;
            Destroy(anchorProjector.gameObject);
            foreach (var mesh in bakedMeshes)
            {
                if (mesh) Destroy(mesh);
            }
        }

        public void SetPosition(DecalData data)
        {
            SetParent(data.parent);
            

            if (data.parent.EndsWith("_r")) data.pos.z = -data.pos.z;
            decalProjector.localPosition = new Vector3(data.pos.x, data.pos.y, data.pos.z);
            decalProjector.localRotation = Quaternion.AngleAxis(data.roll, Vector3.forward);
            decalProjector.localScale = new Vector3(data.scale, data.scale, 0.15f);

            var dir = Vector3.one;
            if (data.parent == "head") dir = new Vector3(data.pitch, data.yaw);
            else dir = new Vector3(data.yaw, 0, data.pitch);
            axisProjector.localRotation = Quaternion.Euler(dir);
        }

        public void SetParent(string boneName)
        {
            anchorProjector.localScale = Vector3.one;

            oriProjector.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            axisProjector.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            decalProjector.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            anchorProjector.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);


            Transform bone = bones[boneName];
            anchorProjector.parent = bone;
            anchorProjector.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            anchorProjector.localScale = Vector3.one;

            Vector3 direction = decalProjector.up;
            if (boneName.EndsWith("_r")) direction = -direction;
            if (boneName == "head") direction = -Vector3.forward;

            oriProjector.position = bone.position + (-direction * 0.05f);
            oriProjector.LookAt(anchorProjector);
        }

        public void SetBoundsPosition(Vector3 pos)
        {
            foreach (var item in outfit.skinnedRenderers)
            {
                var bounds = item.localBounds;
                bounds.center = new Vector3(0, 0, pos.z * 2);
                bounds.extents += new Vector3(0.5f, 0.5f, 0.5f);
                item.localBounds = bounds;
            }
        }

    }
}