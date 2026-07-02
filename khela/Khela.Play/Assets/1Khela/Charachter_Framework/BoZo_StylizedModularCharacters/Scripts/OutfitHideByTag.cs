using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{
    public class OutfitHideByTag : MonoBehaviour , IOutfitExtension
    {
        OutfitSystem system;
        [SerializeField] HideSettings[] settings;

        public void Execute(OutfitSystem outfitSystem, Outfit outfit)
        {

        }

        public string GetID()
        {
            return "OutfitHideByTag";
        }

        public object GetValue()
        {
            return null;
        }

        public Type GetValueType()
        {
            return null;
        }

        public void Initalize(OutfitSystem outfitSystem, Outfit outfit)
        {
            system = GetComponentInParent<OutfitSystem>(true);
            system.OnTagsChanged += SetHide;
        }

        public void OnDestroy()
        {
            if(system) system.OnTagsChanged -= SetHide;
        }

        private void SetHide(List<string> tags)
        {
            foreach (var item in settings)
            {
                if (item.renderer == null) continue;
                if (system.ContainsTag(item.tag))
                {
                    item.renderer.gameObject.SetActive(false);
                }
                else
                {
                    item.renderer.gameObject.SetActive(true);
                }
            }
        }

        [System.Serializable]
        private struct HideSettings
        {
            public SkinnedMeshRenderer renderer;
            public string tag;
        } 
    }
}
