using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace Bozo.ModularCharacters
{

    public class ApplyTags : MonoBehaviour
    {
        public string[] tags;
        private OutfitSystem system;
        private Outfit parentOutfit;
        private void Awake()
        {
            system = GetComponentInParent<OutfitSystem>();
            parentOutfit = GetComponentInParent<Outfit>();
        }

        private void OnEnable()
        {
            if(system)
            {
                //system.AddTags(tags);
            }
        }

        private void OnDisable()
        {
            if (system) 
            {
                //system.RemoveTags(tags);
            }
        }
    }

}