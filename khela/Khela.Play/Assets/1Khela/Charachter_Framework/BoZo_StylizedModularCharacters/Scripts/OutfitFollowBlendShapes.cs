using System;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public class OutfitFollowBlendShapes : MonoBehaviour, IOutfitExtension
    {
        OutfitSystem system;
        SkinnedMeshRenderer[] mesh;
        SkinnedMeshRenderer followTarget;

        [SerializeField] OutfitType follow;
        [SerializeField] List<Vector2> shapes = new List<Vector2>();

        bool initalized;

        private void OnDisable()
        {
            system.OnOutfitChanged -= OnNewSetUpHead;
        }

        private void Init()
        {

        }

        private void OnNewSetUpHead(Outfit outfit)
        {
            if (outfit == null) { return; }
            if (outfit.Type != follow) { return; }
            if (!outfit.skinnedRenderer) { return; }
            followTarget = outfit.skinnedRenderer;
            SetUp();
        }

        private void SetUp()
        {
            mesh = GetComponentsInChildren<SkinnedMeshRenderer>();
            if (mesh == null) return;

            var characterShapeTitle = followTarget.sharedMesh.GetBlendShapeName(0);
            var sort = characterShapeTitle.Split(".");
            if (sort.Length > 1) { characterShapeTitle = sort[0] + "."; }
            else { characterShapeTitle = ""; }
            shapes.Clear();
            foreach (var item in mesh)
            {
                for (int i = 0; i < item.sharedMesh.blendShapeCount; i++)
                {
                    var shapeName = item.sharedMesh.GetBlendShapeName(i);
                    sort = shapeName.Split(".");
                    if (sort.Length > 1) { shapeName = sort[1]; }

                    var shapeIndex = followTarget.sharedMesh.GetBlendShapeIndex(characterShapeTitle + shapeName);

                    if (shapeIndex != -1)
                    {
                        shapes.Add(new Vector2(i, shapeIndex));
                    }
                }
            }
        }

        private void Update()
        {
            if (followTarget == null) return;

            for (int i = 0; i < shapes.Count; i++)
            {
                foreach (var item in mesh)
                {
                    if (item.GetBlendShapeWeight((int)shapes[i].x) == followTarget.GetBlendShapeWeight((int)shapes[i].y)) continue;
                    item.SetBlendShapeWeight((int)shapes[i].x, followTarget.GetBlendShapeWeight((int)shapes[i].y));
                }
            }
        }

        public string GetID()
        {
            return "BlendShapeFollow";
        }

        public string GetUniqueID()
        {
            return name + "BlendShapeFollow";
        }

        public void Initalize(OutfitSystem outfitSystem, Outfit outfit)
        {
            if (initalized) return;
            system = outfitSystem;
            if (system == null) return;
            initalized = true;
            system.OnOutfitChanged += OnNewSetUpHead;
            mesh = outfit.skinnedRenderers;

            var followOutfit = system.GetOutfit(follow);
            if (followOutfit == null) { return; }
            if (followOutfit.skinnedRenderer == null) { return; }
            followTarget = followOutfit.skinnedRenderer;
            SetUp();
        }

        public void Execute(OutfitSystem outfitSystem, Outfit outfit)
        {

        }

        public object GetValue()
        {
            return null;
        }

        public Type GetValueType()
        {
            return null;
        }

    }
}
