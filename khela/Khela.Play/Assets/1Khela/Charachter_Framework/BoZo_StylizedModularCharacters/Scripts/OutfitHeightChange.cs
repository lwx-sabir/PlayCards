using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public class OutfitHeightChange : MonoBehaviour, IOutfitExtension
    {
        OutfitSystem outfitSystem;
        [SerializeField] float HeightOffset;
        [Header("Heel Options")]
        [SerializeField] bool heelEnabled;
        [SerializeField] string animParameter = "HeelHeight";
        [SerializeField] string blendName = "AnimShape_HeelHeight";
        [Range(0,1)][SerializeField] float heelHeight;
        [SerializeField] float heelHeightOffset;

        public void Execute(OutfitSystem outfitSystem, Outfit outfit)
        {

            this.outfitSystem = outfitSystem;
            if (outfitSystem == null) return;
            var animator = outfitSystem.animator;

            if (outfit == null) return;
            if (animator && heelEnabled)
            {
                if (animator.hasRootMotion) return;
                foreach (AnimatorControllerParameter param in animator.parameters)
                {
                    if (param.name == animParameter)
                    {

                        animator.SetFloat(param.name, heelHeight);

                        if (outfit.skinnedRenderer == null) return;
                        var mesh = outfit.skinnedRenderer.sharedMesh;
                        var index = mesh.GetBlendShapeIndex(blendName);
                        if (index == -1) return;
                        //removing nameshape that maya gives
                        var sort = blendName.Split(".");
                        if (sort.Length > 1) { blendName = sort[1]; }
                        outfit.skinnedRenderer.SetBlendShapeWeight(index, heelHeight * 100);
                    }
                }
            }

            outfitSystem.AddHeightSource(gameObject, heelHeightOffset);
            outfitSystem.OnRigChanged += OnRigChanged;
        }

        public void OnRigChanged(SkinnedMeshRenderer smr)
        {
            if (!outfitSystem) return;
            foreach (AnimatorControllerParameter param in outfitSystem.animator.parameters)
            {
                if (param.name == animParameter)
                {

                    outfitSystem.animator.SetFloat(param.name, heelHeight);
                }
            }
        }

        private void OnDestroy()
        {
            if (outfitSystem)
            {
                outfitSystem.OnRigChanged -= OnRigChanged;
                outfitSystem.RemoveHeightSource(gameObject, HeightOffset);
                outfitSystem.SetHeight();
            }

            
        }

        public string GetID()
        {
            return "HeightChange";
        }

        public void Initalize(OutfitSystem outfitSystem, Outfit outfit)
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
